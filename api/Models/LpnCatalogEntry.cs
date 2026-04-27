namespace NSL.Api.Models;

/// <summary>
/// One row of the lpn_catalog table — a single Amazon LPN unit with the
/// product metadata extracted from a B-Stock manifest XLSX.
/// </summary>
public sealed class LpnCatalogEntry
{
    public string Lpn { get; set; } = string.Empty;
    public string? Asin { get; set; }
    public string? Upc { get; set; }
    public string? Ean { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public decimal? Msrp { get; set; }
    public decimal? UnitCost { get; set; }
    public string? Condition { get; set; }
    public int? QtyInManifest { get; set; }
    public string? SellerCategory { get; set; }
    public string? ProductClass { get; set; }
    public string? OrderNumber { get; set; }
    public string? PalletId { get; set; }
    public string? LotId { get; set; }
    public string SourceManifest { get; set; } = string.Empty;
    public string? SourcePalletRef { get; set; }
}

public sealed class ManifestImportResult
{
    public Guid ImportId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    public List<string> UnmappedColumns { get; set; } = new();
    public bool DuplicateOfPriorImport { get; set; }
    public string? PriorImportId { get; set; }
}
