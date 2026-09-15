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
///   GET    /api/pallets/{id}/history — audit trail (manifest_history, B7)
///   POST   /api/pallets/{id}/sold-to-inventory — fake sale + Draft clone (B2/B6)
///
/// Photo URLs in the result rows are SAS-signed before being returned, so the
/// browser can fetch them from the private scan-photos blob container without
/// needing to handle auth headers.
/// </summary>
public sealed class PalletsFunction
{
    private readonly SqlService _sql;
    private readonly BlobService _blob;
    private readonly SquareService _square;
    private readonly string _storageAccount;
    private readonly ILogger<PalletsFunction> _log;

    /// <summary>B9: a live box is "Just Dropped" for this many hours after it went live.</summary>
    internal const int JustDroppedHours = 48;

    private static readonly string[] BoxSizes = { "mega_box", "mini_pallet", "full_pallet", "individual" };

    public sealed record CreatePalletRequest(string? displayName, string? source, string? palletReference, string? notes);
    public sealed record UpdatePalletRequest(
        string? displayName, string? sellMode, string? photoUrl, string? notes,
        string? publicDescription,  // public website blurb (distinct from internal notes)
        string? category,      // pallet-level top bucket: Apparel, Electronics, ...
        bool?   archived,      // true = archive, false = restore, null = no change
        bool?   isGhost,       // legacy: true = ghost backstock, false = real, null = no change
        string? publishState,  // draft | live | ghost | sold  (#6 — routes through sp_SetPublishState)
        decimal? listPrice,    // #3 published ask override; send the key with null to clear
        decimal? salePrice,    // #3 sale price (strike-through on the site); send key with null to clear
        string? boxSize,       // mega_box | mini_pallet | full_pallet | individual; "" (or key present + null) clears
        decimal? weightLbs,    // B3 box weight for shipping quotes; send the key with null (or <= 0) to clear
        bool?   isHotDeal);    // true = manually featured in Hot Deals, false = not, null = no change

    /// <summary>The audited fields (B7) as read straight off dbo.manifests.</summary>
    private sealed record AuditSnapshot(string? publish_state, decimal? list_price, decimal? sale_price, string? box_size, string? sell_mode, bool is_hot_deal);

    public PalletsFunction(SqlService sql, BlobService blob, SquareService square, IConfiguration config, ILogger<PalletsFunction> log)
    {
        _sql = sql;
        _blob = blob;
        _square = square;
        _storageAccount = config["StorageAccountName"] ?? "";
        _log = log;
    }

    private const string AuditSnapshotSql =
        "SELECT publish_state, list_price, sale_price, box_size, sell_mode, is_hot_deal FROM dbo.manifests WHERE id = @id";

    /// <summary>
    /// B7 audit trail: one dbo.manifest_history row per field that differs
    /// between two snapshots. Values are stored as plain strings ("180.00",
    /// "live", NULL). Shared by PATCH, Sold → inventory and the Square paths.
    /// </summary>
    private static async Task WriteHistoryAsync(
        System.Data.IDbConnection conn, Guid id, AuditSnapshot? before, AuditSnapshot? after, string? who,
        System.Data.IDbTransaction? tx = null)
    {
        if (before == null || after == null) return;
        var changes = new List<(string field, string? oldV, string? newV)>();
        void Diff(string field, string? a, string? b) { if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add((field, a, b)); }
        static string? Money(decimal? v) => v?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        static string YesNo(bool v) => v ? "yes" : "no";

        Diff("publish_state", before.publish_state, after.publish_state);
        Diff("list_price",    Money(before.list_price), Money(after.list_price));
        Diff("sale_price",    Money(before.sale_price), Money(after.sale_price));
        Diff("box_size",      before.box_size, after.box_size);
        Diff("sell_mode",     before.sell_mode, after.sell_mode);
        Diff("is_hot_deal",   YesNo(before.is_hot_deal), YesNo(after.is_hot_deal));

        foreach (var (field, oldV, newV) in changes)
            await InsertHistoryAsync(conn, id, field, oldV, newV, who, tx);
    }

