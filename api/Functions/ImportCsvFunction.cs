using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using System.Text;

namespace NSL.Api.Functions;

/// <summary>
/// POST /api/import-csv  (#8)
///
/// Accepts a CSV (request body) and upserts rows into dbo.lpn_catalog so non-
/// Amazon / barcode-less goods can be added or adjusted without an XLSX
/// manifest. Header names are matched flexibly (case-insensitive, common
/// synonyms). A row keyed by lpn/sku updates that catalog row; a row with no
/// key gets a synthetic "NSL-xxxxxxxx" lpn so it inserts as new inventory.
///
/// Recognised columns (any subset; title or a key recommended):
///   lpn|sku|id, upc|barcode, asin, title|name|product, description|desc|details,
///   brand|manufacturer, category|cat, condition|cond, qty|quantity|count,
///   msrp|retail|list, cost|unit_cost, price|wholesale|wholesale_price
///
/// Header:  x-filename (optional) — recorded as source_manifest.
/// </summary>
public sealed class ImportCsvFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<ImportCsvFunction> _log;

    public ImportCsvFunction(SqlService sql, ILogger<ImportCsvFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("ImportCsv")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "import-csv")] HttpRequest req,
        CancellationToken ct)
    {
        var filename = req.Headers["x-filename"].FirstOrDefault() ?? "csv-upload.csv";

        string text;
        using (var sr = new StreamReader(req.Body, Encoding.UTF8)) text = await sr.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return new BadRequestObjectResult(new { error = "Empty body — POST the CSV text as the request body." });

        var rows = ParseCsv(text);
        if (rows.Count < 2)
            return new BadRequestObjectResult(new { error = "CSV needs a header row and at least one data row." });

        // Map header → column index using synonyms.
        var header = rows[0];
        int Col(params string[] names)
        {
            for (int i = 0; i < header.Count; i++)
            {
                var h = header[i].Trim().ToLowerInvariant().Replace(" ", "_");
                if (names.Contains(h)) return i;
            }
            return -1;
        }
        int cLpn   = Col("lpn", "sku", "id");
        int cUpc   = Col("upc", "barcode", "gtin");
        int cAsin  = Col("asin");
        int cTitle = Col("title", "name", "product", "product_name", "item");
        int cDesc  = Col("description", "desc", "details");
        int cBrand = Col("brand", "manufacturer");
        int cCat   = Col("category", "cat");
        int cCond  = Col("condition", "cond");
        int cQty   = Col("qty", "quantity", "count");
        int cMsrp  = Col("msrp", "retail", "list", "unit_retail");
        int cCost  = Col("cost", "unit_cost", "our_cost");
        int cPrice = Col("price", "wholesale", "wholesale_price", "sell_price");

        if (cTitle < 0 && cLpn < 0 && cUpc < 0)
            return new BadRequestObjectResult(new { error = "CSV must have at least a title, lpn/sku, or upc column." });

        string? Get(List<string> r, int idx) =>
            idx >= 0 && idx < r.Count && !string.IsNullOrWhiteSpace(r[idx]) ? r[idx].Trim() : null;
        decimal? Money(List<string> r, int idx)
        {
            var v = Get(r, idx);
            if (v == null) return null;
            v = v.Replace("$", "").Replace(",", "").Trim();
            return decimal.TryParse(v, out var d) ? d : (decimal?)null;
        }
        int? Int(List<string> r, int idx)
        {
            var v = Get(r, idx);
            return int.TryParse(v, out var n) ? n : (int?)null;
        }

        var entries = new List<SqlService.CsvCatalogRow>();
        int skipped = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;

            var lpn = Get(r, cLpn);
            if (string.IsNullOrWhiteSpace(lpn))
                lpn = "NSL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

            var title = Get(r, cTitle);
            if (title == null && Get(r, cUpc) == null && Get(r, cLpn) == null) { skipped++; continue; }

            entries.Add(new SqlService.CsvCatalogRow(
                Lpn: lpn[..Math.Min(lpn.Length, 40)],
                Upc: Get(r, cUpc), Asin: Get(r, cAsin),
                Title: title, Description: Get(r, cDesc),
                Brand: Get(r, cBrand), Category: Get(r, cCat),
                Condition: Get(r, cCond), Qty: Int(r, cQty),
                Msrp: Money(r, cMsrp), UnitCost: Money(r, cCost),
                WholesalePrice: Money(r, cPrice), SourceManifest: filename));
        }

        if (entries.Count == 0)
            return new BadRequestObjectResult(new { error = "No usable rows found in CSV.", skipped });

        var (inserted, updated) = await _sql.UpsertCatalogCsvAsync(entries, ct);
        _log.LogInformation("ImportCsv {File}: {Ins} inserted, {Upd} updated, {Skip} skipped",
            filename, inserted, updated, skipped);

        return new OkObjectResult(new
        {
            filename,
            rows = entries.Count,
            inserted,
            updated,
            skipped
        });
    }

    /// <summary>
    /// Minimal RFC-4180-ish CSV parser: handles quoted fields, embedded commas,
    /// doubled quotes, and CRLF/LF line endings. Good enough for hand-made
    /// spreadsheets exported from Excel / Google Sheets.
    /// </summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        rows.Add(row); row = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }
        }
        // last field / row (file may not end with a newline)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
