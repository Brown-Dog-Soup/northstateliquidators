using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace NSL.Api.Functions;

/// <summary>
/// Excel exports. Buyers ask for manifests; staff want the whole floor in a
/// spreadsheet. Everything here is read-only and rendered with ClosedXML
/// (already a dependency for manifest import).
///
///   GET /api/pallets/{id}/manifest          — staff manifest for one box (includes cost)
///   GET /api/public/pallets/{id}/manifest   — buyer-safe manifest (no cost / wholesale)
///   GET /api/inventory/export               — every box + every item, two sheets
///                                             ?includeArchived=true to add archived boxes
///
/// Staff routes are behind SWA auth (staticwebapp.config.json: /api/* requires
/// authenticated). The public route only serves boxes present in
/// v_public_pallets, same rule as the JSON endpoint the modal already uses.
/// </summary>
public sealed class ExportFunction
{
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Money = "$#,##0.00";

    private readonly SqlService _sql;
    private readonly ILogger<ExportFunction> _log;

    public ExportFunction(SqlService sql, ILogger<ExportFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    // ------------------------------------------------------------------
    // Staff: one box, full detail
    // ------------------------------------------------------------------
    [Function("ExportPalletManifest")]
    public async Task<IActionResult> PalletManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}/manifest")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var pallet = await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM dbo.v_pallets WHERE manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();
        var p = (IDictionary<string, object?>)pallet;

        var items = (await conn.QueryAsync(PalletsFunction.ItemsWithCatalogSql, new { id }))
            .Cast<IDictionary<string, object?>>().ToList();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Manifest");

        // Header block
        int r = 1;
        ws.Cell(r, 1).Value = "North State Liquidators — Box Manifest (internal)";
        ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 1).Style.Font.FontSize = 14;
        r += 2;
        r = KeyVal(ws, r, "Box #",         p.Get("pallet_number"));
        r = KeyVal(ws, r, "Name",          p.Get("display_name"));
        r = KeyVal(ws, r, "Category",      p.Get("category"));
        r = KeyVal(ws, r, "Status",        p.Get("publish_state") ?? p.Get("status"));
        r = KeyVal(ws, r, "Sell mode",     p.Get("sell_mode"));
        r = KeyVal(ws, r, "Received",      p.Get("received_date"));
        r = KeyVal(ws, r, "Source / ref",  Join(p.Get("source"), p.Get("pallet_reference")));
        r = KeyVal(ws, r, "Line items",    p.Get("item_count"));
        r = KeyVal(ws, r, "Units",         p.Get("unit_count"));
        r = KeyVal(ws, r, "Total MSRP",    p.Get("total_msrp"), Money);
        r = KeyVal(ws, r, "Total cost",    p.Get("total_cost_units") ?? p.Get("total_cost"), Money);
        r = KeyVal(ws, r, "Total wholesale", p.Get("total_wholesale"), Money);
        r = KeyVal(ws, r, "List price",    p.Get("list_price"), Money);
        r = KeyVal(ws, r, "Sale price",    p.Get("sale_price"), Money);
        r = KeyVal(ws, r, "Exported",      DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        r++;

        // Item table
        var cols = new (string Header, string Key, string? Fmt)[]
        {
            ("LPN", "lpn", null), ("UPC", "upc", null), ("ASIN", "asin", null),
            ("Title", "title", null), ("Brand", "brand", null),
            ("Category", "category", null), ("Seller category", "seller_category", null),
            ("Condition", "condition", null), ("Qty", "qty", "0"),
            ("Unit MSRP", "est_msrp", Money), ("Ext. MSRP", "__ext_msrp", Money),
            ("Est. resale", "est_resale", Money),
            ("Unit cost", "unit_cost", Money), ("Ext. cost", "__ext_cost", Money),
            ("Wholesale", "wholesale_price", Money),
            ("Notes", "notes", null), ("Scanned", "created_at", "yyyy-mm-dd hh:mm"),
        };
        foreach (var it in items)
        {
            var qty = ToDec(it.Get("qty")) ?? 1m;
            it["__ext_msrp"] = Mul(ToDec(it.Get("est_msrp")), qty);
            it["__ext_cost"] = Mul(ToDec(it.Get("unit_cost")), qty);
        }
        WriteTable(ws, r, cols, items);

        var name = $"NSL-Box-{p.Get("pallet_number")}-{Slug(p.Get("display_name")?.ToString())}.xlsx";
        _log.LogInformation("Export staff manifest {Id} ({Rows} items)", id, items.Count);
        return File(wb, name);
    }

    // ------------------------------------------------------------------
    // Public: one box, buyer-safe columns only
    // ------------------------------------------------------------------
    [Function("ExportPublicManifest")]
    public async Task<IActionResult> PublicManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/pallets/{id}/manifest")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var pallet = await conn.QueryFirstOrDefaultAsync(@"
