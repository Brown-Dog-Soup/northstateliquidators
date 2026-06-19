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
        string? category,      // pallet-level top bucket: Apparel, Electronics, ...
        bool?   archived,      // true = archive, false = restore, null = no change
        bool?   isGhost,       // legacy: true = ghost backstock, false = real, null = no change
        string? publishState,  // draft | live | ghost | sold  (#6 — routes through sp_SetPublishState)
        decimal? listPrice,    // #3 published ask override; send the key with null to clear
        decimal? salePrice);   // #3 sale price (strike-through on the site); send key with null to clear

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
        // Read the raw body once so we can both deserialize the typed shape and
        // tell present-vs-absent for the price keys (needed so callers that send
        // only {archived} don't accidentally clear list_price/sale_price).
        string raw;
        using (var sr = new StreamReader(req.Body)) raw = await sr.ReadToEndAsync(ct);
        UpdatePalletRequest? body;
        JsonDocument? doc = null;
        try
        {
            body = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<UpdatePalletRequest>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(raw)) doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        bool HasKey(string name) =>
            doc != null && doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.EnumerateObject().Any(prop =>
                string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase));

        await using var conn = await _sql.OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(body?.sellMode))
        {
            await conn.ExecuteAsync("EXEC dbo.sp_SetSellMode @manifest_id = @id, @sell_mode = @mode",
                new { id, mode = body.sellMode });
        }

        // #6 Publish state is the source of truth and keeps is_ghost / sold_at /
        // line_items in sync, so route it (and the legacy isGhost toggle) through
        // sp_SetPublishState rather than a bare UPDATE.
        string? targetState = body?.publishState;
        if (targetState == null && body?.isGhost.HasValue == true)
            targetState = body.isGhost.Value ? "ghost" : "draft";
        if (!string.IsNullOrWhiteSpace(targetState))
        {
            await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @id, @publish_state = @ps",
                new { id, ps = targetState });
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
        // #3 Prices: only touch a column when its key is actually present in the
        // body. A null/<=0 value clears it (no sale / no override).
        if (HasKey("listPrice"))
        {
            sets.Add("list_price = @lp");
            p.Add("lp", body?.listPrice is > 0 ? body?.listPrice : (decimal?)null);
        }
        if (HasKey("salePrice"))
        {
            sets.Add("sale_price = @sp2");
            p.Add("sp2", body?.salePrice is > 0 ? body?.salePrice : (decimal?)null);
        }
        doc?.Dispose();

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

    public sealed record GenerateGhostRequest(int? palletCount, int? itemsPerPallet, string? category);

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
        var itemsPerPallet = Math.Clamp(body?.itemsPerPallet ?? 12, 1, 200);
        // null/empty = let the proc randomize the category per pallet (old behavior).
        var category = string.IsNullOrWhiteSpace(body?.category) ? null : body!.category!.Trim();

        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(
            "EXEC dbo.sp_GenerateGhostBackstock @pallet_count = @P, @items_per_pallet = @I, @category = @C",
            new { P = palletCount, I = itemsPerPallet, C = category })).ToList();

        _log.LogInformation("GenerateGhostBackstock: created {N} ghost pallets ({I} items each, category={C})",
            rows.Count, itemsPerPallet, category ?? "random");
        return new OkObjectResult(new { generated = rows.Count, palletCount, itemsPerPallet, category, pallets = rows });
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

    /// <summary>
    /// GET /api/public/pallets — anonymous read of what the marketing site may
    /// show. Live pallets first (for sale), then recently-sold (ghost + sold)
    /// as social proof. Photo URLs are SAS-signed like everywhere else.
    /// </summary>
    [Function("PublicPallets")]
    public async Task<IActionResult> PublicPallets(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/pallets")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(@"
SELECT manifest_id, pallet_number, display_name, category, publish_state,
       received_date, sold_at, photo_url, item_count, unit_count, total_msrp,
       list_price, sale_price, is_sold, is_on_sale, ask_price
FROM dbo.v_public_pallets
ORDER BY CASE WHEN publish_state = 'live' THEN 0 ELSE 1 END,
         COALESCE(sold_at, received_date) DESC")).ToList();
        SignRowPhotos(rows);
        return new OkObjectResult(rows);
    }

    /// <summary>
    /// GET /api/public/pallets/{id}/items — anonymous read of a publicly-visible
    /// pallet's manifest, for the "View Manifest" modal on the marketing site.
    ///
    /// Customer-safe by construction: it selects ONLY title / brand / category /
    /// condition / qty / est_msrp / photo. unit_cost and wholesale_price (our
    /// margins) are intentionally NOT selected here — unlike the staff-gated
    /// GetPallet/ListPalletItems endpoints. The pallet is gated through
    /// v_public_pallets, so a draft/archived pallet returns 404 and its contents
    /// can't be enumerated.
    /// </summary>
    [Function("PublicPalletItems")]
    public async Task<IActionResult> PublicPalletItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/pallets/{id}/items")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);

        var pallet = await conn.QueryFirstOrDefaultAsync(@"
SELECT manifest_id, pallet_number, display_name, category, publish_state,
       item_count, unit_count, total_msrp, ask_price, list_price, sale_price,
       is_sold, is_on_sale, photo_url
FROM dbo.v_public_pallets WHERE manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();   // not public → don't leak it
        SignRowPhotos((object)pallet);

        // Margin-safe column list — NO unit_cost / wholesale_price.
        var items = (await conn.QueryAsync(@"
SELECT title, brand, category, condition, qty, est_msrp, photo_blob_url
FROM dbo.line_items
WHERE manifest_id = @id
ORDER BY CASE WHEN est_msrp IS NULL THEN 1 ELSE 0 END, est_msrp DESC, created_at DESC",
            new { id })).ToList();
        SignRowPhotos(items);

        return new OkObjectResult(new { pallet, items });
    }

    public sealed record CreateFromItemsRequest(string? displayName, string[]? lpns);

    /// <summary>
    /// POST /api/pallets/from-items — build a new pallet from catalog rows the
    /// user checked off on the Inventory page (#9). Useful for barcode-less
    /// goods (e.g. Bella Canvas shirts) that come in via CSV import rather than
    /// a physical scan. Returns the new pallet id so the UI can jump to it.
    /// </summary>
    [Function("CreatePalletFromItems")]
    public async Task<IActionResult> CreateFromItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/from-items")] HttpRequest req,
        CancellationToken ct)
    {
        CreateFromItemsRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<CreateFromItemsRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        if (body?.lpns == null || body.lpns.Length == 0)
            return new BadRequestObjectResult(new { error = "lpns array is required and must be non-empty" });

        var lpnsJson = JsonSerializer.Serialize(body.lpns);
        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(
            "EXEC dbo.sp_CreatePalletFromCatalog @display_name = @Name, @lpns_json = @Lpns",
            new { Name = body.displayName, Lpns = lpnsJson });

        if (row == null) return new ObjectResult(new { error = "sp_CreatePalletFromCatalog returned no rows" }) { StatusCode = 500 };
        _log.LogInformation("CreatePalletFromItems: pallet {Id} with {N} item(s)", (object?)row.id, (object?)row.items_added);
        return new OkObjectResult(row);
    }

    public sealed record AddItemRequest(
        string? title, string? brand, string? category, int? qty,
        string? condition, decimal? sellPrice, decimal? msrp,
        decimal? wholesalePrice, string? description, string? notes);

    /// <summary>
    /// POST /api/pallets/{id}/items — add an ad-hoc line item to a pallet with
    /// no barcode (#9 companion). For goods that never had a UPC/LPN — the
    /// receiver types a title/qty/price and it lands on the pallet directly.
    /// </summary>
    [Function("AddPalletItem")]
    public async Task<IActionResult> AddItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/{id}/items")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        AddItemRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<AddItemRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }

        if (string.IsNullOrWhiteSpace(body?.title))
            return new BadRequestObjectResult(new { error = "title is required for a barcode-less item" });

        await using var conn = await _sql.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM dbo.manifests WHERE id = @id", new { id });
        if (exists == 0) return new NotFoundResult();

        var newId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
INSERT INTO dbo.line_items
    (id, manifest_id, qty, condition, enrich_status, enrich_source,
     title, description, brand, category, est_msrp, est_resale, wholesale_price, notes,
     created_at, enriched_at)
VALUES
    (@id, @mid, @qty, @cond, 'hit', 'manual',
     @title, @desc, @brand, @cat, @msrp, @sell, @whole, @notes,
     SYSUTCDATETIME(), SYSUTCDATETIME())",
            new
            {
                id = newId, mid = id, qty = body.qty ?? 1,
                cond = string.IsNullOrWhiteSpace(body.condition) ? "untested" : body.condition,
                title = body.title, desc = body.description, brand = body.brand, cat = body.category,
                msrp = body.msrp, sell = body.sellPrice, whole = body.wholesalePrice, notes = body.notes
            });

        _log.LogInformation("AddPalletItem {Item} -> pallet {Pallet}", newId, id);
        return new OkObjectResult(new { id = newId, manifest_id = id, title = body.title });
    }
}
