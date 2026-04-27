using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using NSL.Api.Models;
using System.Globalization;
using System.Security.Cryptography;

namespace NSL.Api.Services;

/// <summary>
/// Parses an Amazon B-Stock liquidation manifest XLSX into LpnCatalogEntry rows.
///
/// Handles column-name variation across manifests by matching headers loosely
/// against a list of synonyms. Records unmapped headers so the importer can
/// surface them for review.
/// </summary>
public sealed class ManifestParser
{
    private readonly ILogger<ManifestParser> _log;

    public ManifestParser(ILogger<ManifestParser> log) => _log = log;

    private static readonly Dictionary<string, string[]> ColumnSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lpn"]            = new[] { "lpn", "license plate", "license plate number", "item id", "pallet id", "lpn id" },
        ["Asin"]           = new[] { "asin", "asin3" },
        ["Upc"]            = new[] { "upc", "upc1", "upc code" },
        ["Ean"]            = new[] { "ean" },
        ["Title"]          = new[] { "item description", "title", "product name", "description", "asin title" },
        ["Brand"]          = new[] { "brand", "manufacturer" },
        ["SellerCategory"] = new[] { "seller category" },
        ["Condition"]      = new[] { "condition" },
        ["OrderNumber"]    = new[] { "order #", "order number", "order id", "order_id" },
        ["Qty"]            = new[] { "qty", "quantity", "units" },
        ["Msrp"]           = new[] { "unit retail", "msrp", "retail price", "unit msrp" },
        ["UnitCost"]       = new[] { "unit cost", "unit cost2", "cost per unit", "wholesale price", "wholesale price3" },
        ["ProductClass"]   = new[] { "product class", "gl description" },
        ["Subcategory"]    = new[] { "subcategory", "subcategory2", "subcategory3" },
        ["PalletId"]       = new[] { "pallet id", "pallet id2" },
        ["LotId"]          = new[] { "lot id", "lot id4" }
    };

    public sealed class ParseResult
    {
        public List<LpnCatalogEntry> Entries { get; set; } = new();
        public List<string> UnmappedColumns { get; set; } = new();
        public string? OrderNumber { get; set; }
        public string? PalletReference { get; set; }
    }

    public ParseResult Parse(Stream xlsxStream, string sourceFilename)
    {
        using var workbook = new XLWorkbook(xlsxStream);
        var sheet = workbook.Worksheets.FirstOrDefault(s =>
            string.Equals(s.Name, "Manifest", StringComparison.OrdinalIgnoreCase))
            ?? workbook.Worksheets.FirstOrDefault(s =>
                s.RangeUsed()?.RowsUsed().Skip(1).Any() == true)
            ?? throw new InvalidOperationException("Workbook contains no usable sheet.");

        _log.LogInformation("Parsing sheet '{Sheet}' ({Rows} rows)", sheet.Name, sheet.RowsUsed().Count());

        var headerRow = sheet.Row(1);
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        // Map XLSX column index -> our canonical field name (or null if unmapped)
        var colToField = new Dictionary<int, string>();
        var unmapped = new List<string>();
        for (int c = 1; c <= lastCol; c++)
        {
            var headerText = headerRow.Cell(c).GetString().Trim();
            if (string.IsNullOrWhiteSpace(headerText)) continue;

            var match = ColumnSynonyms.FirstOrDefault(kv =>
                kv.Value.Contains(headerText, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key) && !colToField.ContainsValue(match.Key))
            {
                colToField[c] = match.Key;
            }
            else if (string.IsNullOrEmpty(match.Key))
            {
                unmapped.Add(headerText);
            }
        }

        // Require at least Lpn + Title to consider a manifest parseable
        if (!colToField.Values.Contains("Lpn") || !colToField.Values.Contains("Title"))
            throw new InvalidOperationException(
                $"Manifest missing required columns. Need at least LPN + Title. Found: {string.Join(", ", colToField.Values)}");

        var entries = new List<LpnCatalogEntry>();
        string? orderNumber = null;
        string? palletRef = null;

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var entry = new LpnCatalogEntry { SourceManifest = sourceFilename };

            foreach (var (col, field) in colToField)
            {
                var cell = row.Cell(col);
                if (cell.IsEmpty()) continue;

                switch (field)
                {
                    case "Lpn":            entry.Lpn = cell.GetString().Trim(); break;
                    case "Asin":           entry.Asin = cell.GetString().Trim(); break;
                    case "Upc":            entry.Upc = NormalizeBarcode(cell.GetString()); break;
                    case "Ean":            entry.Ean = NormalizeBarcode(cell.GetString()); break;
                    case "Title":          entry.Title = TrimToMax(cell.GetString(), 500); break;
                    case "Brand":          entry.Brand = TrimToMax(cell.GetString(), 200); break;
                    case "SellerCategory": entry.SellerCategory = TrimToMax(cell.GetString(), 200); break;
                    case "Condition":      entry.Condition = TrimToMax(cell.GetString(), 40); break;
                    case "OrderNumber":    entry.OrderNumber = TrimToMax(cell.GetString(), 100); orderNumber ??= entry.OrderNumber; break;
                    case "Qty":            entry.QtyInManifest = ParseInt(cell); break;
                    case "Msrp":           entry.Msrp = ParseDecimal(cell); break;
                    case "UnitCost":       entry.UnitCost = ParseDecimal(cell); break;
                    case "ProductClass":   entry.ProductClass = TrimToMax(cell.GetString(), 200); break;
                    case "Subcategory":    entry.Subcategory = TrimToMax(cell.GetString(), 200); break;
                    case "PalletId":       entry.PalletId = TrimToMax(cell.GetString(), 200); palletRef ??= entry.PalletId; break;
                    case "LotId":          entry.LotId = TrimToMax(cell.GetString(), 200); break;
                }
            }

            if (string.IsNullOrWhiteSpace(entry.Lpn)) continue;
            entries.Add(entry);
        }

        _log.LogInformation("Parsed {Count} LPN entries; {Unmapped} unmapped columns", entries.Count, unmapped.Count);

        return new ParseResult
        {
            Entries = entries,
            UnmappedColumns = unmapped,
            OrderNumber = orderNumber,
            PalletReference = palletRef
        };
    }

    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeBarcode(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        // Drop trailing ".0" from numeric-typed cells that Excel coerces
        if (trimmed.EndsWith(".0", StringComparison.Ordinal)) trimmed = trimmed[..^2];
        return trimmed.Length > 20 ? trimmed[..20] : trimmed;
    }

    private static string? TrimToMax(string raw, int max)
    {
        var t = raw.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > max ? t[..max] : t;
    }

    private static int? ParseInt(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        if (int.TryParse(cell.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    private static decimal? ParseDecimal(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number) return Convert.ToDecimal(cell.GetDouble());
        if (decimal.TryParse(cell.GetString(), NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
}
