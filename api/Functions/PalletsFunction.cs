using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
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
///
/// Photo URLs in the result rows are SAS-signed before being returned, so the
/// browser can fetch them from the private scan-photos blob container without
/// needing to handle auth headers.
/// </summary>
public sealed class PalletsFunction
{
    private readonly SqlService _sql;
    private readonly BlobService _blob;
    private readonly string _storageAccount;
    private readonly ILogger<PalletsFunction> _log;

    public sealed record CreatePalletRequest(string? displayName, string? source, string? palletReference, string? notes);
    public sealed record UpdatePalletRequest(
        string? displayName, string? sellMode, string? photoUrl, string? notes,
        string? category,    // pallet-level top bucket: Apparel, Electronics, ...
        bool?   archived,    // true = archive, false = restore, null = no change
        bool?   isGhost);    // true = mark as ghost backstock, false = real, null = no change

    public PalletsFunction(SqlService sql, BlobService blob, IConfiguration config, ILogger<PalletsFunction> log)
    {
        _sql = sql;
        _blob = blob;
        _storageAccount = config["StorageAccountName"] ?? "";
        _log = log;
    }

    /// <summary>
    /// If the URL is a bare blob URL pointing at our scan-photos container,
    /// rewrite it to a SAS-signed read URL valid for 4 hours so the browser
    /// can load it without auth. Non-matching URLs are returned untouched.
    /// </summary>
    private string? SignBlobUrl(string? rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return rawUrl;
        var prefix = $"https://{_storageAccount}.blob.core.windows.net/scan-photos/";
        if (!rawUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return rawUrl;
        var path = rawUrl[prefix.Length..].Split('?')[0];
        try { return _blob.GenerateReadSas("scan-photos", path, TimeSpan.FromHours(4)); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to sign blob URL for {Path}", path); return rawUrl; }
    }

    private void SignRowPhotos(IEnumerable<dynamic>? rows)
    {
        if (rows == null) return;
        foreach (var row in rows) SignRowPhotos((object?)row);
    }

    private void SignRowPhotos(object? row)
    {
        if (row is not IDictionary<string, object?> dict) return;
        if (dict.ContainsKey("photo_url"))      dict["photo_url"]      = SignBlobUrl(dict["photo_url"] as string);
        if (dict.ContainsKey("photo_blob_url")) dict["photo_blob_url"] = SignBlobUrl(dict["photo_blob_url"] as string);
    }

    [Function("ListPallets")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets")] HttpRequest req,
        CancellationToken ct)
    {
        // ?includeArchived=true surfaces archived pallets; default hides them so
        // the admin gallery only shows active work.
        var includeArchived = string.Equals(
            req.Query["includeArchived"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var sql = includeArchived
            ? "SELECT * FROM dbo.v_pallets ORDER BY received_date DESC, pallet_number DESC"
            : "SELECT * FROM dbo.v_pallets WHERE archived_at IS NULL ORDER BY received_date DESC, pallet_number DESC";

        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(sql)).ToList();
        SignRowPhotos(rows);
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
        SignRowPhotos((object)pallet);

        var items = (await conn.QueryAsync(@"
SELECT id, lpn, upc, asin, qty, condition, title, description, brand, category,
       est_msrp, est_resale, unit_cost, wholesale_price,
       photo_blob_url, enrich_status, enrich_source, notes, created_at
FROM dbo.line_items WHERE manifest_id = @id ORDER BY created_at DESC", new { id })).ToList();
        SignRowPhotos(items);

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

        if (!string.IsNullOrWhiteSpace(body?.sellMode))
        {
            await conn.ExecuteAsync("EXEC dbo.sp_SetSellMode @manifest_id = @id, @sell_mode = @mode",
                new { id, mode = body.sellMode });
        }

        var sets = new List<string>();
        var p = new DynamicParameters();
        p.Add("id", id);
        if (body?.displayName != null) { sets.Add("display_name = @dn"); p.Add("dn", body.displayName); }
        if (body?.photoUrl    != null) { sets.Add("photo_url = @pu");    p.Add("pu", body.photoUrl); }
        if (body?.notes       != null) { sets.Add("notes = @nt");        p.Add("nt", body.notes); }
        if (body?.category    != null) { sets.Add("category = @cat");    p.Add("cat", body.category); }
        if (body?.archived.HasValue == true)
        {
            sets.Add("archived_at = @ar");
            p.Add("ar", body.archived.Value ? (DateTime?)DateTime.UtcNow : null);
        }
        if (body?.isGhost.HasValue == true)
        {
            sets.Add("is_ghost = @gh");
            p.Add("gh", body.isGhost.Value ? 1 : 0);
        }

        if (sets.Count > 0)
        {
            sets.Add("updated_at = SYSUTCDATETIME()");
            await conn.ExecuteAsync(
                $"UPDATE dbo.manifests SET {string.Join(", ", sets)} WHERE id = @id", p);
        }

        var updated = await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM dbo.v_pallets WHERE manifest_id = @id", new { id });
        if (updated != null) SignRowPhotos((object)updated);
        return new OkObjectResult(updated);
    }

    public sealed record GenerateGhostRequest(int? palletCount, int? itemsPerPallet);

    /// <summary>
    /// POST /api/pallets/generate-ghost-backstock
    /// Calls sp_GenerateGhostBackstock to fabricate N ghost pallets with past
    /// sold dates, drawn from real lpn_catalog items. Renders on the public
    /// site as "Recently Sold" social proof. Doesn't affect real inventory.
    /// </summary>
    [Function("GenerateGhostBackstock")]
    public async Task<IActionResult> GenerateGhost(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/generate-ghost-backstock")] HttpRequest req,
        CancellationToken ct)
    {
        GenerateGhostRequest? body = null;
        try
        {
            body = await JsonSerializer.DeserializeAsync<GenerateGhostRequest>(
                req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException) { /* body optional — defaults applied below */ }

        var palletCount = Math.Clamp(body?.palletCount ?? 5, 1, 50);
        var itemsPerPallet = Math.Clamp(body?.itemsPerPallet ?? 12, 1, 50);

        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(
            "EXEC dbo.sp_GenerateGhostBackstock @pallet_count = @P, @items_per_pallet = @I",
            new { P = palletCount, I = itemsPerPallet })).ToList();

        _log.LogInformation("GenerateGhostBackstock: created {N} ghost pallets ({I} items each)", rows.Count, itemsPerPallet);
        return new OkObjectResult(new { generated = rows.Count, palletCount, itemsPerPallet, pallets = rows });
    }

    /// <summary>
    /// POST /api/pallets/{id}/duplicate
    /// Clone a pallet: create a new manifest (name + " (copy)") and copy every
    /// line item onto it. The copy is a fresh real pallet — it starts un-sold
    /// (sold_at cleared) and non-ghost regardless of the source, so duplicating
    /// a ghost or sold pallet still yields a normal working pallet. Returns the
    /// new pallet id so the UI can navigate straight to it.
    /// </summary>
    [Function("DuplicatePallet")]
    public async Task<IActionResult> Duplicate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/{id}/duplicate")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);

        var src = await conn.QueryFirstOrDefaultAsync(
            "SELECT display_name, source, notes, category FROM dbo.manifests WHERE id = @id", new { id });
        if (src == null) return new NotFoundResult();

        var newName = (((string?)src.display_name) ?? "Pallet") + " (copy)";

        var created = await conn.QueryFirstOrDefaultAsync(@"
EXEC dbo.sp_CreateManifest
  @display_name = @DisplayName,
  @source       = @Source,
  @notes        = @Notes",
            new { DisplayName = newName, Source = (string?)src.source, Notes = (string?)src.notes });

        if (created == null)
            return new ObjectResult(new { error = "sp_CreateManifest returned no rows" }) { StatusCode = 500 };

        Guid newId = (Guid)created.id;

        // sp_CreateManifest doesn't take category — copy it over explicitly.
        if (src.category != null)
            await conn.ExecuteAsync(
                "UPDATE dbo.manifests SET category = @cat WHERE id = @nid",
                new { cat = (string)src.category, nid = newId });

        // Copy line items. New ids + manifest_id + created_at; sold_at is left
        // off (NEWID()-only insert), so the copy is unsold even if the source
        // was a ghost/sold pallet.
        var copied = await conn.ExecuteAsync(@"
INSERT INTO dbo.line_items
    (id, manifest_id, upc, lpn, asin, qty, condition,
     photo_blob_url, enrich_status, enrich_source,
     title, description, brand, category,
     est_msrp, est_resale, unit_cost, wholesale_price, notes,
     created_at, enriched_at)
SELECT
     NEWID(), @nid, upc, lpn, asin, qty, condition,
     photo_blob_url, enrich_status, enrich_source,
     title, description, brand, category,
     est_msrp, est_resale, unit_cost, wholesale_price, notes,
     SYSUTCDATETIME(), enriched_at
FROM dbo.line_items WHERE manifest_id = @sid",
            new { nid = newId, sid = id });

        _log.LogInformation("DuplicatePallet {Src} -> {New}: copied {N} item(s)", id, newId, copied);
        return new OkObjectResult(new
        {
            id            = newId,
            pallet_number = created.pallet_number,
            display_name  = created.display_name,
            items_copied  = copied
        });
    }

    /// <summary>
    /// Hard-delete a pallet and all its line items. Use sparingly; archive
    /// (PATCH archived=true) is the safer default and is what the admin UI
    /// uses by default. This endpoint exists for the rare "scanned the wrong
    /// thing entirely, never want to see it again" cleanup.
    /// </summary>
    [Function("DeletePallet")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "pallets/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        try
        {
            var itemRows = await conn.ExecuteAsync(
                "DELETE FROM dbo.line_items WHERE manifest_id = @id",
                new { id }, transaction: tx);
            var palletRows = await conn.ExecuteAsync(
                "DELETE FROM dbo.manifests WHERE id = @id",
                new { id }, transaction: tx);
            tx.Commit();

            if (palletRows == 0) return new NotFoundResult();
            _log.LogInformation("DeletePallet {Id}: removed pallet + {N} item(s)", id, itemRows);
            return new OkObjectResult(new { id, deleted = true, items_deleted = itemRows });
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    [Function("ListPalletItems")]
    public async Task<IActionResult> ListItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}/items")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var items = (await conn.QueryAsync(@"
SELECT id, lpn, upc, asin, qty, condition, title, description, brand, category,
       est_msrp, est_resale, unit_cost, wholesale_price,
       photo_blob_url, enrich_status, enrich_source, notes, created_at
FROM dbo.line_items WHERE manifest_id = @id ORDER BY created_at DESC", new { id })).ToList();
        SignRowPhotos(items);
        return new OkObjectResult(items);
    }
}
