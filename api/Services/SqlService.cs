using Azure.Core;
using Azure.Identity;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSL.Api.Models;

namespace NSL.Api.Services;

/// <summary>
/// Thin Dapper-based wrapper around SqlConnection with managed-identity auth
/// to Azure SQL. Connection string comes from the SqlConnectionString app
/// setting; auth uses Active Directory Default which picks up the SWA managed
/// Function's identity at runtime, or developer credentials locally.
/// </summary>
public sealed class SqlService
{
    private readonly string _connectionString;
    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<SqlService> _log;

    public SqlService(IConfiguration config, ILogger<SqlService> log)
    {
        _connectionString = config["SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString app setting missing.");
        _credential = new DefaultAzureCredential();
        _log = log;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        // SqlClient handles auth natively based on the connection string —
        // SQL auth (User ID + Password), Entra Default (Authentication=Active
        // Directory Default), Entra Password, etc. We do not need to manage
        // AccessToken here. SWA managed Functions doesn't expose IMDS, so we
        // ship today with SQL auth credentials in the connection string.
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Look up a previous import by SHA-256. Returns the prior import id if found.
    /// </summary>
    public async Task<Guid?> FindImportBySha256Async(string sha256, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM dbo.manifest_imports WHERE sha256 = @Sha",
            new { Sha = sha256 });
    }

    /// <summary>
    /// Bulk-upsert LpnCatalogEntry rows via SqlBulkCopy into a staging temp
    /// table + single MERGE into dbo.lpn_catalog. Newer imports win on
    /// conflict. Returns (rowsInserted, rowsUpdated).
    ///
    /// Performance: 2,000 rows in ~3s vs ~100s for the row-at-a-time pattern.
    /// </summary>
    public async Task<(int Inserted, int Updated)> UpsertLpnCatalogAsync(
        IEnumerable<LpnCatalogEntry> entries, CancellationToken ct = default)
    {
        var list = entries.ToList();
        if (list.Count == 0) return (0, 0);

        await using var conn = await OpenAsync(ct);

        // 1. Create staging temp table on this connection
        const string createStaging = @"
IF OBJECT_ID('tempdb..#lpn_staging') IS NOT NULL DROP TABLE #lpn_staging;
CREATE TABLE #lpn_staging (
    lpn varchar(40) NOT NULL,
    asin varchar(20) NULL, upc varchar(20) NULL, ean varchar(20) NULL,
    title nvarchar(500) NULL, description nvarchar(max) NULL,
    brand nvarchar(200) NULL, category nvarchar(200) NULL, subcategory nvarchar(200) NULL,
    msrp decimal(12,2) NULL, unit_cost decimal(12,4) NULL,
    condition varchar(40) NULL, qty_in_manifest int NULL,
    seller_category nvarchar(200) NULL, product_class nvarchar(200) NULL,
    order_number nvarchar(100) NULL, pallet_id nvarchar(200) NULL, lot_id nvarchar(200) NULL,
    source_manifest nvarchar(500) NOT NULL, source_pallet_ref nvarchar(200) NULL
);";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = createStaging;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Bulk-copy rows into staging
        var dt = new System.Data.DataTable();
        dt.Columns.Add("lpn", typeof(string));
        dt.Columns.Add("asin", typeof(string));
        dt.Columns.Add("upc", typeof(string));
        dt.Columns.Add("ean", typeof(string));
        dt.Columns.Add("title", typeof(string));
        dt.Columns.Add("description", typeof(string));
        dt.Columns.Add("brand", typeof(string));
        dt.Columns.Add("category", typeof(string));
        dt.Columns.Add("subcategory", typeof(string));
        dt.Columns.Add("msrp", typeof(decimal));
        dt.Columns.Add("unit_cost", typeof(decimal));
        dt.Columns.Add("condition", typeof(string));
        dt.Columns.Add("qty_in_manifest", typeof(int));
        dt.Columns.Add("seller_category", typeof(string));
        dt.Columns.Add("product_class", typeof(string));
        dt.Columns.Add("order_number", typeof(string));
        dt.Columns.Add("pallet_id", typeof(string));
        dt.Columns.Add("lot_id", typeof(string));
        dt.Columns.Add("source_manifest", typeof(string));
        dt.Columns.Add("source_pallet_ref", typeof(string));

