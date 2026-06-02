using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;

namespace NSL.Api.Functions;

/// <summary>
/// GET /api/lookup/{code}
///
/// Tries lookups in this order:
///   1. dbo.sp_LookupCode — local lpn_catalog match by lpn / upc / asin
///   2. UPCitemdb (or whichever public UPC provider is wired) — only for
///      12/13-digit numeric codes that don't match the local catalog
///
/// Returns 404 if neither source matches.
/// </summary>
public sealed class LookupFunction
{
    private readonly SqlService _sql;
    private readonly UpcLookupService _upc;
    private readonly ILogger<LookupFunction> _log;

    public LookupFunction(SqlService sql, UpcLookupService upc, ILogger<LookupFunction> log)
    {
        _sql = sql;
        _upc = upc;
        _log = log;
    }

    [Function("Lookup")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lookup/{code}")] HttpRequest req,
        string code,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new BadRequestObjectResult(new { error = "code path parameter is required" });

        await using var conn = await _sql.OpenAsync(ct);

        // 1. Local catalog by the scanned code (lpn / upc / asin).
        var row = await conn.QueryFirstOrDefaultAsync(
            "EXEC dbo.sp_LookupCode @code = @c", new { c = code });

        // 2. External provider. Always attempted (not just on a catalog miss) so
        //    we can show a *market* price alongside the *manifest* price — and so
        //    a retail UPC that missed the catalog can bridge to the manifest row
        //    by ASIN/EAN below.
        var market = await _upc.LookupAsync(code, ct);

        // 3. Bridge. B-Stock catalog rows are keyed on LPN/ASIN and often have a
        //    blank UPC, so scanning a product's retail UPC misses the catalog even
        //    when the item is in the manifest. If the provider handed back an
        //    ASIN / EAN (or the UPC-A↔EAN-13 form), re-check the catalog by those
        //    to recover the manifest price.
        bool bridged = false;
        if (row == null)
        {
            // Re-run the granted sp_LookupCode (matches lpn/upc/asin) for each
            // bridge candidate. Routing through the proc keeps us on the same
            // permissions the app already has — no direct base-table read.
            foreach (var cand in new[] { market?.Asin, market?.Ean, NormalizeGtin(code) })
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                row = await conn.QueryFirstOrDefaultAsync(
                    "EXEC dbo.sp_LookupCode @code = @c", new { c = cand });
                if (row != null) { bridged = true; break; }
            }
        }

        // 4a. Catalog hit (direct or bridged): return the manifest data with the
        //     market price attached as an extra field (dual-source pricing).
        if (row != null)
        {
            var dict = (IDictionary<string, object>)row;
            dict["market_price"] = market?.Msrp;
            // Lend the provider's image if the catalog row has none.
            if ((!dict.TryGetValue("image_url", out var img) || img == null) && market?.ImageUrl != null)
                dict["image_url"] = market.ImageUrl;
            _log.LogInformation("Lookup {Code} -> hit (catalog{Bridged}{Market})", code,
                bridged ? "+bridge" : "", market != null ? "+market" : "");
            return new OkObjectResult(row);
        }

        // 4b. No catalog row — external-only hit. The market price is the only one.
        if (market != null)
        {
            _log.LogInformation("Lookup {Code} -> hit ({Source}, no manifest)", code, market.Source);
            return new OkObjectResult(new
            {
                match_source    = market.Source,
                lpn             = (string?)null,
                asin            = market.Asin,
                upc             = market.Upc ?? code,
                title           = market.Title,
                description     = market.Description,
                brand           = market.Brand,
                category        = market.Category,
                subcategory     = (string?)null,
                msrp            = (decimal?)null,     // no manifest price for off-catalog UPCs
                market_price    = market.Msrp,        // the only price we have
                unit_cost       = (decimal?)null,
                wholesale_price = (decimal?)null,
                condition       = (string?)null,
                qty_in_manifest = (int?)null,
                pallet_id       = (string?)null,
                lot_id          = (string?)null,
                order_number    = (string?)null,
                image_url       = market.ImageUrl
            });
        }

        // Distinguish "barcode is structurally invalid" from "valid but unknown".
        // 12/13-digit numeric codes that fail the GTIN check digit are almost
        // always a scanner misread — surface that to the UI so the user knows
        // to rescan instead of treating it as a manual-entry catalog miss.
        if (System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d{12,13}$")
            && !UpcLookupService.IsValidGtinCheckDigit(code))
        {
            _log.LogInformation("Lookup {Code} -> invalid GTIN check digit", code);
            return new ObjectResult(new { error = "invalid_upc", message = "Barcode check digit failed — likely a scanner misread. Please rescan." })
            {
                StatusCode = 422
            };
        }

        _log.LogInformation("Lookup {Code} -> miss", code);
        return new NotFoundResult();
    }

    /// <summary>
    /// UPC-A (12 digits) and EAN-13 (13 digits, leading 0) are the same GTIN in
    /// two forms. A manifest may store one form while the scanner reads the other,
    /// so we try the alternate form when bridging a missed scan to the catalog.
    /// </summary>
    private static string? NormalizeGtin(string code)
    {
        var c = code.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(c, @"^\d{12}$"))  return "0" + c;        // UPC-A → EAN-13
        if (System.Text.RegularExpressions.Regex.IsMatch(c, @"^0\d{12}$")) return c[1..];          // EAN-13(0) → UPC-A
        return null;
    }
}