    /// <summary>One explicit dbo.manifest_history row (used where the change is known, not diffed).</summary>
    internal static Task InsertHistoryAsync(
        System.Data.IDbConnection conn, Guid id, string field, string? oldValue, string? newValue, string? who,
        System.Data.IDbTransaction? tx = null)
        => conn.ExecuteAsync(@"
INSERT INTO dbo.manifest_history (manifest_id, changed_by, field, old_value, new_value)
VALUES (@id, @who, @field, @oldV, @newV)",
            new { id, who, field, oldV = oldValue, newV = newValue }, transaction: tx);

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
        if (dict.ContainsKey("highlight_photo")) dict["highlight_photo"] = SignBlobUrl(dict["highlight_photo"] as string);
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

        // is_hot_deal / hot_deal_at live on dbo.manifests, not on v_pallets — join
        // rather than re-declare the view (see db/hot-deal-toggle.sql).
        var sql = includeArchived
            ? "SELECT v.*, m.is_hot_deal, m.hot_deal_at FROM dbo.v_pallets v JOIN dbo.manifests m ON m.id = v.manifest_id ORDER BY v.received_date DESC, v.pallet_number DESC"
            : "SELECT v.*, m.is_hot_deal, m.hot_deal_at FROM dbo.v_pallets v JOIN dbo.manifests m ON m.id = v.manifest_id WHERE v.archived_at IS NULL ORDER BY v.received_date DESC, v.pallet_number DESC";

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
            "SELECT v.*, m.is_hot_deal, m.hot_deal_at FROM dbo.v_pallets v JOIN dbo.manifests m ON m.id = v.manifest_id WHERE v.manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();
        SignRowPhotos((object)pallet);

        var items = (await conn.QueryAsync(ItemsWithCatalogSql, new { id })).ToList();
        SignRowPhotos(items);

        return new OkObjectResult(new { pallet, items });
    }

    /// <summary>
    /// Staff-facing line-item query. Joins each row back to lpn_catalog (by the
    /// key that matched at scan time: lpn > upc > asin) so the admin UI can show
    /// the manifest's Seller Category, and so cost/wholesale still display for
    /// items scanned BEFORE a manifest import filled those in on the catalog
    /// (line_items snapshots catalog pricing at scan time, so late-arriving
    /// manifest data never reaches old rows without this fallback).
    /// </summary>
    internal const string ItemsWithCatalogSql = @"
SELECT li.id, li.lpn, li.upc, li.asin, li.qty, li.condition, li.title, li.description,
       li.brand, li.category, cat.seller_category,
       li.est_msrp, li.est_resale,
       COALESCE(li.unit_cost, cat.unit_cost)             AS unit_cost,
       COALESCE(li.wholesale_price, cat.wholesale_price) AS wholesale_price,
       li.photo_blob_url, li.enrich_status, li.enrich_source, li.notes, li.created_at,
       li.is_highlight
