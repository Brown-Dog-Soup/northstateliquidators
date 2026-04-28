using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// Pallet (manifest) management endpoints used by /staff/admin.
///
///   GET    /api/pallets              — list all pallets (v_pallets view)
///   POST   /api/pallets              — create a new pallet (sp_CreateManifest)
///   GET    /api/pallets/{id}         — pallet detail incl. line_items
///   PATCH  /api/pallets/{id}         — update display_name, sell_mode, photo_url, etc.
///   GET    /api/pallets/{id}/items   — line_items for a pallet
/// </summary>
public sealed class PalletsFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<PalletsFunction> _log;

    public sealed record CreatePalletRequest(string? displayName, string? source, string? palletReference, string? notes);
    public sealed record UpdatePalletRequest(string? displayName, string? sellMode, string? photoUrl, string? notes);

    public PalletsFunction(SqlService sql, ILogger<PalletsFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("ListPallets")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = await conn.QueryAsync(
            "SELECT * FROM dbo.v_pallets ORDER BY received_date DESC, pallet_number DESC");
        return new OkObjectResult(rows);
    }

    [Function("CreatePallet")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets")] HttpRequest req,
        CancellationToken ct)
    {
        CreatePalletRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<CreatePalletRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(@"
EXEC dbo.sp_CreateManifest
  @display_name      = @DisplayName,
  @source            = @Source,
  @pallet_reference  = @PalletReference,
  @notes             = @Notes",
            new
            {
                DisplayName = body?.displayName,
                Source = body?.source,
                PalletReference = body?.palletReference,
                Notes = body?.notes
            });

        if (row == null) return new ObjectResult(new { error = "sp_CreateManifest returned no rows" }) { StatusCode = 500 };
        return new OkObjectResult(row);
    }

    [Function("GetPallet")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var pallet = await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM dbo.v_pallets WHERE manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();

        var items = await conn.QueryAsync(@"
SELECT id, lpn, upc, asin, qty, condition, title, brand, category,
       est_msrp, est_resale, unit_cost, photo_blob_url, enrich_status,
       enrich_source, notes, created_at
FROM dbo.line_items WHERE manifest_id = @id ORDER BY created_at DESC", new { id });

        return new OkObjectResult(new { pallet, items });
    }

    [Function("UpdatePallet")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "pallets/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        UpdatePalletRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<UpdatePalletRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        await using var conn = await _sql.OpenAsync(ct);

        // sell_mode goes through the stored proc (it cascades to status)
        if (!string.IsNullOrWhiteSpace(body?.sellMode))
        {
            await conn.ExecuteAsync("EXEC dbo.sp_SetSellMode @manifest_id = @id, @sell_mode = @mode",
                new { id, mode = body.sellMode });
        }

        // Other fields update inline
        var sets = new List<string>();
        var p = new DynamicParameters();
        p.Add("id", id);
        if (body?.displayName != null) { sets.Add("display_name = @dn"); p.Add("dn", body.displayName); }
        if (body?.photoUrl    != null) { sets.Add("photo_url = @pu");    p.Add("pu", body.photoUrl); }
        if (body?.notes       != null) { sets.Add("notes = @nt");        p.Add("nt", body.notes); }

        if (sets.Count > 0)
        {
            sets.Add("updated_at = SYSUTCDATETIME()");
            await conn.ExecuteAsync(
                $"UPDATE dbo.manifests SET {string.Join(", ", sets)} WHERE id = @id", p);
        }

        var updated = await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM dbo.v_pallets WHERE manifest_id = @id", new { id });
        return new OkObjectResult(updated);
    }

    [Function("ListPalletItems")]
    public async Task<IActionResult> ListItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}/items")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var items = await conn.QueryAsync(@"
SELECT id, lpn, upc, asin, qty, condition, title, brand, category,
       est_msrp, est_resale, unit_cost, photo_blob_url, enrich_status,
       enrich_source, notes, created_at
FROM dbo.line_items WHERE manifest_id = @id ORDER BY created_at DESC", new { id });
        return new OkObjectResult(items);
    }
}
