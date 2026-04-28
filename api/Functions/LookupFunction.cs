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

        // 1. Local catalog
        await using (var conn = await _sql.OpenAsync(ct))
        {
            var row = await conn.QueryFirstOrDefaultAsync(
                "EXEC dbo.sp_LookupCode @code = @c", new { c = code });
            if (row != null)
            {
                _log.LogInformation("Lookup {Code} -> hit (local catalog)", code);
                return new OkObjectResult(row);
            }
        }

        // 2. Public UPC fallback
        var upc = await _upc.LookupAsync(code, ct);
        if (upc != null)
        {
            _log.LogInformation("Lookup {Code} -> hit ({Source})", code, upc.Source);
            return new OkObjectResult(new
            {
                match_source    = upc.Source,
                lpn             = (string?)null,
                asin            = (string?)null,
                upc             = upc.Upc ?? code,
                title           = upc.Title,
                brand           = upc.Brand,
                category        = upc.Category,
                subcategory     = (string?)null,
                msrp            = upc.Msrp,
                unit_cost       = (decimal?)null,
                condition       = (string?)null,
                qty_in_manifest = (int?)null,
                pallet_id       = (string?)null,
                lot_id          = (string?)null,
                order_number    = (string?)null,
                image_url       = upc.ImageUrl
            });
        }

        _log.LogInformation("Lookup {Code} -> miss", code);
        return new NotFoundResult();
    }
}