FROM dbo.line_items li
OUTER APPLY (
    SELECT TOP 1 c.seller_category, c.unit_cost, c.wholesale_price
    FROM dbo.lpn_catalog c
    WHERE c.lpn = li.lpn
       OR (li.upc  IS NOT NULL AND c.upc  = li.upc)
       OR (li.asin IS NOT NULL AND c.asin = li.asin)
    ORDER BY CASE WHEN c.lpn = li.lpn THEN 0 WHEN c.upc = li.upc THEN 1 ELSE 2 END
) cat
WHERE li.manifest_id = @id ORDER BY li.created_at DESC";

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

        // Box size is a fixed list (drives the Shop pages on the website).
        // "" or a present-but-null key clears it; anything else must match.
        string? boxSize = body?.boxSize?.Trim();
        if (!string.IsNullOrEmpty(boxSize) && !BoxSizes.Contains(boxSize))
        {
            doc?.Dispose();
            return new BadRequestObjectResult(new { error = "boxSize must be one of mega_box | mini_pallet | full_pallet | individual" });
        }

        await using var conn = await _sql.OpenAsync(ct);

        // B7 audit: snapshot the audited fields before anything changes.
        var before = await conn.QueryFirstOrDefaultAsync<AuditSnapshot>(AuditSnapshotSql, new { id });

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
        if (body?.publicDescription != null) { sets.Add("public_description = @pd"); p.Add("pd", body.publicDescription); }
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
        // Box size / weight (Wishlist 4): same key-present rule as the prices.
        if (HasKey("boxSize"))
        {
            sets.Add("box_size = @bs");
            p.Add("bs", string.IsNullOrEmpty(boxSize) ? null : boxSize);
        }
        if (HasKey("weightLbs"))
        {
            sets.Add("weight_lbs = @wl");
            p.Add("wl", body?.weightLbs is > 0 ? body?.weightLbs : (decimal?)null);
        }
        // Hot Deals manual toggle: null = no change (same tri-state as `archived`
        // above), so this checks HasValue rather than HasKey.
        if (body?.isHotDeal.HasValue == true)
        {
            sets.Add("is_hot_deal = @hd");
            p.Add("hd", body.isHotDeal.Value);
            sets.Add("hot_deal_at = @hda");
            p.Add("hda", body.isHotDeal.Value ? (DateTime?)DateTime.UtcNow : null);
        }
        doc?.Dispose();

        if (sets.Count > 0)
        {
            sets.Add("updated_at = SYSUTCDATETIME()");
            await conn.ExecuteAsync(
                $"UPDATE dbo.manifests SET {string.Join(", ", sets)} WHERE id = @id", p);
        }

        // B7 audit: one history row per audited field that actually changed.
        var after = await conn.QueryFirstOrDefaultAsync<AuditSnapshot>(AuditSnapshotSql, new { id });
        await WriteHistoryAsync(conn, id, before, after, ClientPrincipal.UserDetails(req));

        var updated = await conn.QueryFirstOrDefaultAsync(
            "SELECT v.*, m.is_hot_deal, m.hot_deal_at FROM dbo.v_pallets v JOIN dbo.manifests m ON m.id = v.manifest_id WHERE v.manifest_id = @id", new { id });
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
            "SELECT display_name, source, notes, category, box_size, weight_lbs FROM dbo.manifests WHERE id = @id", new { id });
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

        // sp_CreateManifest doesn't take category / box_size / weight_lbs —
        // copy them over explicitly.
        if (src.category != null || src.box_size != null || src.weight_lbs != null)
            await conn.ExecuteAsync(
                "UPDATE dbo.manifests SET category = @cat, box_size = @bs, weight_lbs = @wl WHERE id = @nid",
                new { cat = (string?)src.category, bs = (string?)src.box_size, wl = (decimal?)src.weight_lbs, nid = newId });

        // Copy line items. New ids + manifest_id + created_at; sold_at is left
        // off (NEWID()-only insert), so the copy is unsold even if the source
        // was a ghost/sold pallet.
        var copied = await conn.ExecuteAsync(@"
INSERT INTO dbo.line_items
    (id, manifest_id, upc, lpn, asin, qty, condition,
     photo_blob_url, enrich_status, enrich_source,
     title, description, brand, category,
     est_msrp, est_resale, unit_cost, wholesale_price, notes,
     is_highlight, created_at, enriched_at)
SELECT
     NEWID(), @nid, upc, lpn, asin, qty, condition,
     photo_blob_url, enrich_status, enrich_source,
     title, description, brand, category,
     est_msrp, est_resale, unit_cost, wholesale_price, notes,
     is_highlight, SYSUTCDATETIME(), enriched_at
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
            // manifest_history has an FK to manifests — clear the audit rows first.
            await conn.ExecuteAsync(
                "DELETE FROM dbo.manifest_history WHERE manifest_id = @id",
                new { id }, transaction: tx);
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

    /// <summary>
    /// POST /api/pallets/{id}/sold-to-inventory (Wishlist 4 B2/B6) — ONE button.
    /// The original shows SOLD on the website for 48 h (v_public_pallets drops
    /// it after that; no scheduler), and a clone with a new BOX # and the same
    /// items lands in Draft right away so staff can put it back on the site.
    /// All the data work is in sp_SoldToInventory; this route retires any open
    /// Square link on the original and writes the audit rows.
    /// </summary>
    [Function("SoldToInventory")]
    public async Task<IActionResult> SoldToInventory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/{id}/sold-to-inventory")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);

        // What the original looked like BEFORE — the proc validates the rest.
        var orig = await conn.QueryFirstOrDefaultAsync(
            "SELECT publish_state, checkout_link_id, invoice_id FROM dbo.manifests WHERE id = @id", new { id });
        if (orig == null) return new NotFoundResult();
        if (orig.invoice_id != null && (string?)orig.publish_state != "sold")
            return new ConflictObjectResult(new { error = "This box has an outstanding Square invoice — cancel the invoice first, or wait for it to be paid." });
        string? prevState = (string?)orig.publish_state;
        string? linkId = (string?)orig.checkout_link_id;

        // Retire the public Buy link on the original FIRST (same as
        // SquareReconcile's "pulled box" branch) so nobody can pay for a box
        // that reads SOLD. Done before the proc so a Square failure leaves the
        // box untouched — there is nothing to undo and staff simply retry.
        // (Once the box is sold, the invoice route refuses it and Reconcile
        // only sweeps fake-sold rows, so this is the one reliable moment.)
        if (linkId != null && _square.Configured)
        {
            try
            {
                await _square.DeletePaymentLinkAsync(linkId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SoldToInventory: could not retire Square link {Link} on BOX {Id} — box left as-is", linkId, id);
                return new ObjectResult(new { error = "Could not retire the Square Buy link on this box — try again in a moment." }) { StatusCode = 502 };
            }
            await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET checkout_link_id = NULL, checkout_order_id = NULL,
       checkout_url = NULL, checkout_created_at = NULL WHERE id = @id", new { id });
            _log.LogInformation("SoldToInventory: retired Square link {Link} on BOX {Id}", linkId, id);
        }

        dynamic? row;
        try
        {
            row = await conn.QueryFirstOrDefaultAsync(
                "EXEC dbo.sp_SoldToInventory @manifest_id = @id", new { id });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 50000)
        {
            // RAISERROR from the proc: ghost / already sold / archived / not found.
            return new ConflictObjectResult(new { error = ex.Message });
        }
        if (row == null) return new ObjectResult(new { error = "sp_SoldToInventory returned no rows" }) { StatusCode = 500 };

        Guid originalId = (Guid)row.original_id;
        Guid cloneId    = (Guid)row.clone_id;
        int  originalNo = (int)row.original_pallet_number;
        int  cloneNo    = (int)row.clone_pallet_number;

        // B7 audit rows: two on the original, one on the clone.
        var who = ClientPrincipal.UserDetails(req);
        await InsertHistoryAsync(conn, originalId, "publish_state", prevState, "sold", who);
        await InsertHistoryAsync(conn, originalId, "sold_to_inventory", null, $"BOX #{cloneNo}", who);
        await InsertHistoryAsync(conn, cloneId, "publish_state", null, $"draft (cloned from BOX #{originalNo})", who);

        _log.LogInformation("SoldToInventory: BOX #{Orig} -> SOLD (fake), clone BOX #{Clone} in draft ({N} items)",
            originalNo, cloneNo, (object?)row.items_copied);
        return new OkObjectResult(new
        {
            originalId,
            originalPalletNumber = originalNo,
            cloneId,
            clonePalletNumber = cloneNo,
            cloneDisplayName = (string?)row.clone_display_name,
            itemsCopied = (int)row.items_copied
        });
    }

    /// <summary>
    /// GET /api/pallets/{id}/history (Wishlist 4 B7) — newest first, capped at
    /// 200. Rows come from dbo.manifest_history (status + price + size + sell
    /// mode changes, plus Sold → inventory).
    /// </summary>
    [Function("PalletHistory")]
    public async Task<IActionResult> History(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}/history")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM dbo.manifests WHERE id = @id", new { id });
        if (exists == 0) return new NotFoundResult();

        var rows = (await conn.QueryAsync(@"
SELECT TOP 200 id, changed_at, changed_by, field, old_value, new_value
FROM dbo.manifest_history
WHERE manifest_id = @id
ORDER BY changed_at DESC, id DESC", new { id })).ToList();
        return new OkObjectResult(rows);
    }

    [Function("ListPalletItems")]
    public async Task<IActionResult> ListItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pallets/{id}/items")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var items = (await conn.QueryAsync(ItemsWithCatalogSql, new { id })).ToList();
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
        // Customer-safe column list — NO cost / wholesale / margin / notes, and
        // never sold_to_inventory_at (it would reveal a fake sale). is_hot_deal /
        // hot_deal_at live on dbo.manifests, not on v_public_pallets — join
        // rather than re-declare the view (see db/hot-deal-toggle.sql).
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(@"
SELECT v.manifest_id, v.pallet_number, v.display_name, v.category, v.publish_state,
       v.received_date, v.sold_at, v.live_at, v.photo_url, v.public_description,
       v.box_size, v.weight_lbs, v.item_count, v.unit_count, v.total_msrp, v.list_price, v.sale_price,
       v.condition_mix, v.highlight_title, v.highlight_msrp, v.highlight_photo,
       v.is_sold, v.is_on_sale, v.ask_price,
       m.is_hot_deal, m.hot_deal_at,
       CAST(CASE WHEN v.publish_state = 'live' AND v.live_at >= DATEADD(HOUR, -@hrs, SYSUTCDATETIME())
                 THEN 1 ELSE 0 END AS BIT) AS is_just_dropped
FROM dbo.v_public_pallets v
JOIN dbo.manifests m ON m.id = v.manifest_id
ORDER BY CASE WHEN v.publish_state = 'live' THEN 0 ELSE 1 END,
         COALESCE(v.live_at, v.sold_at, v.received_date) DESC", new { hrs = JustDroppedHours })).ToList();
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
       is_sold, is_on_sale, photo_url, public_description,
       box_size, weight_lbs, condition_mix, highlight_title, highlight_msrp, highlight_photo, live_at
FROM dbo.v_public_pallets WHERE manifest_id = @id", new { id });
        if (pallet == null) return new NotFoundResult();   // not public → don't leak it
        SignRowPhotos((object)pallet);

        // Margin-safe column list — NO unit_cost / wholesale_price / notes / lpn.
        var items = (await conn.QueryAsync(@"
SELECT title, brand, category, condition, qty, est_msrp, photo_blob_url, is_highlight
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
        string? condition, decimal? sellPrice, decimal? msrp, decimal? cost,
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
     title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
     created_at, enriched_at)
VALUES
    (@id, @mid, @qty, @cond, 'hit', 'manual',
     @title, @desc, @brand, @cat, @msrp, @sell, @cost, @whole, @notes,
     SYSUTCDATETIME(), SYSUTCDATETIME())",
            new
            {
                id = newId, mid = id, qty = body.qty ?? 1,
                cond = string.IsNullOrWhiteSpace(body.condition) ? "untested" : body.condition,
                title = body.title, desc = body.description, brand = body.brand, cat = body.category,
                msrp = body.msrp, sell = body.sellPrice, cost = body.cost, whole = body.wholesalePrice, notes = body.notes
            });

        _log.LogInformation("AddPalletItem {Item} -> pallet {Pallet}", newId, id);
        return new OkObjectResult(new { id = newId, manifest_id = id, title = body.title });
    }
}