SELECT manifest_id, pallet_number, display_name, category, publish_state,
       item_count, unit_count, total_msrp, ask_price, is_sold, public_description
FROM dbo.v_public_pallets WHERE manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();   // not public → don't leak it
        var p = (IDictionary<string, object?>)pallet;

        // Margin-safe column list — NO unit_cost / wholesale_price / notes / LPN.
        var items = (await conn.QueryAsync(@"
SELECT title, brand, category, condition, qty, est_msrp
FROM dbo.line_items
WHERE manifest_id = @id
ORDER BY CASE WHEN est_msrp IS NULL THEN 1 ELSE 0 END, est_msrp DESC, created_at DESC",
            new { id })).Cast<IDictionary<string, object?>>().ToList();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Manifest");

        int r = 1;
        ws.Cell(r, 1).Value = "North State Liquidators";
        ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 1).Style.Font.FontSize = 16;
        r++;
        ws.Cell(r, 1).Value = "Raleigh, NC · (919) 526-0112 · hello@northstateliquidators.com · northstateliquidators.com";
        ws.Cell(r, 1).Style.Font.FontColor = XLColor.DimGray;
        r += 2;
        r = KeyVal(ws, r, "Box #",       p.Get("pallet_number"));
        r = KeyVal(ws, r, "Lot",         p.Get("display_name"));
        r = KeyVal(ws, r, "Category",    p.Get("category"));
        r = KeyVal(ws, r, "Units",       p.Get("unit_count"));
        r = KeyVal(ws, r, "Est. retail", p.Get("total_msrp"), Money);
        var sold = p.Get("is_sold") is bool b && b;
        r = KeyVal(ws, r, sold ? "Status" : "Ask", sold ? "SOLD" : p.Get("ask_price"), sold ? null : Money);
        r = KeyVal(ws, r, "Manifest date", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        if (p.Get("public_description") is string d && d.Length > 0)
        {
            ws.Cell(r, 1).Value = d; ws.Cell(r, 1).Style.Font.Italic = true;
            ws.Range(r, 1, r, 6).Merge().Style.Alignment.WrapText = true;
            r++;
        }
        r++;

        var cols = new (string Header, string Key, string? Fmt)[]
        {
            ("Item", "title", null), ("Brand", "brand", null), ("Category", "category", null),
            ("Condition", "condition", null), ("Qty", "qty", "0"),
            ("Est. retail (each)", "est_msrp", Money), ("Est. retail (ext.)", "__ext_msrp", Money),
        };
        foreach (var it in items)
            it["__ext_msrp"] = Mul(ToDec(it.Get("est_msrp")), ToDec(it.Get("qty")) ?? 1m);
        int end = WriteTable(ws, r, cols, items);

        ws.Cell(end + 2, 1).Value = "Estimated retail is the original list price and is not a promise of resale value. " +
                                    "Liquidation goods are sold as-is; conditions are customer return, shelf pull, or new in box. " +
                                    "First come, first served — call to claim.";
        ws.Range(end + 2, 1, end + 2, 7).Merge().Style.Alignment.WrapText = true;
        ws.Row(end + 2).Height = 45;
        ws.Cell(end + 2, 1).Style.Font.FontColor = XLColor.DimGray;

        var name = $"NSL-Manifest-Box-{p.Get("pallet_number")}-{Slug(p.Get("display_name")?.ToString())}.xlsx";
        _log.LogInformation("Export public manifest {Id} ({Rows} items)", id, items.Count);
        return File(wb, name);
    }

    // ------------------------------------------------------------------
    // Staff: whole floor. Sheet 1 = boxes, sheet 2 = every item.
    // ------------------------------------------------------------------
    [Function("ExportInventory")]
    public async Task<IActionResult> Inventory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inventory/export")] HttpRequest req,
        CancellationToken ct)
    {
        bool includeArchived = string.Equals(req.Query["includeArchived"], "true", StringComparison.OrdinalIgnoreCase);
        var where = includeArchived ? "" : "WHERE p.archived_at IS NULL";

        await using var conn = await _sql.OpenAsync(ct);
        var boxes = (await conn.QueryAsync($@"
SELECT p.pallet_number, p.display_name, p.category, p.publish_state, p.sell_mode,
       p.received_date, p.sold_at, p.archived_at, p.is_ghost,
       p.item_count, p.unit_count, p.total_msrp, p.total_cost_units, p.total_wholesale,
       p.total_est_resale, p.list_price, p.sale_price,
       COALESCE(p.sale_price, p.list_price) AS ask_price,
       p.source, p.pallet_reference, p.notes, p.manifest_id
FROM dbo.v_pallets p {where}
ORDER BY p.pallet_number")).Cast<IDictionary<string, object?>>().ToList();

        var items = (await conn.QueryAsync($@"
SELECT m.pallet_number, m.display_name AS box_name, m.publish_state,
       li.lpn, li.upc, li.asin, li.title, li.brand, li.category, cat.seller_category,
       li.condition, li.qty, li.est_msrp, li.est_resale,
       COALESCE(li.unit_cost, cat.unit_cost)             AS unit_cost,
       COALESCE(li.wholesale_price, cat.wholesale_price) AS wholesale_price,
       li.notes, li.created_at
FROM dbo.line_items li
JOIN dbo.manifests m ON m.id = li.manifest_id
OUTER APPLY (
    SELECT TOP 1 c.seller_category, c.unit_cost, c.wholesale_price
    FROM dbo.lpn_catalog c
    WHERE c.lpn = li.lpn
       OR (li.upc  IS NOT NULL AND c.upc  = li.upc)
       OR (li.asin IS NOT NULL AND c.asin = li.asin)
    ORDER BY CASE WHEN c.lpn = li.lpn THEN 0 WHEN c.upc = li.upc THEN 1 ELSE 2 END
) cat
{(includeArchived ? "" : "WHERE m.archived_at IS NULL")}
ORDER BY m.pallet_number, li.created_at")).Cast<IDictionary<string, object?>>().ToList();

        foreach (var it in items)
        {
            var qty = ToDec(it.Get("qty")) ?? 1m;
            it["__ext_msrp"] = Mul(ToDec(it.Get("est_msrp")), qty);
            it["__ext_cost"] = Mul(ToDec(it.Get("unit_cost")), qty);
        }

        using var wb = new XLWorkbook();

        var wsB = wb.AddWorksheet("Boxes");
        wsB.Cell(1, 1).Value = $"North State Liquidators — Inventory by box · exported {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC" +
                               (includeArchived ? " · includes archived" : "");
        wsB.Cell(1, 1).Style.Font.Bold = true;
        WriteTable(wsB, 3, new (string, string, string?)[]
        {
            ("Box #", "pallet_number", null), ("Name", "display_name", null), ("Category", "category", null),
            ("Status", "publish_state", null), ("Sell mode", "sell_mode", null),
            ("Received", "received_date", "yyyy-mm-dd"), ("Sold", "sold_at", "yyyy-mm-dd"),
            ("Archived", "archived_at", "yyyy-mm-dd"), ("Ghost", "is_ghost", null),
            ("Line items", "item_count", "0"), ("Units", "unit_count", "0"),
            ("Total MSRP", "total_msrp", Money), ("Total cost", "total_cost_units", Money),
            ("Total wholesale", "total_wholesale", Money), ("Est. resale", "total_est_resale", Money),
            ("List price", "list_price", Money), ("Sale price", "sale_price", Money), ("Ask", "ask_price", Money),
            ("Source", "source", null), ("Reference", "pallet_reference", null), ("Notes", "notes", null),
            ("Box id", "manifest_id", null),
        }, boxes);

        var wsI = wb.AddWorksheet("Items");
        wsI.Cell(1, 1).Value = "Every scanned or imported item, one row each, with its box.";
        wsI.Cell(1, 1).Style.Font.Bold = true;
        WriteTable(wsI, 3, new (string, string, string?)[]
        {
            ("Box #", "pallet_number", null), ("Box name", "box_name", null), ("Box status", "publish_state", null),
            ("LPN", "lpn", null), ("UPC", "upc", null), ("ASIN", "asin", null),
            ("Title", "title", null), ("Brand", "brand", null),
            ("Category", "category", null), ("Seller category", "seller_category", null),
            ("Condition", "condition", null), ("Qty", "qty", "0"),
            ("Unit MSRP", "est_msrp", Money), ("Ext. MSRP", "__ext_msrp", Money),
            ("Est. resale", "est_resale", Money),
            ("Unit cost", "unit_cost", Money), ("Ext. cost", "__ext_cost", Money),
            ("Wholesale", "wholesale_price", Money),
            ("Notes", "notes", null), ("Scanned", "created_at", "yyyy-mm-dd hh:mm"),
        }, items);

        _log.LogInformation("Export inventory: {Boxes} boxes, {Items} items, archived={A}", boxes.Count, items.Count, includeArchived);
        return File(wb, $"NSL-Inventory-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>Writes a header row + data rows starting at <paramref name="row"/>; returns the last row written.</summary>
    private static int WriteTable(IXLWorksheet ws, int row,
        (string Header, string Key, string? Fmt)[] cols, List<IDictionary<string, object?>> rows)
    {
        for (int c = 0; c < cols.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = cols[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#002868");
            cell.Style.Font.FontColor = XLColor.White;
        }
        int r = row;
        foreach (var data in rows)
        {
            r++;
            for (int c = 0; c < cols.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                Set(cell, data.Get(cols[c].Key));
                if (cols[c].Fmt != null) cell.Style.NumberFormat.Format = cols[c].Fmt!;
            }
        }
        if (rows.Count > 0)
            ws.Range(row, 1, r, cols.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(row);
        ws.Columns(1, cols.Length).AdjustToContents(row, Math.Min(r, row + 200));
        foreach (var col in ws.Columns(1, cols.Length))
            if (col.Width > 60) col.Width = 60;
        return r;
    }

    private static int KeyVal(IXLWorksheet ws, int row, string key, object? val, string? fmt = null)
    {
        ws.Cell(row, 1).Value = key;
        ws.Cell(row, 1).Style.Font.Bold = true;
        Set(ws.Cell(row, 2), val);
        if (fmt != null) ws.Cell(row, 2).Style.NumberFormat.Format = fmt;
        return row + 1;
    }

    private static void Set(IXLCell cell, object? v)
    {
        switch (v)
        {
            case null:            cell.Value = Blank.Value; break;
            case string s:        cell.Value = s; break;
            case bool b:          cell.Value = b ? "yes" : "no"; break;
            case DateTime dt:     cell.Value = dt; break;
            case DateTimeOffset o: cell.Value = o.UtcDateTime; break;
            case Guid g:          cell.Value = g.ToString(); break;
            case decimal d:       cell.Value = d; break;
            case double d:        cell.Value = d; break;
            case float f:         cell.Value = (double)f; break;
            case int i:           cell.Value = i; break;
            case long l:          cell.Value = l; break;
            case short sh:        cell.Value = sh; break;
            case byte by:         cell.Value = by; break;
            default:              cell.Value = v.ToString() ?? ""; break;
        }
    }

    private static decimal? ToDec(object? v) => v switch
    {
        null => null,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        string s when decimal.TryParse(s, out var d) => d,
        _ => null,
    };

    private static object? Mul(decimal? a, decimal b) => a.HasValue ? a.Value * b : null;

    private static string Join(object? a, object? b)
    {
        var parts = new[] { a?.ToString(), b?.ToString() }.Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" / ", parts);
    }

    private static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "box";
        var slug = Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "box" : (slug.Length > 40 ? slug[..40].TrimEnd('-') : slug);
    }

    private static FileContentResult File(XLWorkbook wb, string fileName)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new FileContentResult(ms.ToArray(), XlsxMime) { FileDownloadName = fileName };
    }
}

internal static class RowExt
{
    public static object? Get(this IDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v : null;
}
