using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// POST /api/scan — wraps sp_RecordScan.
/// Body (JSON):
///   {
///     "code":      "LPNNG5YZ6VXX5",     // required
///     "qty":       1,                   // optional, default 1
///     "condition": "open_box",          // optional override
///     "notes":     "scuffed lid",       // optional
///     "photoUrl":  "https://...",       // optional
///     "manifestId":"<guid>"             // optional — defaults to most-recent manifest
///   }
/// Returns the new line_items.id and the resolved enrichment fields.
/// </summary>
public sealed class ScanFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<ScanFunction> _log;

    public sealed record ScanRequest(
        string code,
        int? qty,
        string? condition,
        string? notes,
        string? photoUrl,
        decimal? sellPrice,
        Guid? manifestId,
        // Optional fields carried from /api/lookup so non-catalog matches
        // (UPCitemdb fallback) still persist a usable title/brand on the line item.
        string? title,
        string? brand,
        string? category,
        decimal? msrp,
        string? matchSource,
        decimal? wholesalePrice);   // PRICE column on receiving page (manifest's Wholesale Price)

    public ScanFunction(SqlService sql, ILogger<ScanFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("Scan")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scan")] HttpRequest req,
        CancellationToken ct)
    {
        ScanRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ScanRequest>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON body", detail = ex.Message });
        }

        if (body == null || string.IsNullOrWhiteSpace(body.code))
            return new BadRequestObjectResult(new { error = "code is required" });

        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(@"
EXEC dbo.sp_RecordScan
  @manifest_id         = @ManifestId,
  @code                = @Code,
  @qty                 = @Qty,
  @condition           = @Condition,
  @notes               = @Notes,
  @photo_url           = @PhotoUrl,
  @sell_price          = @SellPrice,
  @arg_title           = @Title,
  @arg_brand           = @Brand,
  @arg_category        = @Category,
  @arg_msrp            = @Msrp,
  @arg_match_source    = @MatchSource,
  @arg_wholesale_price = @WholesalePrice",
            new
            {
                ManifestId     = body.manifestId,
                Code           = body.code,
                Qty            = body.qty ?? 1,
                Condition      = body.condition,
                Notes          = body.notes,
                PhotoUrl       = body.photoUrl,
                SellPrice      = body.sellPrice,
                Title          = body.title,
                Brand          = body.brand,
                Category       = body.category,
                Msrp           = body.msrp,
                MatchSource    = body.matchSource,
                WholesalePrice = body.wholesalePrice
            });

        if (row == null) return new ObjectResult(new { error = "sp_RecordScan returned no rows" }) { StatusCode = 500 };
        return new OkObjectResult(row);
    }
}
