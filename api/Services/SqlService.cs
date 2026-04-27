using Azure.Core;
using Azure.Identity;
using Dapper;
using Microsoft.Data.SqlClient;
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
        var conn = new SqlConnection(_connectionString);
        // If the connection string already specifies Authentication=Active Directory Default,
        // SqlClient will handle token acquisition; otherwise we set AccessToken explicitly.
        if (!_connectionString.Contains("Authentication=", StringComparison.OrdinalIgnoreCase))
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://database.windows.net/.default" }), ct);
            conn.AccessToken = token.Token;
        }
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
    /// Bulk-upsert a batch of LpnCatalogEntry rows. Newer imports win on conflict.
    /// Returns (rowsInserted, rowsUpdated).
    /// </summary>
    public async Task<(int Inserted, int Updated)> UpsertLpnCatalogAsync(
        IEnumerable<LpnCatalogEntry> entries, CancellationToken ct = default)
    {
        const string sql = @"
MERGE dbo.lpn_catalog AS target
USING (SELECT @Lpn AS lpn) AS source
ON target.lpn = source.lpn
WHEN MATCHED THEN
  UPDATE SET asin = @Asin, upc = @Upc, ean = @Ean, title = @Title,
             description = @Description, brand = @Brand, category = @Category,
             subcategory = @Subcategory, msrp = @Msrp, unit_cost = @UnitCost,
             condition = @Condition, qty_in_manifest = @QtyInManifest,
             seller_category = @SellerCategory, product_class = @ProductClass,
             order_number = @OrderNumber, pallet_id = @PalletId, lot_id = @LotId,
             source_manifest = @SourceManifest, source_pallet_ref = @SourcePalletRef,
             last_seen_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT (lpn, asin, upc, ean, title, description, brand, category, subcategory,
          msrp, unit_cost, condition, qty_in_manifest, seller_category,
          product_class, order_number, pallet_id, lot_id, source_manifest, source_pallet_ref)
  VALUES (@Lpn, @Asin, @Upc, @Ean, @Title, @Description, @Brand, @Category, @Subcategory,
          @Msrp, @UnitCost, @Condition, @QtyInManifest, @SellerCategory,
          @ProductClass, @OrderNumber, @PalletId, @LotId, @SourceManifest, @SourcePalletRef)
OUTPUT $action;";

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        int inserted = 0, updated = 0;
        foreach (var batch in entries.Chunk(200))
        {
            foreach (var e in batch)
            {
                var action = await conn.QuerySingleOrDefaultAsync<string>(sql, e, tx);
                if (action == "INSERT") inserted++;
                else if (action == "UPDATE") updated++;
            }
        }
        await tx.CommitAsync(ct);
        _log.LogInformation("LPN catalog upsert: {Ins} inserted, {Upd} updated", inserted, updated);
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