        foreach (var e in list)
        {
            dt.Rows.Add(
                e.Lpn, (object?)e.Asin ?? DBNull.Value, (object?)e.Upc ?? DBNull.Value,
                (object?)e.Ean ?? DBNull.Value, (object?)e.Title ?? DBNull.Value,
                (object?)e.Description ?? DBNull.Value, (object?)e.Brand ?? DBNull.Value,
                (object?)e.Category ?? DBNull.Value, (object?)e.Subcategory ?? DBNull.Value,
                (object?)e.Msrp ?? DBNull.Value, (object?)e.UnitCost ?? DBNull.Value,
                (object?)e.Condition ?? DBNull.Value, (object?)e.QtyInManifest ?? DBNull.Value,
                (object?)e.SellerCategory ?? DBNull.Value, (object?)e.ProductClass ?? DBNull.Value,
                (object?)e.OrderNumber ?? DBNull.Value, (object?)e.PalletId ?? DBNull.Value,
                (object?)e.LotId ?? DBNull.Value, e.SourceManifest,
                (object?)e.SourcePalletRef ?? DBNull.Value);
        }

        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#lpn_staging", BulkCopyTimeout = 60 })
        {
            foreach (System.Data.DataColumn c in dt.Columns)
                bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }

        // 3. Single MERGE from staging into target. OUTPUT clause counts inserts vs updates.
        const string merge = @"
DECLARE @actions TABLE (action varchar(10));
MERGE dbo.lpn_catalog AS t
USING #lpn_staging AS s ON t.lpn = s.lpn
WHEN MATCHED THEN UPDATE SET
    asin = s.asin, upc = s.upc, ean = s.ean, title = s.title,
    description = s.description, brand = s.brand, category = s.category,
    subcategory = s.subcategory, msrp = s.msrp, unit_cost = s.unit_cost,
    condition = s.condition, qty_in_manifest = s.qty_in_manifest,
    seller_category = s.seller_category, product_class = s.product_class,
    order_number = s.order_number, pallet_id = s.pallet_id, lot_id = s.lot_id,
    source_manifest = s.source_manifest, source_pallet_ref = s.source_pallet_ref,
    last_seen_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (lpn, asin, upc, ean, title, description, brand, category, subcategory,
     msrp, unit_cost, condition, qty_in_manifest, seller_category, product_class,
     order_number, pallet_id, lot_id, source_manifest, source_pallet_ref)
VALUES
    (s.lpn, s.asin, s.upc, s.ean, s.title, s.description, s.brand, s.category, s.subcategory,
     s.msrp, s.unit_cost, s.condition, s.qty_in_manifest, s.seller_category, s.product_class,
     s.order_number, s.pallet_id, s.lot_id, s.source_manifest, s.source_pallet_ref)
OUTPUT $action INTO @actions;
SELECT
    SUM(CASE WHEN action = 'INSERT' THEN 1 ELSE 0 END) AS inserted,
    SUM(CASE WHEN action = 'UPDATE' THEN 1 ELSE 0 END) AS updated
FROM @actions;";

        int inserted = 0, updated = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = merge;
            cmd.CommandTimeout = 120;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                inserted = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                updated  = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            }
        }

        _log.LogInformation("LPN catalog upsert: {Ins} inserted, {Upd} updated (bulk)", inserted, updated);
        return (inserted, updated);
    }

    /// <summary>
    /// Insert a manifest_imports audit row.
    /// </summary>
    public async Task<Guid> InsertManifestImportAsync(
        string filename, string sha256, string? palletReference, string? orderNumber,
        int rowCount, int rowsInserted, int rowsUpdated, int rowsSkipped,
        string unmappedColumnsJson, string importedBy, string? archiveBlobUrl,
        CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO dbo.manifest_imports
  (id, filename, sha256, pallet_reference, order_number, row_count,
   rows_inserted, rows_updated, rows_skipped, unmapped_columns, imported_by, archive_blob_url)
VALUES
  (@Id, @Filename, @Sha256, @PalletReference, @OrderNumber, @RowCount,
   @RowsInserted, @RowsUpdated, @RowsSkipped, @UnmappedColumns, @ImportedBy, @ArchiveBlobUrl);";

        var id = Guid.NewGuid();
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(sql, new
        {
            Id = id, Filename = filename, Sha256 = sha256,
            PalletReference = palletReference, OrderNumber = orderNumber,
            RowCount = rowCount, RowsInserted = rowsInserted, RowsUpdated = rowsUpdated,
            RowsSkipped = rowsSkipped, UnmappedColumns = unmappedColumnsJson,
            ImportedBy = importedBy, ArchiveBlobUrl = archiveBlobUrl
        });
        return id;
    }
}
