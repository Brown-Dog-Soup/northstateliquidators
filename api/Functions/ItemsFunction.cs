using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// Edit and delete endpoints for individual scanned line items.
///
///   PATCH  /api/items/{id}   — edit qty/condition/sell_price/title/brand/notes
///   DELETE /api/items/{id}   — remove an accidental scan (hard delete)
/// </summary>
public sealed class ItemsFunction
{
    private readonly SqlService _sql;
    private readonly ILogger<ItemsFunction> _log;

    public sealed record PatchRequest(
        int? qty,
        string? condition,
        decimal? sellPrice,
        string? title,
        string? brand,
        string? notes);

    public ItemsFunction(SqlService sql, ILogger<ItemsFunction> log)
    {
        _sql = sql;
        _log = log;
    }

    [Function("PatchItem")]
    public async Task<IActionResult> Patch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "items/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        PatchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<PatchRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }
        if (body == null) return new BadRequestObjectResult(new { error = "empty body" });

        var sets = new List<string>();
        var p = new DynamicParameters();
        p.Add("id", id);
        if (body.qty.HasValue)               { sets.Add("qty = @q");           p.Add("q", body.qty.Value); }
        if (body.condition != null)          { sets.Add("condition = @c");     p.Add("c", body.condition); }
        if (body.sellPrice.HasValue)         { sets.Add("est_resale = @sp");   p.Add("sp", body.sellPrice.Value); }
        if (body.title != null)              { sets.Add("title = @t");         p.Add("t", body.title); }
        if (body.brand != null)              { sets.Add("brand = @b");         p.Add("b", body.brand); }
        if (body.notes != null)              { sets.Add("notes = @n");         p.Add("n", body.notes); }

        if (sets.Count == 0) return new BadRequestObjectResult(new { error = "no fields to update" });

        await using var conn = await _sql.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            $"UPDATE dbo.line_items SET {string.Join(", ", sets)} WHERE id = @id", p);
        if (rows == 0) return new NotFoundResult();

        var updated = await conn.QueryFirstOrDefaultAsync(@"
SELECT id, manifest_id, lpn, upc, asin, qty, condition, title, brand, category,
       est_msrp, est_resale, unit_cost, photo_blob_url, enrich_status, notes, created_at
FROM dbo.line_items WHERE id = @id", new { id });
        _log.LogInformation("PatchItem {Id}: {N} fields updated", id, sets.Count);
        return new OkObjectResult(updated);
    }

    [Function("DeleteItem")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "items/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = await conn.ExecuteAsync("DELETE FROM dbo.line_items WHERE id = @id", new { id });
        if (rows == 0) return new NotFoundResult();
        _log.LogInformation("DeleteItem {Id}: removed", id);
        return new OkObjectResult(new { id, deleted = true });
    }
}
