using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSL.Api.Services;
using Dapper;
using System.Text.Json;

namespace NSL.Api.Functions;

/// <summary>
/// Square checkout for one-of-a-kind boxes (SQUARE-INTEGRATION.md).
///
///   GET  /api/public/checkout-status   — is online buying on, plus the cart's
///                                        limits and Rob's delivery radius
///   POST /api/public/checkout          — mint/reuse ONE payment link for a cart of N boxes
///   POST /api/public/checkout/{id}     — legacy single-box route: a cart of one, pickup
///   POST /api/square/webhook           — payment.updated → fulfil the cart's boxes;
///                                        refund.updated → accumulate what went back
///                                        (HMAC-authed, anonymous route)
///   POST /api/square-reconcile         — staff-triggered sweep healing missed webhooks
///
/// Design invariants: one single-use link per CART (reuse is decided by our own
/// checkout_orders rows, not Square); SOLD only ever set via sp_SetPublishState;
/// webhook handler idempotent (UNIQUE square_payment_id + event replays no-op);
/// "paid" judged by tenders/net-due, never order state (stays OPEN forever).
/// </summary>
public sealed class SquareFunction
{
    private readonly SqlService _sql;
    private readonly SquareService _square;
    private readonly CheckoutFulfillment _fulfill;
    private readonly ILogger<SquareFunction> _log;

    public SquareFunction(SqlService sql, SquareService square, CheckoutFulfillment fulfill, ILogger<SquareFunction> log)
    {
        _sql = sql;
        _square = square;
        _fulfill = fulfill;
        _log = log;
    }

    public const int CartMax = 20;

    // Soft per-IP rate limit on the anonymous cart route. Same shape, same
    // eviction and the same 429 as MembersFunction's signup limiter (the house
    // pattern) — deliberately a copy rather than a second, cleverer design, so
    // the two public POSTs cannot behave differently when someone hammers them.
    //
    // 10 per 60 s per IP. A real shopper clicks Checkout a handful of times, and
    // a repeat click on an UNCHANGED cart is answered by the link-reuse query
    // without minting anything — only a genuine cart revision costs budget.
    // Twice the signup allowance because a household, an office and a phone on
    // carrier NAT all present as one address and must not lock each other out;
    // still nowhere near enough for a script walking subsets of the public box
    // ids, which is the abuse the old deterministic idempotency key used to cap.
    //
    // x-forwarded-for is caller-controlled (the edge APPENDS the real address to
    // whatever the client sent), so a spoofer can dodge the per-IP bucket. Two
    // backstops, as in MembersFunction: a global cap per window that no header
    // value can dodge, and a hard cap on the dictionary so junk keys cannot grow
    // it unbounded. 60 global leaves room for a busy drop day on one instance.
    private const int CheckoutRateLimitPerWindow = 10;
    private const int CheckoutGlobalLimitPerWindow = 60;
    private const int MaxTrackedIps = 5000;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (int count, DateTime windowStart)> _hits = new();
    private static readonly object _globalLock = new();
    private static int _globalCount;
    private static DateTime _globalWindowStart = DateTime.MinValue;

    // The zip list is read on every public page load, so keep an in-process
    // copy. Rob's edits show up within the TTL, and the checkout call itself
    // always re-reads dbo.delivery_zips, so the authority never goes stale.
    private static readonly TimeSpan ZipCacheTtl = TimeSpan.FromMinutes(5);
    // Deliberately NOT the same TTL, and not a bug to be "fixed" into one. A good
    // list is worth holding for MINUTES. A failed read is worth holding for
    // SECONDS: with no failure window at all, every public page load during a SQL
    // outage opens its own doomed connection; hold it too long and delivery stays
    // switched off for minutes after the database is already back. Ten seconds
    // bounds the storm and still recovers about as fast as anyone notices.
    private static readonly TimeSpan ZipFailureWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The cached zip list, when it was last read successfully, and when a read
    /// last failed. ONE reference field on purpose: this replaced a
    /// Nullable&lt;ValueTuple&lt;List, DateTime&gt;&gt;, a multi-word struct whose
    /// write is not atomic — a reader racing a refresh could observe HasValue
    /// true with Zips not yet written and NRE on a route every public page load
    /// hits. Publishing a record is a single atomic store, so a reader sees the
    /// whole snapshot or none of it.
    /// </summary>
    internal sealed record ZipCache(List<DeliveryZip> Zips, DateTime ReadAt, DateTime FailedAt);
    private static ZipCache? _zipCache;

    /// <summary>
    /// Serve <paramref name="cached"/> rather than re-reading delivery_zips?
    /// True while the last good read is inside <see cref="ZipCacheTtl"/>, and
    /// also while a FAILED read is inside the much shorter
    /// <see cref="ZipFailureWindow"/> — in which case what gets served is
    /// whatever the last good list was, exactly as before.
    /// </summary>
    internal static bool ServeCachedZips(ZipCache? cached, DateTime now)
        => cached != null && (now - cached.ReadAt < ZipCacheTtl || now - cached.FailedAt < ZipFailureWindow);

    /// <summary>
    /// Best-effort client address for the per-IP bucket: the first
    /// x-forwarded-for entry (per the contract) but only when it parses as a
    /// real IP (":port" suffix stripped); anything else falls back to the socket
    /// address. Never trusted for anything but rate limiting.
    /// Mirrors MembersFunction.ClientIp — keep the two identical.
    /// </summary>
    private static string ClientIp(HttpRequest req)
    {
        var fwd = req.Headers["x-forwarded-for"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (first.Length > 0 && first.Length <= 64)
            {
                // "1.2.3.4:5678" —> "1.2.3.4"; "[::1]:5678" —> "[::1]" (IPAddress.TryParse accepts the brackets)
                if (!first.StartsWith('[') && first.Count(c => c == ':') == 1) first = first[..first.IndexOf(':')];
                else if (first.StartsWith('[') && first.Contains("]:")) first = first[..(first.IndexOf("]:") + 1)];
                if (System.Net.IPAddress.TryParse(first, out var parsed)) return parsed.ToString();
            }
        }
        return req.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// True when this IP is over the limit for the current window, or when the
    /// whole instance is (global cap — a spoofed x-forwarded-for can't dodge that).
    /// Mirrors MembersFunction.OverRateLimit — keep the two identical.
    /// </summary>
    private static bool OverRateLimit(string ip)
    {
        var now = DateTime.UtcNow;

        bool globalOver;
        lock (_globalLock)
        {
            if (now - _globalWindowStart >= RateWindow) { _globalWindowStart = now; _globalCount = 0; }
            _globalCount++;
            globalOver = _globalCount > CheckoutGlobalLimitPerWindow;
        }
        if (globalOver) return true;

        var entry = _hits.AddOrUpdate(ip,
            _ => (1, now),
            (_, cur) => now - cur.windowStart >= RateWindow ? (1, now) : (cur.count + 1, cur.windowStart));
        // Cleanup so the dictionary never grows unbounded: drop expired windows
        // first; if it is still oversized (a flood of junk keys inside one
        // window) drop everything but the current key rather than keep growing.
        if (_hits.Count > MaxTrackedIps)
        {
            foreach (var kv in _hits)
                if (now - kv.Value.windowStart >= RateWindow) _hits.TryRemove(kv.Key, out _);
            if (_hits.Count > MaxTrackedIps)
                foreach (var kv in _hits)
                    if (kv.Key != ip) _hits.TryRemove(kv.Key, out _);
        }
        return entry.count > CheckoutRateLimitPerWindow;
    }

    /// <summary>
    /// Test seam: clear the process-wide limiter and zip-cache state so a test
    /// starts from a known window. Never called by the running app.
    /// </summary>
    internal static void ResetStaticStateForTests()
    {
        _hits.Clear();
        lock (_globalLock) { _globalCount = 0; _globalWindowStart = DateTime.MinValue; }
        _zipCache = null;
    }

    public const string FleaNote = "Fridays at the Raleigh Flea Market — we'll confirm the stall and time by phone.";
    public sealed record CheckoutRequest(Guid[]? ids, string? delivery, string? zip, string? address, string? memberNumber);

    /// <summary>One row of Rob's delivery radius: the zip and what reaching it costs.</summary>
    public sealed record DeliveryZip(string Zip, long FeeCents);

    [Function("CheckoutStatus")]
    public async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/checkout-status")] HttpRequest req,
        CancellationToken ct)
    {
        // Rob's delivery radius, not customer data — safe to publish, and the
        // drawer needs it to enable/disable the $10 radio without a round trip.
        // Still re-validated server-side on every checkout (spec 8.3/8.4).
        List<DeliveryZip> zips = new();
        if (_square.CheckoutEnabled)
        {
            var cached = _zipCache;
            if (ServeCachedZips(cached, DateTime.UtcNow))
            {
                zips = cached!.Zips;
            }
            else
            {
                try
                {
                    await using var conn = await _sql.OpenAsync(ct);
                    zips = (await conn.QueryAsync(
                        "SELECT zip, fee_cents FROM dbo.delivery_zips WHERE active = 1 ORDER BY zip"))
                        .Select(r => new DeliveryZip((string)r.zip, Convert.ToInt64(r.fee_cents)))
                        .ToList();
                    _zipCache = new ZipCache(zips, DateTime.UtcNow, DateTime.MinValue);
                }
                // Only OUR cancellation is left alone. Nothing inside this try
                // makes an HTTP call today, but the next person to add one must
                // inherit the right shape: a transport timeout arrives as
                // TaskCanceledException with ct un-signalled, and belongs here.
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Serve the last list we had rather than nothing: a quiet
                    // SQL hiccup here would otherwise hide the cart site-wide.
                    // Stamp the failure too, so the next few seconds of page loads
                    // are answered from here instead of each opening its own doomed
                    // connection. The last good list (and when we read it) rides
                    // along untouched, so recovery is as fast as it ever was.
                    zips = cached?.Zips ?? new List<DeliveryZip>();
                    _zipCache = new ZipCache(zips, cached?.ReadAt ?? DateTime.MinValue, DateTime.UtcNow);
                    _log.LogError(ex, "CheckoutStatus: delivery_zips query failed — serving the cached list ({N} zips) and holding off for {Seconds}s", zips.Count, ZipFailureWindow.TotalSeconds);
                }
            }
        }
        return new OkObjectResult(new
        {
            enabled = _square.CheckoutEnabled && _square.Configured,
            cartMax = CartMax,
            taxPercent = 7.25m,
            // What to quote BEFORE a zip is known. Once the shopper types one,
            // deliveryFees[zip] is the number they will actually be charged —
            // the same fee_cents this endpoint read and the checkout call puts
            // on the Square order, so the label and the receipt cannot disagree.
            deliveryCents = SquarePayloads.DeliveryCents,
            deliveryZips = zips.Select(z => z.Zip).ToList(),
            deliveryFees = zips.ToDictionary(z => z.Zip, z => z.FeeCents),
            fleaNote = FleaNote
        });
    }

    /// <summary>Legacy single-box route (tabs loaded before the cart deploy): a cart of one, pickup.</summary>
    [Function("CreateCheckoutOne")]
    public Task<IActionResult> CreateCheckoutOne(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout/{id}")] HttpRequest req,
        Guid id, CancellationToken ct)
        => CreateCartCheckoutCore(new[] { id }, DeliveryMethod.Pickup, null, null, null, ct);

    [Function("CreateCheckout")]
    public async Task<IActionResult> CreateCheckout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout")] HttpRequest req,
        CancellationToken ct)
    {
        // Anonymous, and every call past the reuse query mints a real Square
        // order and a checkout_orders row — so limit before doing any work at
        // all. checkout-status stays unlimited: it is a read the homepage makes
        // on every load.
        var ip = ClientIp(req);
        if (OverRateLimit(ip))
        {
            _log.LogWarning("CreateCheckout: rate limit hit for {Ip}", ip);
            return new ObjectResult(new { error = "Too many checkout attempts from this connection — try again in a minute." }) { StatusCode = 429 };
        }

        CheckoutRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<CheckoutRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException) { return new BadRequestObjectResult(new { error = "Invalid JSON" }); }
        if (!DeliveryMethods.TryParse(body?.delivery, out var method))
            return new BadRequestObjectResult(new { error = "Unknown delivery option.", field = "delivery" });
        return await CreateCartCheckoutCore(body?.ids ?? Array.Empty<Guid>(), method, body?.zip, body?.address, body?.memberNumber, ct);
    }

    /// <summary>
    /// Square caps order.reference_id at 40 characters and a full 20-box cart
    /// is ~144, so build the box list ONLY while it fits; past that a count is
    /// more use to Rob than a list truncated mid-number. This is a label for
    /// his Square dashboard, NOT a correlation key — the line-item uid is.
    /// SquarePayloads' Cap() is a backstop behind this, not the guard.
    /// </summary>
    public static string ReferenceId(IReadOnlyCollection<int> palletNumbers)
    {
        var boxList = "NSL " + string.Join(" ", palletNumbers.Select(n => "#" + n));
        return boxList.Length <= 40 ? boxList : $"NSL {palletNumbers.Count} boxes";
    }

    /// <summary>
    /// Can this box go in a cart? Available for a link, priced, and carrying a
    /// pallet number. That last clause is the defensive one: manifests.pallet_number
    /// is INT NULL and the number is what names the line on the buyer's receipt,
    /// so a box without one is treated as unavailable rather than guessed at. A
    /// live priced box should always have one — this is the third nullable-column
    /// cast found in this build, and the previous two would each have aborted a
    /// paid order.
    /// </summary>
    internal static bool Sellable(string? publishState, DateTime? archivedAt, bool isGhost,
        string? invoiceId, decimal? askPrice, int? palletNumber)
        => Availability.ForLink(publishState, archivedAt, isGhost, invoiceId)
           && askPrice is > 0
           && palletNumber.HasValue;

    private async Task<IActionResult> CreateCartCheckoutCore(Guid[] rawIds, DeliveryMethod method,
        string? rawZip, string? rawAddress, string? rawMember, CancellationToken ct)
    {
        if (!_square.CheckoutEnabled || !_square.Configured)
            return new ObjectResult(new { error = "Online checkout is not available right now." }) { StatusCode = 503 };

        var ids = rawIds.Where(g => g != Guid.Empty).Distinct().OrderBy(g => g).ToArray();
        if (ids.Length == 0) return new BadRequestObjectResult(new { error = "Add at least one box." });
        if (ids.Length > CartMax) return new BadRequestObjectResult(new { error = $"A cart holds at most {CartMax} boxes." });

        // Only a delivery order carries a zip/address; the other two ignore them.
        string? zip = null, address = null;
        // A delivery order always overwrites this from its own delivery_zips
        // row below. It starts at zero so that "nothing set it" can only ever
        // mean no delivery charge, never a charge nobody asked for.
        long deliveryFeeCents = 0;
        string? member = string.IsNullOrWhiteSpace(rawMember) ? null
                       : (rawMember!.Trim().Length == 7 ? rawMember.Trim() : null);

        // Shape check first: a malformed zip is answerable from the request
        // alone and must not cost a pooled SQL connection.
        if (method == DeliveryMethod.Delivery)
        {
            zip = (rawZip ?? "").Trim();
            // [0-9], not \d: in .NET \d is Unicode-aware and Arabic-Indic digits
            // satisfy it. The delivery_zips lookup refuses them a moment later so
            // nothing was exploitable, but the pattern should mean what a reader
            // thinks it means.
            if (!System.Text.RegularExpressions.Regex.IsMatch(zip, @"^[0-9]{5}$"))
                return new BadRequestObjectResult(new { error = "Enter a 5-digit zip code so we can check delivery.", field = "zip" });
        }

        await using var conn = await _sql.OpenAsync(ct);

        if (method == DeliveryMethod.Delivery)
        {
            // The browser's copy of the list is a convenience; this is the authority.
            var zipRow = await conn.QueryFirstOrDefaultAsync(
                "SELECT fee_cents FROM dbo.delivery_zips WHERE zip = @zip AND active = 1", new { zip });
            var outOfRange = $"We can't reach {zip} on our own truck — warehouse pickup and the Friday flea-market drop are both free.";
            if (zipRow == null)
                return new BadRequestObjectResult(new { error = outOfRange, field = "zip" });

            // fee_cents is the authority on what reaching this zip costs, and it
            // is what goes on the Square order — the same number checkout-status
            // published for this zip, so the drawer's label and the buyer's
            // receipt are the same figure by construction.
            deliveryFeeCents = Convert.ToInt64(zipRow.fee_cents);

            address = (rawAddress ?? "").Trim();
            if (address.Length == 0)
                return new BadRequestObjectResult(new { error = "Add the street address for the delivery.", field = "address" });
            if (address.Length > 300) address = address[..300];
        }

        var rows = (await conn.QueryAsync(@"
SELECT p.manifest_id, p.pallet_number, p.display_name, p.publish_state, p.is_ghost, p.archived_at, m.invoice_id,
       COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p JOIN dbo.manifests m ON m.id = p.manifest_id
WHERE p.manifest_id IN @ids", new { ids })).ToDictionary(r => (Guid)r.manifest_id);

        var unavailable = new List<Guid>();
        var lines = new List<CartLine>();
        var numbers = new List<int>();
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var b)) { unavailable.Add(id); continue; }
            decimal? ask = (decimal?)b.ask_price;
            // manifests.pallet_number is INT NULL (db/admin-portal-additions.sql).
            // An unconditional (int) cast raises InvalidCastException mid-request
            // and aborts a paid order, so read it as int? and let Sellable decide.
            int? palletNumber = (int?)b.pallet_number;
            bool isGhost = b.is_ghost == true;
            if (!Sellable((string?)b.publish_state, (DateTime?)b.archived_at, isGhost,
                          (string?)b.invoice_id, ask, palletNumber))
            { unavailable.Add(id); continue; }
            lines.Add(new CartLine(id, SquarePayloads.BoxLineName(palletNumber!.Value, (string?)b.display_name),
                (long)Math.Round(ask!.Value * 100m)));
            numbers.Add(palletNumber.Value);
        }
        if (unavailable.Count > 0)
            return new ConflictObjectResult(new
            {
                error = unavailable.Count == ids.Length
                    ? "These boxes are no longer available."
                    : "Some boxes in your cart are no longer available.",
                unavailable
            });

        // Reuse an open link with the IDENTICAL (box, amount) set AND the same
        // delivery choice — open + identical means current, because price/state
        // changes cancel links. A different delivery choice prices differently,
        // so it has to mint a new link (spec 5).
        //
        // delivery_cents is part of that identity now that the fee is per-zip:
        // without it, a link minted before Rob repriced a zip would be handed
        // back at the old fee forever. Missing a reuse only costs an extra
        // link; reusing an underpriced one costs the difference every time.
        var wanted = lines.ToDictionary(l => l.ManifestId, l => l.AmountCents);
        string methodDb = DeliveryMethods.ToDb(method);
        var candidates = (await conn.QueryAsync(@"
SELECT o.square_order_id, o.url, o.delivery_method, o.delivery_zip, o.delivery_address, b.manifest_id, b.amount_cents
FROM dbo.checkout_orders o
JOIN dbo.checkout_order_boxes b ON b.square_order_id = o.square_order_id
WHERE o.status = 'open' AND o.kind = 'link' AND o.url IS NOT NULL
  AND o.delivery_method = @method
  AND o.delivery_cents = @del
  AND ((o.delivery_zip IS NULL AND @zip IS NULL) OR o.delivery_zip = @zip)
  AND ((o.delivery_address IS NULL AND @addr IS NULL) OR o.delivery_address = @addr)
  AND o.square_order_id IN (SELECT square_order_id FROM dbo.checkout_order_boxes WHERE manifest_id = @first)",
            new { first = ids[0], method = methodDb, del = deliveryFeeCents, zip, addr = address })).GroupBy(r => (string)r.square_order_id);
        foreach (var g in candidates)
        {
            var set = g.ToDictionary(r => (Guid)r.manifest_id, r => (long)r.amount_cents);
            if (set.Count == wanted.Count && wanted.All(kv => set.TryGetValue(kv.Key, out var amt) && amt == kv.Value))
                return new OkObjectResult(new { url = (string)g.First().url });
        }

        var redirect = $"{_square.PublicBaseUrl}/thanks.html?boxes={string.Join(",", numbers)}";

        SquareService.CartLink link;
        {
            var (created, linkError) = await TryCreateCartLinkAsync(
                lines, numbers, redirect, method, methodDb, deliveryFeeCents, ct);
            if (linkError != null) return linkError;
            link = created!;
        }

        // Every money column below is Square's own number (spec 8.6) — we do
        // not recompute the tax, so our row can never disagree with the receipt.
        long subtotal = link.TotalCents - link.TaxCents - link.DeliveryCents;
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status,
                                 subtotal_cents, tax_cents, delivery_cents, total_cents,
                                 delivery_method, delivery_zip, delivery_address, member_number)
VALUES (@oid, 'link', @lid, @url, 'open', @sub, @tax, @del, @total, @method, @zip, @addr, @member)",
                new { oid = link.OrderId, lid = link.Id, url = link.Url,
                      sub = subtotal, tax = link.TaxCents, del = link.DeliveryCents, total = link.TotalCents,
                      method = methodDb, zip, addr = address, member }, transaction: tx);
            foreach (var l in lines)
                await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents) VALUES (@oid, @mid, @amt, @tax)",
                    new { oid = link.OrderId, mid = l.ManifestId, amt = l.AmountCents,
                          tax = link.LineTaxCents.TryGetValue(l.ManifestId, out var t) ? t : 0L }, transaction: tx);
            tx.Commit();
        }

        _log.LogInformation("CreateCheckout: {N} box(es) {Boxes} {Method} -> link {LinkId} order {OrderId} subtotal {Sub} tax {Tax} delivery {Del} total {Total}",
            lines.Count, string.Join(",", numbers), methodDb, link.Id, link.OrderId, subtotal, link.TaxCents, link.DeliveryCents, link.TotalCents);
        return new OkObjectResult(new { url = link.Url });
    }

    /// <summary>
    /// The Square payment-link call and the 502 that wraps its failures, lifted
    /// out of <see cref="CreateCartCheckoutCore"/> so the failure path can be
    /// exercised without a database standing behind it. Returns the link, or the
    /// answer the shopper gets.
    ///
    /// The catch filter is the whole point. HttpClient reports its OWN timeout as
    /// TaskCanceledException, which derives from OperationCanceledException — so a
    /// plain `ex is not OperationCanceledException` let the one failure this 502
    /// copy exists for (Square hung or slow) escape as a bare 500. We decline to
    /// handle it only when OUR token was actually signalled, i.e. the shopper
    /// really did disconnect; that still propagates, as it should.
    /// </summary>
    internal async Task<(SquareService.CartLink? Link, IActionResult? Error)> TryCreateCartLinkAsync(
        IReadOnlyList<CartLine> lines, IReadOnlyCollection<int> numbers, string redirect,
        DeliveryMethod method, string methodDb, long deliveryFeeCents, CancellationToken ct)
    {
        try
        {
            var link = await _square.CreateCartPaymentLinkAsync(lines, redirect,
                idempotencyKey: $"nsl-cart-{Guid.NewGuid():N}",
                referenceId: ReferenceId(numbers),
                paymentNote: SquarePayloads.PaymentNote(numbers), method, deliveryFeeCents, ct);
            return (link, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A 4xx from Square here is most likely a bad or moved
            // SQUARE_TAX_CATALOG_ID. The setting's ABSENCE falls back to the
            // ad-hoc tax; a WRONG value does not, it just fails. Log that guess
            // explicitly so the cause is obvious from App Insights.
            _log.LogError(ex, "CreateCheckout: Square rejected the payment link for boxes {Boxes} ({Method}). Most likely cause: SQUARE_TAX_CATALOG_ID is wrong or that catalog tax has moved/been deleted — an absent setting falls back to the ad-hoc tax, a wrong value does not.",
                string.Join(",", numbers), methodDb);
            return (null, new ObjectResult(new { error = "Couldn't start checkout — call us at (919) 526-0112 and we'll take care of you." }) { StatusCode = 502 });
        }
    }

    /// <summary>
    /// Square webhook. Anonymous HTTP route — Square's HMAC signature IS the
    /// authentication; anything unverified is dropped with 403. Always answers
    /// fast; heavy lifting is a couple of indexed queries.
    /// </summary>
    [Function("SquareWebhook")]
    public async Task<IActionResult> Webhook(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square/webhook")] HttpRequest req,
        CancellationToken ct)
    {
        string raw;
        using (var sr = new StreamReader(req.Body)) raw = await sr.ReadToEndAsync(ct);

        if (!_square.VerifyWebhookSignature(raw, req.Headers["x-square-hmacsha256-signature"].FirstOrDefault()))
        {
            _log.LogWarning("SquareWebhook: signature verification FAILED ({Len} bytes)", raw.Length);
            return new StatusCodeResult(403);
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException)
        {
            _log.LogWarning("SquareWebhook: body is not JSON ({Len} bytes)", raw.Length);
            return new OkObjectResult(new { ignored = "malformed" });
        }
        using var docScope = doc;
        var root = doc.RootElement;
        var type = SquareEvents.EventType(root);

        // Refunds: keep our audit row's running total honest, and clear the
        // attention flag once what was OWED has actually gone back — whether we
        // triggered the refund from the admin button or someone did it in the
        // Square Dashboard.
        if (type is "refund.updated" or "refund.created")
        {
            if (!SquareEvents.TryParseRefund(root, out var refund))
                return new OkObjectResult(new { ignored = "malformed" });

            // Square sends refund.updated on every state change. Only a settled
            // refund moves money: PENDING/APPROVED must leave the flag standing,
            // or a refund that later fails looks handled.
            bool settled = refund.Status == "COMPLETED" && refund.AmountCents is > 0;
            bool lost    = refund.Status is "FAILED" or "REJECTED";
            bool recorded = false;
            if (refund.PaymentId != null && (settled || lost))
            {
                await using var rconn = await _sql.OpenAsync(ct);
                if (settled)
                {
                    recorded = await _fulfill.RecordRefundAsync(
                        rconn, refund.RefundId, refund.PaymentId, refund.AmountCents!.Value);
                    _log.LogInformation("SquareWebhook: refund {RefundId} COMPLETED for payment {PaymentId} ({Amt}c) recorded={Rec}",
                        refund.RefundId, refund.PaymentId, refund.AmountCents, recorded);
                }
                else
                {
                    // The money did NOT go back. Re-raise the flag so the refund
                    // stays on someone's list — but ONLY where something is still
                    // owed, which is the exact negation of the test that clears
                    // the flag in RecordRefundAsync.
                    //
                    // Guarding on status <> 'REFUNDED' was the wrong question.
                    // Refunding exactly the owed amount clears the flag and leaves
                    // the status at PARTIAL_REFUNDED, because the order was not
                    // voided. Square does not guarantee ordering and retries across
                    // 24 hours, so a FAILED event for a DIFFERENT, earlier attempt
                    // then passed that guard: needs_refund went back to 1 and the
                    // correct PARTIAL_REFUNDED was clobbered on a payment that is
                    // fully square. Nothing dedupes this branch by refund id
                    // either, so one such event could toggle the flag over and
                    // over. A false "needs refund" row on the staff sales page is
                    // exactly what db/hotfix-floor-payments.sql exists to punish:
                    // cry wolf twice and the owners stop reading the flag when it
                    // is real.
                    //
                    // An unknown owed amount (both columns NULL) is NOT "nothing
                    // owed" — RecordRefundAsync will never clear the flag there, so
                    // re-raising it cannot contradict a settled payment, and the
                    // REFUND_FAILED status is what tells staff why it is standing.
                    var reflagged = await rconn.ExecuteAsync(@"
UPDATE dbo.payments SET needs_refund = 1, status = 'REFUND_' + @rs
WHERE square_payment_id = @pid
  AND (COALESCE(refund_due_cents, amount_cents) IS NULL
       OR refunded_cents < COALESCE(refund_due_cents, amount_cents))",
                        new { pid = refund.PaymentId, rs = refund.Status });
                    // Say which of the two actually happened. That UPDATE is now
                    // DESIGNED to match nothing in the ordinary case — a stale FAILED
                    // event for a payment that has since been made whole — so logging
                    // "re-flagged" unconditionally, at Error, would report a re-flag
                    // that did not happen on precisely the scenario the owed-based
                    // guard exists to create. Someone reading the logs during a real
                    // incident would chase it. A genuine re-flag stays Error; the
                    // no-op is the system working.
                    if (reflagged > 0)
                        _log.LogError("SquareWebhook: refund {RefundId} {Status} for payment {PaymentId} — attention flag re-raised, money is still owed",
                            refund.RefundId, refund.Status, refund.PaymentId);
                    else
                        _log.LogInformation("SquareWebhook: refund {RefundId} {Status} for payment {PaymentId} — ignored: nothing is owed on that payment (or we hold no payment row for it), so no flag was raised",
                            refund.RefundId, refund.Status, refund.PaymentId);
                }
            }
            return new OkObjectResult(new { refund = refund.Status, recorded });
        }

        if (type != "payment.updated" && type != "payment.created")
            // Subscribed but not relevant — or not an event shape at all. Either
            // way a 200: a 500 here buys us ~11 Square retries over 24 hours.
            return new OkObjectResult(new { ignored = type ?? "malformed" });

        if (!SquareEvents.TryParsePayment(root, out var pay))
            return new OkObjectResult(new { ignored = "malformed" });
        // payment.updated fires several times per payment (APPROVED, then
        // COMPLETED, ...). Only COMPLETED sells a box; the payments row's
        // UNIQUE square_payment_id dedupes the rest.
        if (pay.Status != "COMPLETED")
            return new OkObjectResult(new { ignored = pay.Status ?? "malformed" });

        IActionResult Floor()
        {
            _log.Log(pay.Product == null ? LogLevel.Warning : LogLevel.Information,
                "SquareWebhook: ignoring {Product} payment {PaymentId} (floor/other)", pay.Product ?? "unknown", pay.PaymentId);
            return new OkObjectResult(new { ignored = "floor" });
        }

        // No order id means there is nothing to look up, so answer before taking
        // a connection — the counter raises plenty of these.
        if (pay.OrderId == null && IsFloorPayment(knownOrder: false, pay.Product))
            return Floor();

        await using var conn = await _sql.OpenAsync(ct);

        bool known = pay.OrderId != null && await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.checkout_orders WHERE square_order_id = @oid", new { oid = pay.OrderId }) > 0;
        if (IsFloorPayment(known, pay.Product))
            return Floor();

        var r = await _fulfill.FulfillOrderAsync(conn, pay.OrderId, pay.PaymentId, pay.AmountCents, raw, "square", ct);
        return new OkObjectResult(FulfillmentBody(r));
    }

    /// <summary>
    /// Is this payment the floor POS's rather than ours? The Square merchant
    /// account is shared with the counter, so a cash sale at the register raises
    /// payment.updated here too. It is ours only if it belongs to an order we
    /// minted, or came from one of our own products (payment link / invoice) —
    /// a counter sale is neither, and must be answered 200 and left unrecorded.
    /// db/hotfix-floor-payments.sql exists because two RETAIL cash sales were
    /// once recorded and flagged as owing a refund they did not owe.
    /// </summary>
    public static bool IsFloorPayment(bool knownOrder, string? product)
        => !knownOrder && !SquareEvents.IsOurProduct(product);

    /// <summary>
    /// The webhook's 200 body for a fulfilment. `sold` is
    /// <see cref="FulfillResult.NewlySold"/>, never <see cref="FulfillResult.Sold"/>:
    /// on a second tender against an order we already fulfilled every box reads
    /// 'sold' — sold by US, on the earlier payment — and reporting that tally
    /// tells the caller N boxes just sold, firing the buyer's confirmation again
    /// for boxes they already have. `refundDue` is tax-inclusive (spec §8.8).
    /// </summary>
    public static object FulfillmentBody(FulfillResult r) => r.Outcome switch
    {
        "duplicate" => new { duplicate = true },
        "unmatched" => new { unmatched = true },
        _ => new
        {
            fulfilled = true,
            sold = r.NewlySold,
            unavailable = r.Unavailable,
            refundDue = r.RefundDueCents,
            boxes = r.PalletNumbers,
            unavailableBoxes = r.UnavailablePalletNumbers,
        },
    };

    public sealed record InvoiceBoxRequest(string? email, string? name, decimal? price);

    /// <summary>
    /// POST /api/pallets/{id}/invoice — wholesale flow: email a real Square
    /// invoice (card or ACH) for this box. The invoice's Square order is
    /// recorded in dbo.checkout_orders like any other checkout order, which is
    /// how the webhook recognises it, so payment.updated COMPLETED marks the box
    /// SOLD with no new logic (ACH-safe: PENDING doesn't sell the box; a failed
    /// ACH never completes). Invoicing retires every open cart link holding the
    /// box and parks it in draft (reserved, off-site).
    /// </summary>
    [Function("InvoiceBox")]
    public async Task<IActionResult> InvoiceBox(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/{id}/invoice")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };

        InvoiceBoxRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<InvoiceBoxRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }
        if (string.IsNullOrWhiteSpace(body?.email) || !body.email.Contains('@'))
            return new BadRequestObjectResult(new { error = "A valid buyer email is required." });

        await using var conn = await _sql.OpenAsync(ct);
        var box = await conn.QueryFirstOrDefaultAsync(@"
SELECT p.manifest_id, p.pallet_number, p.display_name, p.publish_state, p.is_ghost, p.archived_at,
       m.invoice_id,
       COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p JOIN dbo.manifests m ON m.id = p.manifest_id
WHERE p.manifest_id = @id", new { id });
        if (box == null) return new NotFoundResult();
        if ((string)box.publish_state == "sold")
            return new ConflictObjectResult(new { error = "This box is already sold." });
        if (box.is_ghost == true)
            return new ConflictObjectResult(new { error = "Can't invoice a fictitious box." });
        if (box.invoice_id != null)
            return new ConflictObjectResult(new { error = "This box already has an outstanding invoice — cancel it first." });

        var price = body.price is > 0 ? body.price.Value : (decimal?)box.ask_price;
        if (price is null or <= 0)
            return new BadRequestObjectResult(new { error = "No price — set a box price or pass one." });

        // Retire every open cart link holding this box (DB-first fence), then
        // create the invoice. If Square fails below, the box is simply
        // link-less until a shopper re-adds it — never a live box with a dead link.
        List<CanceledLink> canceled;
        using (var tx0 = conn.BeginTransaction())
        {
            canceled = await _fulfill.CancelOpenLinksForBoxesAsync(conn, tx0, new[] { id }, null);
            tx0.Commit();
        }
        await _fulfill.RetireLinksAsync(conn, canceled, ct);

        var customerId = await _square.FindOrCreateCustomerAsync(body.email.Trim(), body.name, ct);
        var name = $"BOX #{box.pallet_number} — {(string?)box.display_name ?? "NSL Box"}";
        var inv = await _square.CreateInvoiceAsync(
            name, (long)Math.Round(price.Value * 100m), customerId,
            title: "North State Liquidators", invoiceNumber: $"BOX-{box.pallet_number}", ct);

        // The invoice's Square order is recorded like any other checkout order:
        // the webhook identifies our orders from dbo.checkout_orders, so without
        // this row a paid invoice would land as an UNMATCHED payment flagged for
        // a refund, with the box never marked sold.
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET invoice_id = @iid, invoice_url = @iurl WHERE id = @id",
                new { id, iid = inv.InvoiceId, iurl = inv.PublicUrl }, transaction: tx);
            // subtotal_cents carries the same figure as total_cents, not the column
            // default of zero: an invoice has no delivery fee and no tax in this
            // table, so the schema's invariant (total = subtotal + tax + delivery)
            // only holds if the goods figure is the whole figure. A consumer that
            // sums subtotal_cents for a goods column — the sales dashboard — would
            // otherwise under-report every invoice by its entire value, and that
            // reads as missing revenue rather than as a bug.
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, url, status, subtotal_cents, total_cents)
VALUES (@oid, 'invoice', @url, 'open', @total, @total)",
                new { oid = inv.OrderId, url = inv.PublicUrl, total = (long)Math.Round(price.Value * 100m) }, transaction: tx);
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents) VALUES (@oid, @mid, @amt)",
                new { oid = inv.OrderId, mid = id, amt = (long)Math.Round(price.Value * 100m) }, transaction: tx);
            tx.Commit();
        }
        // Reserved for the buyer: off the public site while the invoice is out.
        if ((string)box.publish_state == "live")
        {
            await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @id, @publish_state = 'draft'", new { id });
            await PalletsFunction.InsertHistoryAsync(conn, id, "publish_state", "live", "draft", ClientPrincipal.UserDetails(req));
        }

        _log.LogInformation("InvoiceBox: BOX #{Num} invoiced to {Email} for {Price} (invoice {Inv})",
            (object?)box.pallet_number, body.email, price, inv.InvoiceId);
        return new OkObjectResult(new { invoiceId = inv.InvoiceId, url = inv.PublicUrl, status = inv.Status, price });
    }

    /// <summary>
    /// POST /api/pallets/{id}/invoice-cancel — cancel the outstanding invoice,
    /// close its checkout_orders row and clear the box's invoice fields so it
    /// can go back on the site.
    /// </summary>
    [Function("CancelBoxInvoice")]
    public async Task<IActionResult> CancelBoxInvoice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pallets/{id}/invoice-cancel")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var box = await conn.QueryFirstOrDefaultAsync(
            "SELECT invoice_id, publish_state, pallet_number FROM dbo.manifests WHERE id = @id", new { id });
        if (box == null) return new NotFoundResult();
        if (box.invoice_id == null) return new ConflictObjectResult(new { error = "No outstanding invoice on this box." });
        if ((string)box.publish_state == "sold")
            return new ConflictObjectResult(new { error = "Box is sold — the invoice was paid; refund instead." });

        await _square.CancelInvoiceAsync((string)box.invoice_id, ct);
        // ONE transaction, and it is not housekeeping. These two statements used
        // to be a single statement and could not land half-applied; as an
        // unwrapped two-statement batch each one auto-commits on its own, and an
        // Azure SQL transient failover between them (routine, not exotic) would
        // close the order row while the box still carried its invoice id. Both
        // land or neither does.
        //
        // That alone only NARROWED the dead end, it did not close it. Square is
        // called before this transaction and is not rolled back with it, so a
        // transaction that fails entirely still leaves the invoice cancelled at
        // Square and the id on our box. Closing it took the other half:
        // CancelInvoiceAsync now answers "already CANCELED at Square" (and 404)
        // as success rather than throwing, so this retry reaches the SQL below
        // and clears the box. Do not make that call strict again without also
        // moving it inside — or after — this transaction.
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
FROM dbo.checkout_orders o
WHERE o.kind = 'invoice' AND o.status = 'open'
  AND EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b WHERE b.square_order_id = o.square_order_id AND b.manifest_id = @id);
UPDATE dbo.manifests SET invoice_id = NULL, invoice_url = NULL WHERE id = @id",
                new { id }, transaction: tx);
            tx.Commit();
        }
        _log.LogInformation("CancelBoxInvoice: BOX #{Num} invoice canceled", (object?)box.pallet_number);
        return new OkObjectResult(new { canceled = true });
    }

    /// <summary>
    /// GET /api/square-payments — staff view of our payment audit trail, box
    /// context joined in. Flagged rows (needs_refund) first.
    ///
    /// The box columns are three different questions and the page asks all
    /// three, because answering only the first is how the attention table came
    /// to print "no box matched" beside "box was already sold":
    ///
    ///   boxes             — the SOLD boxes, as "#12, #14". NULL when none sold
    ///                       AND ALSO when a box sold but its manifest row was
    ///                       later deleted, which is why the counts exist.
    ///   sold_boxes /
    ///   unavailable_boxes — outcomes on this order, counted from
    ///                       checkout_order_boxes, which outlives its manifest
    ///                       by design (db/cart-checkout.sql).
    ///   box_lines         — how many boxes were on the order at all. Zero here
    ///                       is the only state that honestly reads "matched no
    ///                       order".
    /// </summary>
    [Function("ListSquarePayments")]
    public async Task<IActionResult> ListPayments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "square-payments")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(new CommandDefinition(@"
SELECT TOP 100 p.square_payment_id, p.square_order_id, p.manifest_id,
       p.amount_cents, p.refunded_cents, p.refund_due_cents, p.status, p.needs_refund, p.created_at,
       m.pallet_number, m.display_name,
       o.delivery_method, o.tax_cents, o.delivery_cents,
       (SELECT STRING_AGG('#' + CAST(m2.pallet_number AS VARCHAR(10)), ', ') WITHIN GROUP (ORDER BY m2.pallet_number)
        FROM dbo.checkout_order_boxes b JOIN dbo.manifests m2 ON m2.id = b.manifest_id
        WHERE b.square_order_id = p.square_order_id AND b.outcome = 'sold') AS boxes,
       bx.box_lines, bx.sold_boxes, bx.unavailable_boxes
FROM dbo.payments p
LEFT JOIN dbo.manifests m ON m.id = p.manifest_id
LEFT JOIN dbo.checkout_orders o ON o.square_order_id = p.square_order_id
OUTER APPLY (
    SELECT COUNT(*) AS box_lines,
           COALESCE(SUM(CASE WHEN b.outcome = 'sold'        THEN 1 ELSE 0 END), 0) AS sold_boxes,
           COALESCE(SUM(CASE WHEN b.outcome = 'unavailable' THEN 1 ELSE 0 END), 0) AS unavailable_boxes
    FROM dbo.checkout_order_boxes b WHERE b.square_order_id = p.square_order_id) bx
ORDER BY p.needs_refund DESC, p.created_at DESC", cancellationToken: ct))).ToList();
        return new OkObjectResult(rows);
    }

    public sealed record RefundRequest(string? paymentId, string? reason, long? amountCents);

    /// <summary>
    /// What one click of Refund is allowed to send: either an <see cref="Amount"/>
    /// to put to Square, or a <see cref="Refusal"/> to show the staff member.
    /// </summary>
    internal readonly record struct RefundPlan(long Amount, string? Refusal)
    {
        public bool Allowed => Refusal == null;
    }

    /// <summary>
    /// The refund arithmetic, pure (spec §8.8), so it can be tested rather than
    /// described.
    ///
    /// <paramref name="totalCents"/> NULL is not zero. An unknown total concludes
    /// nothing — the same ruling CheckoutFulfillment applies to the attention
    /// flag — so we refuse rather than send a number we do not have. The staff
    /// exit from that state is <see cref="LookUpPaymentAmount"/>, not a guess.
    ///
    /// Refunds ACCUMULATE, so both bars move as money goes back: the payment's
    /// remainder (total − refunded) caps anything we send, and what is OWED
    /// (refund_due_cents, tax-inclusive per spec §8.8) MINUS what has already
    /// gone back is the default. Subtracting <paramref name="refundedCents"/>
    /// from the owed figure is not tidiness: a second click on a row whose owed
    /// amount is partly settled would otherwise send the whole owed figure
    /// again and over-refund the buyer, because an explicit amount is clamped
    /// only against the payment remainder, which is much larger.
    ///
    /// A settled partial refund is never escalated into a refund of the rest of
    /// the payment: when nothing is owed and nothing was typed, we say so.
    /// </summary>
    internal static RefundPlan PlanRefund(long? totalCents, long refundedCents, long? refundDueCents, long? requestedCents)
    {
        if (totalCents is null or <= 0)
            return new RefundPlan(0,
                "No amount on record for this payment, so we cannot work out what to send. Use “Get amount from Square” on this row first.");
        long remaining = totalCents.Value - refundedCents;
        if (remaining <= 0)
            return new RefundPlan(0, "Already refunded in full — nothing left on this payment to send back.");

        long due = Math.Min(refundDueCents ?? totalCents.Value, totalCents.Value);
        long owed = Math.Max(0, due - refundedCents);
        if (requestedCents is > 0)
            return new RefundPlan(Math.Min(requestedCents.Value, remaining), null);
        if (owed == 0)
            return new RefundPlan(0,
                "Nothing outstanding to refund on this payment — what was owed has already gone back. Refund the rest in the Square Dashboard if you mean to.");
        return new RefundPlan(Math.Min(owed, remaining), null);
    }

    /// <summary>
    /// POST /api/square-refund — refund from the admin. The default amount is
    /// what is OWED (refund_due_cents for a partial-unavailable cart, else the
    /// remainder of the payment); an explicit amountCents is clamped to the
    /// remainder for staff-initiated partials. Bookkeeping goes through
    /// RecordRefundAsync so the refund.updated webhook cannot double count.
    ///
    /// Square's RefundPayment is AMOUNT-ONLY — there is no way to say "return
    /// this box and its tax"; itemised returns belong to the Orders
    /// returns/exchanges flow, which payment links do not give us. So
    /// refund_due_cents is stored tax-inclusive (spec §8.8) and we just send it.
    /// A staff-typed amountCents is NOT grossed up for tax — whoever types it
    /// owns it; the admin button's title attribute says so.
    ///
    /// ONLY A SETTLED REFUND CLEARS THE ATTENTION FLAG, which is the invariant
    /// the webhook now honours and which this route used to contradict: it set
    /// needs_refund = 0 the moment Square ACCEPTED the refund, so a refund that
    /// later FAILED looked handled on the sales page until the failure event
    /// landed — and forever if that event was missed. PENDING/APPROVED is Square
    /// taking the request, not money moving, so the flag is left exactly as it
    /// was and the refund.updated webhook clears it when the money does.
    ///
    /// The already-refunded guard used to trip only on status == 'REFUNDED',
    /// which no longer includes PARTIAL_REFUNDED, so a click on a partly
    /// refunded row asked Square for the FULL original amount, Square refused,
    /// SquareService threw, and staff got an opaque 500 mid-task with a customer
    /// waiting. PlanRefund subtracts what has gone back and refuses in words.
    /// </summary>
    [Function("SquareRefund")]
    public async Task<IActionResult> Refund(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-refund")] HttpRequest req,
        CancellationToken ct)
    {
        RefundRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<RefundRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }
        if (string.IsNullOrWhiteSpace(body?.paymentId))
            return new BadRequestObjectResult(new { error = "paymentId is required" });

        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            "SELECT square_payment_id, amount_cents, refunded_cents, refund_due_cents, status FROM dbo.payments WHERE square_payment_id = @pid",
            new { pid = body.paymentId }, cancellationToken: ct));
        if (row == null) return new NotFoundObjectResult(new { error = "Payment not found in our records." });

        var plan = PlanRefund((long?)row.amount_cents, (long)row.refunded_cents, (long?)row.refund_due_cents, body.amountCents);
        if (!plan.Allowed) return new ConflictObjectResult(new { error = plan.Refusal });
        long cents = plan.Amount;

        using var result = await _square.RefundPaymentAsync(body.paymentId, cents, body.reason, ct);
        var refund = result.RootElement.GetProperty("refund");
        var refundId = refund.GetProperty("id").GetString()!;
        var refundStatus = refund.TryGetProperty("status", out var rs) ? rs.GetString() ?? "PENDING" : "PENDING";
        if (refundStatus == "COMPLETED")
            await _fulfill.RecordRefundAsync(conn, refundId, body.paymentId, cents);
        else
            // PENDING/APPROVED is Square accepting the request, not money back.
            // Leave needs_refund exactly as it was so the row stays on the
            // Needs-attention list until the refund.updated webhook confirms.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.payments SET status = 'REFUND_' + @rs WHERE square_payment_id = @pid AND status NOT IN ('REFUNDED')",
                new { rs = refundStatus, pid = body.paymentId }, cancellationToken: ct));
        _log.LogInformation("SquareRefund: payment {PaymentId} refund {RefundId} {Amt}c -> {Status}", body.paymentId, refundId, cents, refundStatus);
        return new OkObjectResult(new
        {
            paymentId = body.paymentId, refundId, refundStatus,
            amountCents = cents,
            settled = refundStatus == "COMPLETED",
        });
    }

    public sealed record PaymentAmountRequest(string? paymentId);

    /// <summary>
    /// POST /api/square-payment-amount — ask Square what a payment we recorded
    /// with NO amount was actually for, write it down, and re-test the attention
    /// flag against it.
    ///
    /// WHY THIS EXISTS. An unknown total concludes nothing: no refund may clear
    /// the flag on a payment whose amount we do not know, because the
    /// alternative declared a whole payment refunded on the strength of a
    /// dollar. Correct — but it closes the only automatic exit, and the refund
    /// route closes the manual one by refusing outright and telling staff to
    /// refund the payment in the Square Dashboard instead. The sequence that
    /// leaves is: a payment lands with no amount, it gets flagged, Rob refunds
    /// it by hand exactly as instructed, and the row sits on the Needs-attention
    /// list permanently with nothing he can click. That is the same cry-wolf
    /// failure the refund rules exist to prevent, arriving from the other side,
    /// and worse because the system instructed him into it.
    ///
    /// This does NOT pretend a refund happened — it fills in the one missing
    /// fact and lets the ordinary rules apply again. A payment Rob already
    /// refunded by hand (whose refund.updated we DID record into refunded_cents,
    /// while being unable to conclude anything from it) clears on the spot; one
    /// that was never refunded stays flagged, now with a Refund button that
    /// knows what to send.
    ///
    /// The UPDATE is a compare-and-swap on "amount_cents IS NULL": an amount
    /// already on record is never overwritten, and a double click is a no-op.
    /// refunded_cents is NOT taken from Square — it is our own running total,
    /// backed one-for-one by dbo.payment_refunds rows, and the reconcile sweep's
    /// idempotence guard depends on it never being set from anywhere else.
    /// </summary>
    [Function("SquarePaymentAmount")]
    public async Task<IActionResult> LookUpPaymentAmount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-payment-amount")] HttpRequest req,
        CancellationToken ct)
    {
        PaymentAmountRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<PaymentAmountRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }
        if (string.IsNullOrWhiteSpace(body?.paymentId))
            return new BadRequestObjectResult(new { error = "paymentId is required" });
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured, so there is nothing to ask." }) { StatusCode = 503 };

        await using var conn = await _sql.OpenAsync(ct);
        var before = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            "SELECT amount_cents FROM dbo.payments WHERE square_payment_id = @pid",
            new { pid = body.paymentId }, cancellationToken: ct));
        if (before == null) return new NotFoundObjectResult(new { error = "Payment not found in our records." });
        if ((long?)before.amount_cents is not null)
            return new ConflictObjectResult(new { error = "This payment already has an amount on record — use Refund." });

        using var doc = await _square.RetrievePaymentAsync(body.paymentId, ct);
        if (doc == null)
            return new NotFoundObjectResult(new { error = "Square has no payment with that id. Nothing was changed." });
        long? amount = doc.RootElement.TryGetProperty("payment", out var pay) &&
                       pay.TryGetProperty("amount_money", out var am) &&
                       am.TryGetProperty("amount", out var av) && av.TryGetInt64(out var a) ? a : null;
        if (amount is null or <= 0)
        {
            _log.LogError("SquarePaymentAmount: Square returned no amount for payment {PaymentId} either — the row stays flagged and the Square receipt is the only figure anyone can act on", body.paymentId);
            return new ConflictObjectResult(new { error = "Square did not give an amount for this payment either. Work it out from the Square receipt — this row cannot be settled from here." });
        }

        // Compare-and-swap. The status only ever RESOLVES here: a payment whose
        // recorded refunds already cover the newly known total is REFUNDED. A
        // partly-refunded or failed status is left alone — RecordRefundAsync and
        // the webhook own those words, and overwriting REFUND_FAILED with
        // PARTIAL_REFUNDED would delete the one signal saying why it is standing.
        var applied = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.payments SET
    amount_cents = @amt,
    status = CASE WHEN refunded_cents >= @amt THEN 'REFUNDED' ELSE status END,
    needs_refund = CASE WHEN refunded_cents >= COALESCE(refund_due_cents, @amt) THEN 0 ELSE needs_refund END
WHERE square_payment_id = @pid AND amount_cents IS NULL",
            new { pid = body.paymentId, amt = amount.Value }, cancellationToken: ct));

        // Read back rather than infer: the flag is decided by a CASE inside the
        // UPDATE, and the page's next line of copy depends on which way it went.
        var after = await conn.QueryFirstOrDefaultAsync<PaymentAfterLookup>(new CommandDefinition(@"
SELECT amount_cents AS AmountCents, refunded_cents AS RefundedCents,
       refund_due_cents AS RefundDueCents, status AS Status, needs_refund AS NeedsRefund
FROM dbo.payments WHERE square_payment_id = @pid",
            new { pid = body.paymentId }, cancellationToken: ct));
        bool stillFlagged = after?.NeedsRefund ?? true;
        _log.LogWarning("SquarePaymentAmount: payment {PaymentId} had no amount on record; Square says {Amount}c ({Rows} row(s) updated). needs_refund is now {Flag}.",
            body.paymentId, amount.Value, applied, stillFlagged ? 1 : 0);
        return new OkObjectResult(new
        {
            paymentId = body.paymentId,
            amountCents = after?.AmountCents ?? amount.Value,
            refundedCents = after?.RefundedCents ?? 0L,
            refundDueCents = after?.RefundDueCents,
            status = after?.Status,
            needsRefund = stillFlagged,
            cleared = !stillFlagged,
        });
    }

    /// <summary>The payment row as it stands after the amount was filled in.</summary>
    private sealed record PaymentAfterLookup(long? AmountCents, long RefundedCents,
                                             long? RefundDueCents, string? Status, bool NeedsRefund);

    /// <summary>
    /// GET /api/sales-summary?days=30 — the profit view Square alone can't
    /// give: Square knows every sale (floor + web, one account); our DB knows
    /// each box's cost. Web sales = Square payments whose order_id matches a
    /// dbo.payments row; everything else is floor/other. Margin only where a
    /// matched box has a cost roll-up.
    /// </summary>
    [Function("SalesSummary")]
    public async Task<IActionResult> SalesSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sales-summary")] HttpRequest req,
        CancellationToken ct)
    {
        var days = int.TryParse(req.Query["days"], out var d) ? Math.Clamp(d, 1, 365) : 30;
        var begin = DateTime.UtcNow.AddDays(-days);

        // The Square half is optional: admin-marked sales (B1) are DB-only and
        // must show even when Square is unconfigured (PR preview slot) or down.
        // square_error tells sales.js to show a one-line notice instead of the
        // whole page erroring out.
        var squarePayments = new List<JsonElement>();
        var payouts = new List<JsonElement>();
        string? squareError = null;
        if (!_square.Configured)
            squareError = "Square is not configured — showing admin-marked sales only.";
        else
        {
            try
            {
                squarePayments = await _square.ListPaymentsAsync(begin, ct);
                payouts = await _square.ListPayoutsAsync(begin, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SalesSummary: Square list failed — returning admin-marked sales only");
                squareError = "Square didn't answer — showing admin-marked sales only. " + ex.Message;
            }
        }

        await using var conn = await _sql.OpenAsync(ct);
        // A web sale is a Square payment against one of OUR checkout orders, and
        // what it SOLD is checkout_order_boxes — payments.manifest_id is NULL for
        // every box of a cart order (db/square-payments.sql), so the old join on
        // it classified every multi-box sale as "floor".
        //
        // The boxes are LEFT joined on purpose: an order that took money and could
        // hand nothing over (reconcile calls it paidNothingSold) is still a WEB
        // payment and must not be counted as floor revenue at its tax-inclusive
        // total. It lands here with zero goods, its tax and delivery in their own
        // columns, and a note saying so.
        //
        // MIN(o.*) is not an aggregate in spirit — checkout_orders is one row per
        // order, so MIN just satisfies the GROUP BY. Note these are the ORDER's
        // tax and delivery, which on a partial-unavailable order include tax for
        // boxes that did not sell; the refund beside it is where that comes back
        // out.
        var webRows = (await conn.QueryAsync(new CommandDefinition(@"
SELECT p.square_payment_id,
       STRING_AGG('#' + CAST(m.pallet_number AS VARCHAR(10)), ', ') WITHIN GROUP (ORDER BY m.pallet_number) AS boxes,
       MIN(m.pallet_number) AS pallet_number, MIN(m.display_name) AS display_name,
       COUNT(b.manifest_id) AS box_count,
       COALESCE(SUM(b.amount_cents), 0) AS goods_cents,   -- what we actually sold, EX tax
       MIN(o.tax_cents)       AS tax_cents,               -- per order, not per box
       MIN(o.delivery_cents)  AS delivery_cents,
       MIN(o.delivery_method) AS delivery_method,
       SUM(COALESCE(v.total_cost, v.total_cost_units)) AS cost,
       SUM(CASE WHEN b.manifest_id IS NOT NULL
                 AND COALESCE(v.total_cost, v.total_cost_units) IS NULL THEN 1 ELSE 0 END) AS cost_missing
FROM dbo.payments p
JOIN dbo.checkout_orders o ON o.square_order_id = p.square_order_id
LEFT JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id AND b.outcome = 'sold'
LEFT JOIN dbo.manifests m ON m.id = b.manifest_id
LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE p.created_at >= @begin
GROUP BY p.square_payment_id", new { begin }, cancellationToken: ct))).ToList();
        var webByPaymentId = webRows
            .GroupBy(r => (string)r.square_payment_id)
            .ToDictionary(g => g.Key, g => g.First());

        var sales = new List<SaleRow>();
        long squareCents = 0, webCents = 0, floorCents = 0, refundedCents = 0, taxAndDeliveryCents = 0;
        foreach (var p in squarePayments)
        {
            var status = p.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (status != "COMPLETED") continue;
            var pid = p.GetProperty("id").GetString()!;
            long amt = p.TryGetProperty("amount_money", out var am) &&
                       am.TryGetProperty("amount", out var av) ? av.GetInt64() : 0;
            long refunded = p.TryGetProperty("refunded_money", out var rm) &&
                            rm.TryGetProperty("amount", out var rv) ? rv.GetInt64() : 0;
            var created = p.TryGetProperty("created_at", out var ca) ? ca.GetString() : null;
            var isWeb = webByPaymentId.TryGetValue(pid, out var web);

            // A cost roll-up that is missing on ANY sold box makes the whole
            // payment's margin unknowable — a partial cost would read as profit.
            decimal? cost = null;
            if (isWeb && (int)web!.cost_missing == 0) cost = (decimal?)web.cost;
            long boxCount = isWeb ? (long)(int)web!.box_count : 0;

            // REVENUE IS GOODS ONLY (spec §8.6). Sales tax belongs to NCDOR and
            // the delivery fee covers Norm's truck: neither is ours, so neither
            // enters the sale amount, the margin, or ANY of the tiles. The row
            // and the tiles read the same split off one function so they cannot
            // drift — getting the row right while the Website tile still counts
            // tax is the exact failure that ruling exists to catch.
            var split = SplitSale(isWeb,
                paymentAmountCents: amt,
                goodsCents: isWeb ? (long)web!.goods_cents : 0,
                taxCents: isWeb ? (long)web!.tax_cents : 0,
                deliveryCents: isWeb ? (long)web!.delivery_cents : 0,
                cost: cost);

            squareCents += split.GoodsCents;
            refundedCents += refunded;
            if (isWeb) webCents += split.GoodsCents; else floorCents += split.GoodsCents;
            taxAndDeliveryCents += split.TaxAndDeliveryCents;

            sales.Add(new SaleRow(
                payment_id: pid,
                created_at: created,
                amount_cents: split.GoodsCents,
                refunded_cents: refunded,
                tax_cents: isWeb ? (long)web!.tax_cents : 0,
                delivery_cents: isWeb ? (long)web!.delivery_cents : 0,
                delivery_method: isWeb ? (string?)web!.delivery_method : null,
                channel: isWeb ? "web" : "floor",
                source: "square",
                pallet_number: isWeb ? (int?)web!.pallet_number : null,
                display_name: isWeb ? (string?)web!.display_name : null,
                boxes: isWeb ? (string?)web!.boxes : null,
                box_count: (int)boxCount,
                cost: cost,
                margin_cents: split.MarginCents,
                note: isWeb && boxCount == 0
                    ? "Paid, but no box could be handed over — a refund is owed. See Needs attention."
                    : null));
        }

        // B1: boxes marked SOLD in admin with no live Square payment on file.
        // Fake sales (Sold → inventory) are not revenue. Only a COMPLETED*
        // payments row counts as "already in the Square list" — a refunded or
        // refund-flagged row must not hide a later real re-sale of the same box.
        //
        // These rows are listed but NOT added to gross_cents: a floor sale rung
        // up on the Square terminal has no order link, so it is already in the
        // Square loop above as channel 'floor' with no box; when staff then mark
        // that box SOLD in admin (the normal counter workflow) the box shows up
        // here too. Folding admin_cents into gross would count that sale twice.
        var adminRows = (await conn.QueryAsync(@"
SELECT m.id AS manifest_id, m.pallet_number, m.display_name, m.sold_at,
       COALESCE(v.sale_price, v.list_price, v.total_wholesale) AS ask_price,
       v.total_cost, v.total_cost_units
FROM dbo.manifests m
JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE m.publish_state = 'sold'
  AND m.is_ghost = 0
  AND m.sold_to_inventory_at IS NULL
  AND m.sold_at >= @begin
  AND NOT EXISTS (SELECT 1 FROM dbo.payments p
                  JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id
                  WHERE b.manifest_id = m.id AND b.outcome = 'sold'
                    AND p.status <> 'REFUNDED')
ORDER BY m.sold_at DESC", new { begin })).ToList();

        long adminCents = 0;
        foreach (var a in adminRows)
        {
            decimal? ask = (decimal?)a.ask_price;
            long amt = ask is > 0 ? (long)Math.Round(ask.Value * 100) : 0;   // 0 = no price yet, still listed
            decimal? cost = (decimal?)(a.total_cost ?? a.total_cost_units);
            adminCents += amt;
            sales.Add(new SaleRow(
                payment_id: null,
                created_at: ((DateTime)a.sold_at).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                amount_cents: amt,
                refunded_cents: 0,
                tax_cents: 0, delivery_cents: 0, delivery_method: null,
                channel: "admin",
                source: "admin",
                pallet_number: (int?)a.pallet_number,
                display_name: (string?)a.display_name,
                boxes: null, box_count: 1,
                cost: cost,
                margin_cents: cost.HasValue && amt > 0 ? (long?)(amt - (long)Math.Round(cost.Value * 100)) : null,
                note: "Marked sold in admin — no Square payment linked to this box; if it was rung up on the terminal it is already in Floor / other"));
        }
        var squareCount = sales.Count - adminRows.Count;

        // Newest first across both sources (Square gives RFC 3339 strings).
        sales.Sort((x, y) => ParseWhen(y.created_at).CompareTo(ParseWhen(x.created_at)));

        var payoutList = payouts.Select(p => new
        {
            id = p.GetProperty("id").GetString(),
            status = p.TryGetProperty("status", out var s) ? s.GetString() : null,
            amount_cents = p.TryGetProperty("amount_money", out var am) &&
                           am.TryGetProperty("amount", out var av) ? av.GetInt64() : 0,
            arrival = p.TryGetProperty("arrival_date", out var ad) ? ad.GetString() : null
        }).ToList();

        return new OkObjectResult(new
        {
            days,
            gross_cents = squareCents,          // GOODS ONLY (web + floor) — see SplitSale
            square_cents = squareCents,
            web_cents = webCents,
            floor_cents = floorCents,
            admin_cents = adminCents,           // listed separately — may overlap floor_cents (see above)
            refunded_cents = refundedCents,
            // Money we collected and do not own: NCDOR's sales tax and the
            // delivery fee. Its own accumulator, its own tile, never in gross.
            tax_delivery_cents = taxAndDeliveryCents,
            sale_count = squareCount,           // Square sales only, matches gross_cents
            admin_count = adminRows.Count,
            square_error = squareError,         // null when Square answered
            sales,
            payouts = payoutList
        });
    }

    /// <summary>One row of the sales list; property names ARE the JSON keys (snake_case, like the Dapper rows).</summary>
    private sealed record SaleRow(
        string? payment_id, string? created_at, long amount_cents, long refunded_cents,
        long tax_cents, long delivery_cents, string? delivery_method,
        string channel, string source, int? pallet_number, string? display_name,
        string? boxes, int box_count,
        decimal? cost, long? margin_cents, string? note);

    /// <summary>What one payment contributes to revenue, to the held-money tile, and to margin.</summary>
    internal readonly record struct SaleSplit(long GoodsCents, long TaxAndDeliveryCents, long? MarginCents);

    /// <summary>
    /// The revenue rule, in one place (spec §8.6): TAX AND THE DELIVERY FEE ARE
    /// NEVER REVENUE AND NEVER MARGIN. Sales tax is money held for NCDOR; the
    /// $10 covers Norm's fuel. Both get their own column and their own tile.
    ///
    /// The row and the three tiles (Gross, Website, Floor) all take their figure
    /// from here, because the failure this guards against is arithmetic drift
    /// between them — a row that correctly reads $43.00 beside a Website tile
    /// that still reads $46.12 because it summed the payment instead.
    ///
    /// Floor sales are the honest gap. A terminal sale has no order and no line
    /// data, so whatever tax it collected is inside the payment amount with no
    /// way to separate it, and all of it is counted as goods. That overstates
    /// floor revenue by the tax and it is NOT fixable from here — it would need
    /// the Square order behind each terminal payment.
    ///
    /// <paramref name="refundedCents"/> is deliberately absent from the margin:
    /// boxes that came back are outcome='unavailable' and the goods query
    /// already excludes them, so subtracting the refund too would double count.
    /// The one case this reads high is a GOODWILL refund on a fully-sold order;
    /// refunded_cents is displayed next to it so staff can see that (spec §8.6,
    /// accepted imprecision).
    /// </summary>
    internal static SaleSplit SplitSale(bool isWeb, long paymentAmountCents, long goodsCents,
                                        long taxCents, long deliveryCents, decimal? cost)
    {
        long goods = isWeb ? goodsCents : paymentAmountCents;
        long held = isWeb ? taxCents + deliveryCents : 0;
        long? margin = isWeb && cost.HasValue ? goods - (long)Math.Round(cost.Value * 100) : null;
        return new SaleSplit(goods, held, margin);
    }

    private static DateTimeOffset ParseWhen(string? iso) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue;

    /// <summary>How many orders one sweep may ask Square about, per pass.</summary>
    public const int ReconcileMaxSquareCalls = 40;

    /// <summary>
    /// The half of pass 1's Square budget reserved for the backlog, and it may
    /// never be zero. A newest-first window alone starves its own far end: an
    /// invoice order is closed by no pass at all (correctly), so once more than a
    /// window's worth are open, the oldest are never looked at again and a paid
    /// one whose webhook was missed can never heal. Anything that could be hiding
    /// a payment — open orders of any age, and links we closed whose delete Square
    /// never confirmed — is drawn from here instead.
    ///
    /// It is spent as TWO reserved draws rather than one combined sample; see
    /// <see cref="ReconcileRetireRecheckWindow"/> for why that distinction is the
    /// difference between the recovery queue staying reachable and being diluted
    /// into invisibility by the open-order population.
    /// </summary>
    public const int ReconcileBacklogWindow = ReconcileMaxSquareCalls / 2;

    /// <summary>The rest of the budget: the newest open orders, where a webhook missed minutes ago shows up.</summary>
    public const int ReconcileFreshWindow = ReconcileMaxSquareCalls - ReconcileBacklogWindow;

    /// <summary>
    /// THE RECOVERY QUEUE'S RESERVED SHARE of the backlog budget, and why it has
    /// to be reserved rather than merely included.
    ///
    /// The unconfirmed-delete queue — links we closed whose delete Square never
    /// confirmed — is THE recovery net for a wrongly closed paid order: Square
    /// will not cancel the link of an order that was paid, so such a row never
    /// gets its stamp and sits there. It is the only route by which that charged
    /// buyer surfaces.
    ///
    /// When this round widened the backlog from "aged open orders, or the queue"
    /// to "any open order, or the queue", it also put the queue into a single
    /// random sample against the ENTIRE open-order population instead of against a
    /// small, self-draining aged subset. Two hundred open carts and five queue
    /// rows, and a given queue row's chance of being drawn in a run falls to a few
    /// per cent — the charged buyer's expected wait stretches by an order of
    /// magnitude. The comment on <see cref="ReconcileBacklogSql"/> costed the
    /// collision waste and called it self-limiting; it never costed this, which is
    /// the cost that matters.
    ///
    /// So the queue draws FIRST, up to this many rows, and the open-order draw
    /// takes what is left (<see cref="OpenBacklogCap"/>). Reserved, not
    /// ring-fenced: on a healthy floor the queue is empty and the open orders get
    /// the whole backlog window, exactly as before.
    /// </summary>
    public const int ReconcileRetireRecheckWindow = ReconcileBacklogWindow / 2;

    /// <summary>
    /// What is left of the backlog budget for open orders once the recovery queue
    /// has taken its reserved share. Pure, because the entire point of the split
    /// is an arithmetic claim about coverage.
    /// </summary>
    internal static int OpenBacklogCap(int queueRowsDrawn)
        => Math.Max(0, ReconcileBacklogWindow - Math.Min(Math.Max(queueRowsDrawn, 0), ReconcileRetireRecheckWindow));

    /// <summary>
    /// How long an unpaid cart link may stay payable before the sweep retires
    /// it. Square links never expire, so we must — but only once this run has
    /// established at Square that nobody paid it (see <see cref="VerifiedUnpaid"/>).
    /// </summary>
    public const int ReconcileLinkMaxAgeDays = 7;

    /// <summary>How many payments one sweep may repair orphaned refunds on.</summary>
    public const int ReconcileMaxRefundReplays = 40;

    /// <summary>What the sweep should do with one open order, given what Square says about it.</summary>
    internal enum ReconcileVerdict { Heal, CancelLink, StillOpen }

    /// <summary>
    /// What came back when the sweep asked Square about one order.
    /// <c>Order</c> — Square answered with an order we could read.
    /// <c>NotFound</c> — 404: Square holds no such order at all. Note that this
    /// is only evidence about OUR merchant if the run also proved it can see our
    /// merchant — see <see cref="SquareAnswered"/>.
    /// <c>Unreachable</c> — rate limit, rotated token, 5xx, a 2xx whose body was
    /// not the shape we parse, or an order object carrying no payment signal at
    /// all. We learned NOTHING about payment.
    /// </summary>
    internal enum SquareReply { Order, NotFound, Unreachable }

    /// <summary>One order pass 1 asked Square about, and what Square said about its money.</summary>
    internal sealed record OrderCheck(string OrderId, SquareReply Reply, bool Paid);

    /// <summary>
    /// THE MONEY RULE OF THIS SWEEP. Did this run ESTABLISH that the order
    /// carries no payment at Square?
    ///
    /// Nothing may permanently close an order on database state alone. The age
    /// rule used to: a buyer pays on day six, the webhook is missed, the day
    /// seven sweep closes the order because it is old, and the pass that asks
    /// Square only ever looked at open orders — so the charge became invisible,
    /// no box was sold, and no flag was raised anywhere. Closing is now gated on
    /// this answer, so an order we could not ask about simply stays open and is
    /// asked again next run.
    ///
    /// A 404 counts as established ONLY when this run also read at least one real
    /// order back from Square (<paramref name="squareAnswered"/>). Read the
    /// corrected reasoning on <see cref="SquareAnswered"/> before relaxing that:
    /// the earlier version of this comment claimed a wrong-merchant token put the
    /// money out of reach anyway, and that is simply false.
    /// </summary>
    internal static bool VerifiedUnpaid(SquareReply reply, bool paid, bool squareAnswered)
        => (reply == SquareReply.NotFound && squareAnswered)
        || (reply == SquareReply.Order && !paid);

    /// <summary>
    /// CORROBORATION, and the reason a 404 is not self-certifying.
    ///
    /// A credential pointed at the wrong merchant — a sandbox token in
    /// production, a re-created application, a location change after a migration
    /// — does not move anybody's money. Our buyers' orders and their payments stay
    /// exactly where they are. Only our ability to SEE them moves, and what we see
    /// instead is a 404 on every single call.
    ///
    /// That mattered because the sweep trusted a 404 twice over: once as proof
    /// the order was never paid (so pass 2 closed it), and again as proof the
    /// payment link was gone (so pass 3 stamped link_deleted_at on it). A row that
    /// is both closed AND stamped matches neither arm of the backlog window, so it
    /// is unreachable by every pass, permanently — including after somebody fixes
    /// the credential. One configuration mistake, applied silently to every aged
    /// order, with zero errors and a clean-looking report.
    ///
    /// So: one readable order anywhere in the run is the proof that our credential
    /// can see our merchant, and a healthy run essentially never fails to get one.
    /// Without it, no 404 closes anything and no delete is attempted — and the
    /// run says so out loud (see <see cref="LooksLikeWrongMerchant"/>), which is
    /// the only live misconfiguration alarm this system has.
    ///
    /// The cost is one quiet run's worth of delay in the genuinely rare case where
    /// every order in a window really has been forgotten by Square. It is retried
    /// next run, alongside orders that do answer.
    /// </summary>
    internal static bool SquareAnswered(IEnumerable<OrderCheck> checks)
        => checks.Any(c => c.Reply == SquareReply.Order);

    /// <summary>
    /// How many 404s a run must see before the wrong-merchant alarm may shout, and
    /// why that number is not one.
    ///
    /// One 404 is an ordinary event. Pair it with a rate-limit storm on everything
    /// else and the old condition — "some 404, and no readable order" — fired on a
    /// bad afternoon and blamed the credentials for it.
    ///
    /// The quiet floor was worse. Where the only rows a run draws are a couple of
    /// stale ones that 404, nothing corroborates the run, so by this sweep's own
    /// money rule those rows can never be closed or retired — they are drawn again
    /// next run, and the alarm fires EVERY run, indefinitely. That is alarm
    /// fatigue on the only misconfiguration detector this system has, which makes
    /// it worthless on the day it is finally right.
    ///
    /// Ten sits below the forty a wrong credential 404s in a full window and above
    /// the handful of permanently unanswerable rows a quiet floor carries. It is a
    /// floor on the SAMPLE, not a cure for the shape: a floor genuinely carrying
    /// ten dead rows and no live ones still repeats, and a floor that never has
    /// ten open orders at once cannot trip this detector at all. Both are named in
    /// the residuals in ReconcileTests — the credential is meant to be proved once
    /// at setup, not discovered here.
    /// </summary>
    public const int ReconcileWrongMerchantMinSample = 10;

    /// <summary>
    /// The alarm: we asked Square about a meaningful number of real orders and
    /// every answer that came back was "no such order". That is not a state our
    /// own data can produce — we only ever ask about orders we created — so it
    /// means the credential is looking at somebody else's merchant.
    ///
    /// THREE CONDITIONS, each excluding a different false alarm.
    /// NO READABLE ORDER (<see cref="SquareAnswered"/>) — one readable order and
    /// the credential demonstrably reaches our merchant.
    /// AT LEAST <see cref="ReconcileWrongMerchantMinSample"/> 404s — a quiet floor
    /// carrying a few permanently unanswerable rows must not shout every run
    /// forever.
    /// AND THOSE 404s OUTNUMBER EVERYTHING ELSE THE RUN SAW — a partial outage,
    /// one genuinely forgotten order among a 429 storm, says nothing about
    /// credentials, and satisfied the old condition outright.
    /// </summary>
    internal static bool LooksLikeWrongMerchant(IEnumerable<OrderCheck> checks)
    {
        var all = checks as IReadOnlyCollection<OrderCheck> ?? checks.ToList();
        int notFound = all.Count(c => c.Reply == SquareReply.NotFound);
        return notFound >= ReconcileWrongMerchantMinSample
            && notFound * 2 > all.Count
            && !SquareAnswered(all);
    }

    /// <summary>
    /// The orders pass 2 is allowed to consider closing: the ones pass 1 proved
    /// carry no payment. Everything else — unreachable, malformed, paid, or 404ed
    /// by a run that never once reached our own merchant — is left open for the
    /// next run, which is the whole point.
    /// </summary>
    internal static List<string> ClosableOrderIds(IReadOnlyCollection<OrderCheck> checks)
    {
        bool answered = SquareAnswered(checks);
        return checks.Where(c => VerifiedUnpaid(c.Reply, c.Paid, answered))
            .Select(c => c.OrderId).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// WHAT PASS 3 MAY ACTUALLY DELETE AT SQUARE — and the half of the fix that
    /// closes the defect, which is why it is a function and not an inline branch.
    ///
    /// Gating the CLOSING pass on corroboration (<see cref="ClosableOrderIds"/>)
    /// does not close it alone. Pass 3 does not only delete what this run closed:
    /// it also samples a STANDING QUEUE built by earlier healthy runs. A delete
    /// that 404s is read as confirmation, so at the wrong merchant every delete
    /// "succeeds" and stamps link_deleted_at — and a stamped row is out of the
    /// backlog recovery window for good. Those queue rows are exactly where a
    /// paid, wrongly closed order hides, which makes them the last rows on the
    /// floor that may be retired on an uncorroborated 404.
    ///
    /// So the corroboration gate is around BOTH sources. On the first it is
    /// redundant today — with no corroboration pass 2 closes nothing, so
    /// <paramref name="closedThisRun"/> is empty anyway — and deliberately so:
    /// the safety of this pass must not depend on a property of a different pass.
    ///
    /// Skipping costs one run of a dead link staying payable, and it is retried
    /// every run. Note it also skips on a run where every Square call failed
    /// outright, which is right for the same reason and costs the same.
    ///
    /// THIS RUN'S CANCELS ARE NEVER DROPPED FOR BUDGET: those links are payable
    /// RIGHT NOW, and the list cannot exceed the budget in any case — it is a
    /// subset of one window. The budget caps the standing-queue sample behind them.
    /// </summary>
    internal static List<CanceledLink> LinksToRetire(
        bool squareAnswered,
        IEnumerable<CanceledLink> closedThisRun,
        IEnumerable<CanceledLink> standingQueueSample,
        int budget)
    {
        var retire = new List<CanceledLink>();
        if (!squareAnswered) return retire;   // nothing proved this run can see our own merchant

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in closedThisRun)
            if (seen.Add(l.OrderId)) retire.Add(l);
        foreach (var l in standingQueueSample)
        {
            if (retire.Count >= budget) break;
            if (seen.Add(l.OrderId)) retire.Add(l);
        }
        return retire;
    }

    /// <summary>
    /// The one rule the Square-facing pass turns on, pulled out so it can be
    /// tested without a Square account. Note the asymmetry: a paid order is
    /// healed whatever its kind, but only a LINK is ever closed because Square
    /// says CANCELED. An invoice is canceled by explicit staff action
    /// (CancelBoxInvoice) and never by a sweep — closing one here would strand a
    /// real customer holding a real invoice we emailed them, and leave the box
    /// drafted with nothing pointing at it.
    ///
    /// PAID IS CHECKED FIRST, AND THAT ORDER IS LOAD-BEARING. CANCELED-and-paid is
    /// a contradiction Square should not produce, but if it does, the cancel
    /// branch would write link_deleted_at — and since this fix round made that
    /// stamp the thing that takes a row OUT of the backlog recovery window, a
    /// stamped row is a row no pass can ever look at again. Money first: send it
    /// to Heal, where fulfilment either sells the boxes or flags the refund the
    /// buyer is owed, and where the caller logs the contradiction at error.
    /// Nothing stamps a row dead while Square says there is money on it.
    /// </summary>
    internal static ReconcileVerdict VerdictFor(string? squareState, bool paid, string? kind)
        => paid ? ReconcileVerdict.Heal
         : squareState == "CANCELED" && kind == "link" ? ReconcileVerdict.CancelLink
         : ReconcileVerdict.StillOpen;

    /// <summary>One recorded refund, carried with its payment's two running totals.</summary>
    internal sealed record RecordedRefund(string PaymentId, string RefundId, long AmountCents,
        long RecordedCents, long AppliedCents);

    /// <summary>One payment to repair: cents to add, and how many refund rows that accounts for.</summary>
    internal sealed record RefundReplay(string PaymentId, long AmountCents, int RefundCount);

    /// <summary>
    /// Work out which payments have recorded refunds that never reached their
    /// running total, and how much each is short. Pure, so the arithmetic is
    /// testable; the money itself moves in
    /// <see cref="CheckoutFulfillment.ApplyRecordedRefundAsync"/>.
    ///
    /// The amount is always the payment-level shortfall — recorded minus
    /// applied — never the sum of the rows below, because those two differ
    /// whenever some of a payment's refunds DID apply. Getting that backwards
    /// would return the same money to the books twice.
    ///
    /// The row COUNT is reporting only, and it reads the rows oldest-first. That
    /// is not arbitrary: an orphan is by definition a refund recorded while the
    /// payments row did not exist yet, and once that row exists no later refund
    /// for the same payment can orphan — so the unapplied ones are the oldest.
    /// Where that assumption could be violated the count may over-report by a
    /// row; the cents never can, because they come from the totals.
    /// </summary>
    internal static List<RefundReplay> PlanRefundReplay(IEnumerable<RecordedRefund> rowsOldestFirst)
    {
        var plans = new List<RefundReplay>();
        foreach (var g in rowsOldestFirst.GroupBy(r => r.PaymentId))
        {
            var first = g.First();
            long shortfall = first.RecordedCents - first.AppliedCents;
            if (shortfall <= 0) continue;   // already square — run this as often as you like
            long covered = 0;
            int rows = 0;
            foreach (var r in g)
            {
                rows++;
                covered += r.AmountCents;
                if (covered >= shortfall) break;
            }
            plans.Add(new RefundReplay(first.PaymentId, shortfall, rows));
        }
        return plans;
    }

    /// <summary>
    /// The open-order draw of pass 1's backlog: orders a newest-first window can
    /// never reach again.
    ///
    /// OPEN ORDERS OF ANY AGE, because a newest-first window starves its far end
    /// and an invoice order is never closed by any pass, so without this an
    /// invoice that drifts past the cap can never heal. NOT just aged ones: this
    /// used to require created_at older than the link age limit, which left a gap
    /// — during a burst, an order a few days old sitting outside the newest-first
    /// half was asked about by NEITHER half, so a missed notification on it waited
    /// until it aged in. The cost of closing the gap is that the aged rows now
    /// share the draw with the young ones; the population is the open set either
    /// way, and the young ones were the half with a deadline, not the half that
    /// could be locked out forever. It also means this draw can take a row the
    /// newest-first half already took, and a duplicate is dropped rather than
    /// replaced, so a run can spend fewer than its full budget. That waste is
    /// self-limiting: the chance of collision is this window over the open set, so
    /// it is only material when the open set is small enough that both halves
    /// together cover all of it anyway.
    ///
    /// WHAT IS NO LONGER IN HERE, and that is the point. Widening this from aged
    /// orders to all open orders also widened the population the RECOVERY QUEUE
    /// had to compete with, from a small self-draining subset to every open cart
    /// on the floor. The queue now draws separately and first
    /// (<see cref="ReconcileRetireRecheckSql"/>), because a queue row's odds of
    /// being sampled are the one cost that mattered and the only one the old
    /// version of this comment did not count.
    ///
    /// ORDER BY NEWID() is a random sample, not sloppiness. There is no
    /// last-checked column to rotate on and this task may not add one; any fixed
    /// ordering lets the same rows occupy the window every single run while the
    /// ones behind them are never looked at again. A random sample of the same
    /// size gives every open order a chance on every run, so nothing is locked
    /// out permanently.
    /// </summary>
    internal const string ReconcileBacklogSql = @"
SELECT TOP (@cap) square_order_id, kind, status FROM dbo.checkout_orders
WHERE status = 'open'
ORDER BY NEWID()";

    /// <summary>
    /// THE RECOVERY QUEUE'S OWN DRAW: closed links whose delete Square never
    /// confirmed. That queue is precisely where a wrongly closed, genuinely paid
    /// order surfaces — Square will not cancel the link of an order that was paid,
    /// so the row never gets its deletion stamp and sits here. Asking about it is
    /// what keeps a closed order reachable by a pass that can still discover
    /// payment, and what lets an unconfirmable delete reach a terminal state
    /// (healed, or stamped dead on Square's own CANCELED) instead of blocking
    /// every link behind it in the retry queue forever.
    ///
    /// Capped at <see cref="ReconcileRetireRecheckWindow"/> and drawn BEFORE the
    /// open orders, so that a busy floor cannot crowd it out of the window. Same
    /// random sample, for the same reason: no last-checked column to rotate on, so
    /// any fixed ordering would let the same unconfirmable rows hold the front of
    /// the queue forever.
    /// </summary>
    internal const string ReconcileRetireRecheckSql = @"
SELECT TOP (@cap) square_order_id, kind, status FROM dbo.checkout_orders
WHERE status = 'canceled' AND kind = 'link' AND link_deleted_at IS NULL
ORDER BY NEWID()";

    /// <summary>
    /// How big the standing unconfirmed-delete queue may get before the sweep says
    /// so. A link that is unpaid, not cancelled at Square, and permanently
    /// unconfirmable never leaves any set — sampling stops it BLOCKING the queue
    /// but does not give it a terminal state, so these accumulate and dilute the
    /// draw, which is felt as healing latency on a genuinely paid, wrongly closed
    /// order. A terminal state needs a per-row attempt counter and this task may
    /// not touch the schema, so the queue is made VISIBLE instead: past this many
    /// rows one run can no longer reach the whole queue, which is the moment worth
    /// a warning rather than the moment it hurts.
    ///
    /// IT IS THE QUEUE'S OWN RESERVED DRAW, not the whole backlog window. It was
    /// the backlog window, and the justification for that — "past this many rows
    /// the sample can no longer reach the whole queue in one run" — was false the
    /// moment the queue started sharing one sample with every open cart on the
    /// floor: coverage broke down well below that number and at a point that moved
    /// with the open-order count. Now the queue draws its own reserved rows, the
    /// sentence is arithmetic again: more rows than the draw can take is exactly
    /// the point one run stops covering the queue.
    /// </summary>
    public const int ReconcileRetireQueueWarnAt = ReconcileRetireRecheckWindow;

    /// <summary>
    /// Pass 2's close, and the one statement in this sweep that can bury a
    /// customer's money — so read the id filter as load-bearing, not as an
    /// optimisation. <c>square_order_id IN @ids</c> is the list pass 1 just
    /// established is unpaid at Square (<see cref="ClosableOrderIds"/>). Without
    /// it this closes on database state alone, and a paid order closed here is
    /// invisible to every other pass forever: charged, nothing sold, no flag,
    /// and the sweep's own counters reporting all clear.
    ///
    /// The cost of the filter is that a stale link outside this run's window
    /// stays payable one more run. That is the right way round: a link that
    /// outlives its box is a payment we flag and refund, while an order closed
    /// without asking is a payment nobody ever sees.
    /// </summary>
    internal const string ReconcileRuleCancelSql = @"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
OUTPUT inserted.square_order_id, inserted.square_link_id
FROM dbo.checkout_orders o
WHERE o.status = 'open' AND o.kind = 'link'
  AND o.square_order_id IN @ids
  AND (
        o.created_at < DATEADD(DAY, -@ageDays, SYSUTCDATETIME())
     OR EXISTS (SELECT 1
                FROM dbo.checkout_order_boxes b
                JOIN dbo.manifests m ON m.id = b.manifest_id
                LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
                WHERE b.square_order_id = o.square_order_id
                  AND (m.publish_state <> 'live' OR m.archived_at IS NOT NULL OR m.is_ghost = 1 OR m.invoice_id IS NOT NULL
                       OR b.amount_cents <> CAST(ROUND(COALESCE(v.sale_price, v.list_price, v.total_wholesale, 0) * 100, 0) AS BIGINT)))
     OR EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
                WHERE b.square_order_id = o.square_order_id
                  AND NOT EXISTS (SELECT 1 FROM dbo.manifests m WHERE m.id = b.manifest_id))
  )";

    /// <summary>
    /// Reconciliation sweep (spec §4). SWA-managed Functions have no timers,
    /// so this runs from the staff button and the GitHub Actions cron.
    ///
    /// THE PASS ORDER IS THE DESIGN — it is the difference between healing a
    /// missed webhook and burying one.
    /// 1. ASK SQUARE FIRST, about everything that could be hiding a payment: the
    ///    newest open orders, plus the backlog as TWO reserved draws — a sample of
    ///    the links already closed whose delete Square never confirmed (the
    ///    recovery queue, drawn first so a busy floor cannot crowd it out), and
    ///    then a sample of open orders of any age with what is left.
    ///    Paid → fulfil, which heals the missed webhook whatever our row said;
    ///    CANCELED at Square → close the link and stamp it dead on Square's own
    ///    word. Every order is asked about in its own try/catch.
    /// 2. THEN close on database state — too old (Square links never expire, we
    ///    must), box no longer available, price drifted from today's ask — but
    ///    ONLY orders pass 1 established carry no payment at Square.
    /// 3. Delete at Square every canceled link not yet confirmed deleted: this
    ///    run's cancels first, then a sample of the standing queue.
    /// 4. Replay refunds recorded against a payment row that did not exist at
    ///    the time — including any row pass 1 has just created.
    ///
    /// Invoice orders are never canceled here (explicit staff action only):
    /// pass 2 filters on kind = 'link', and pass 1 closes an order only on the
    /// CancelLink verdict, which <see cref="VerdictFor"/> reserves for links.
    ///
    /// Every pass is idempotent and this is expected to run on a schedule, so
    /// the healthy result is all zeros. The database — not Square's answer to a
    /// delete — is the source of truth for whether a link is dead: linksDeleted
    /// is read back from link_deleted_at, and an unconfirmed delete is simply
    /// swept again next time.
    ///
    /// NOTHING SQUARE DOES CAN ABORT THIS RUN. Up to eighty calls a run makes a
    /// 429 ordinary, and a rotated token or a 5xx does the same thing; each is
    /// contained to the one order that caused it, and the refund replay — pure
    /// database work — still runs. It used to be the pass that got skipped,
    /// because an unhandled throw returned the whole function as an error, which
    /// on an unattended schedule is a silent dead sweep.
    ///
    /// A HOST SHUTDOWN IS THE ONE THING THAT DOES ABORT IT, and that is deliberate
    /// rather than incidental. Cancellation reaches every database call in here,
    /// so once the token is tripped the next command would throw out of the sweep
    /// anyway; pass 1 therefore stops and the run returns 503 having done only
    /// what it had already committed. Every pass is idempotent, so the next run
    /// picks it all up. Do not describe this as "Square cannot abort the run, and
    /// neither can anything else" — an earlier comment here did, and it was wrong.
    ///
    /// AND NOTHING IS CLOSED OR DELETED BY A RUN THAT NEVER REACHED OUR OWN
    /// MERCHANT. A credential pointing at the wrong Square account 404s every
    /// call, and a 404 was trusted twice — as proof of non-payment and as proof
    /// the link was gone. See <see cref="SquareAnswered"/>.
    /// </summary>
    [Function("SquareReconcile")]
    public async Task<IActionResult> Reconcile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-reconcile")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };

        await using var conn = await _sql.OpenAsync(ct);

        // ---- 1. Ask Square, BEFORE anything is closed on our own say-so. ----
        // Two halves of one budget so neither end of the list can starve the
        // other: the newest open orders, then the backlog sample.
        var window = new List<(string OrderId, string? Kind, string? Status)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Take(dynamic r)
        {
            string id = (string)r.square_order_id;
            if (seen.Add(id)) window.Add((id, (string?)r.kind, (string?)r.status));
        }
        foreach (var r in await conn.QueryAsync(new CommandDefinition(@"
SELECT TOP (@cap) square_order_id, kind, status FROM dbo.checkout_orders
WHERE status = 'open' ORDER BY created_at DESC",
            new { cap = ReconcileFreshWindow }, cancellationToken: ct))) Take(r);
        // The recovery queue draws its RESERVED share of the backlog budget first,
        // and the open orders take what is left of it. One combined sample let a
        // few hundred open carts crowd a handful of queue rows out of the window,
        // and those rows are where a wrongly closed PAID order surfaces — the last
        // rows on the floor that may be crowded out. Nothing is wasted when the
        // queue is empty: OpenBacklogCap hands the whole backlog window back.
        int queueDrawn = 0;
        foreach (var r in await conn.QueryAsync(new CommandDefinition(ReconcileRetireRecheckSql,
            new { cap = ReconcileRetireRecheckWindow }, cancellationToken: ct)))
        {
            queueDrawn++;
            Take(r);
        }
        int openBacklogCap = OpenBacklogCap(queueDrawn);
        if (openBacklogCap > 0)
            foreach (var r in await conn.QueryAsync(new CommandDefinition(ReconcileBacklogSql,
                new { cap = openBacklogCap }, cancellationToken: ct))) Take(r);
        int closedRechecked = window.Count(w => w.Status == "canceled");

        var checks = new List<OrderCheck>();
        int healed = 0, paidNothingSold = 0, reopened = 0, reopenedNothingSold = 0,
            canceledAtSquare = 0, stillOpen = 0, squareErrors = 0;
        foreach (var o in window)
        {
            // A host shutdown ends the run, here and below. Cancellation reaches
            // every database call in this method, so there is no "carry on with
            // the database-only passes" to be had — the next command would throw
            // out of the sweep. Stop cleanly and say so instead.
            if (ct.IsCancellationRequested) break;
            try
            {
                using var doc = await _square.RetrieveOrderAsync(o.OrderId, ct);
                if (doc == null)
                {
                    // 404 — Square holds no such order. That is only evidence about
                    // OUR merchant if this run also read a real order back from
                    // Square; VerifiedUnpaid applies that corroboration, and a run
                    // where every call 404s closes nothing and deletes nothing.
                    checks.Add(new OrderCheck(o.OrderId, SquareReply.NotFound, false));
                    if (o.Status != "canceled") stillOpen++;
                    continue;
                }
                if (!doc.RootElement.TryGetProperty("order", out var el))
                {
                    // The safe accessor the sibling parser (SquareService.OrderLinesAsync)
                    // has always used. A 2xx whose body is not the shape we parse
                    // used to throw out of the whole sweep here; it tells us
                    // nothing about payment, so it counts as unverified and the
                    // order is left open rather than closed on a guess.
                    checks.Add(new OrderCheck(o.OrderId, SquareReply.Unreachable, false));
                    squareErrors++;
                    _log.LogWarning("SquareReconcile: Square answered for order {OrderId} with no order object — treated as unverified; nothing is closed on it this run", o.OrderId);
                    continue;
                }

                string? state = el.TryGetProperty("state", out var st) ? st.GetString() : null;
                var signal = SquareService.PaymentOf(el);
                if (signal == SquareService.PaymentSignal.Unknown)
                {
                    // An order object carrying neither a tenders array nor a
                    // net_amount_due_money — a truncated body, a proxy that mangled
                    // the response, an empty object. The same rule as the missing
                    // order key one level up: a response we could not understand
                    // tells us nothing about payment, so it is unverified and
                    // nothing is closed on it. It is NOT "verified unpaid".
                    checks.Add(new OrderCheck(o.OrderId, SquareReply.Unreachable, false));
                    squareErrors++;
                    _log.LogWarning("SquareReconcile: Square's order object for {OrderId} carries no payment signal (no tenders, no net_amount_due_money) — treated as unverified; nothing is closed on it this run", o.OrderId);
                    continue;
                }
                bool paid = signal == SquareService.PaymentSignal.Paid;
                checks.Add(new OrderCheck(o.OrderId, SquareReply.Order, paid));

                switch (VerdictFor(state, paid, o.Kind))
                {
                    case ReconcileVerdict.Heal:
                    {
                        var tenderPayment = el.TryGetProperty("tenders", out var tenders) &&
                                            tenders.ValueKind == JsonValueKind.Array && tenders.GetArrayLength() > 0 &&
                                            tenders[0].TryGetProperty("payment_id", out var tp) ? tp.GetString() : null;
                        long? amt = el.TryGetProperty("total_money", out var tm) && tm.TryGetProperty("amount", out var ta) ? ta.GetInt64() : null;
                        var r = await _fulfill.FulfillOrderAsync(conn, o.OrderId, tenderPayment ?? $"reconciled-{o.OrderId}", amt, null, "reconcile", ct);
                        if (r.Outcome == "fulfilled")
                        {
                            // Healing a CANCELED row is the recovery of a charge some
                            // earlier run — or a staff cancel — closed while the money
                            // was already at Square. It is louder than an ordinary heal
                            // because the boxes have very likely gone elsewhere since,
                            // in which case fulfilment has just flagged a refund the
                            // buyer is genuinely owed (RefundDue).
                            //
                            // Counted in the split below rather than here, for the same
                            // reason healed is: a cancelled row that recovered a charge
                            // and could hand nothing over is not a row we reopened, it
                            // is a refund we owe. Incrementing both put exactly the
                            // gloss on the headline number that folding paidNothingSold
                            // into healed used to.

                            // Square says CANCELED and Square says paid, at the same
                            // time. VerdictFor sends that here rather than to the cancel
                            // branch precisely so the row is never stamped dead with
                            // money on it — but it is a contradiction, so say it.
                            if (state == "CANCELED")
                                _log.LogError(
                                    "SquareReconcile: order {OrderId} is CANCELED at Square and PAID at Square at the same time — fulfilled it rather than retiring the link, and left the link unstamped so it stays in the recovery window. Needs a human.",
                                    o.OrderId);

                            // "Healed" has to mean the sweep delivered something. A
                            // single-box order whose box was deleted fulfils with Sold
                            // = 0: we took the money and there is nothing to hand over,
                            // which is a full refund owed, not a repair. Counting that
                            // as a heal puts the one number a person reads first on the
                            // wrong side of the ledger.
                            //
                            // Sold counts every box this order owns; NewlySold only the ones
                            // THIS call moved. On a later sweep over an order an earlier one
                            // already healed, Sold still reads N while NewlySold reads 0 —
                            // report both, or an ordinary no-op sweep looks like it healed the
                            // same order over and over and a real incident gets dismissed.
                            if (r.Sold == 0)
                            {
                                paidNothingSold++;
                                if (o.Status == "canceled") reopenedNothingSold++;
                                _log.LogError(
                                    "SquareReconcile: order {OrderId} was PAID at Square but NOTHING could be sold (our row said {Was}): 0 sold, {Unav} unavailable, full refund due {Due}c. This is not a heal — the buyer is owed their money back.",
                                    o.OrderId, o.Status, r.Unavailable, r.RefundDueCents);
                            }
                            else
                            {
                                healed++;
                                if (o.Status == "canceled") reopened++;
                                _log.Log(o.Status == "canceled" ? LogLevel.Error : LogLevel.Warning,
                                    "SquareReconcile: healed missed webhook — order {OrderId} (our row said {Was}): {Sold} sold ({New} newly), {Unav} unavailable{Partial}, refund due {Due}c",
                                    o.OrderId, o.Status, r.Sold, r.NewlySold, r.Unavailable,
                                    r.Unavailable > 0 ? " — PARTIAL, a refund is owed on the rest" : "", r.RefundDueCents);
                            }
                        }
                        else
                        {
                            // Paid at Square, but fulfilment would not take it: the
                            // payment id already has a row and the order is still open.
                            // It stays open, costs a Square call every run and no pass
                            // can resolve it — so SAY SO, with the ids to act on. An
                            // unexplained stillOpen count is how this stayed invisible.
                            stillOpen++;
                            _log.LogWarning(
                                "SquareReconcile: order {OrderId} is PAID at Square but fulfilment answered '{Outcome}' (payment {PaymentId}, {Amt}c) — it stays open and no pass can clear it. Needs a human: check dbo.payments for that payment id.",
                                o.OrderId, r.Outcome, tenderPayment, amt);
                        }
                        break;
                    }
                    case ReconcileVerdict.CancelLink:
                        // Square says the order is dead, so the link died with it:
                        // link_deleted_at is stamped on Square's own word rather than on a
                        // delete call we would only make to be told the same thing. COALESCE
                        // keeps the original closed_at (this row may already be canceled —
                        // that is the backlog recheck retiring it), and status <> 'paid'
                        // means nothing here can ever demote an order we have fulfilled.
                        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.checkout_orders
SET status = 'canceled',
    closed_at = COALESCE(closed_at, SYSUTCDATETIME()),
    link_deleted_at = COALESCE(link_deleted_at, SYSUTCDATETIME())
WHERE square_order_id = @oid AND status <> 'paid'",
                            new { oid = o.OrderId }, cancellationToken: ct));
                        canceledAtSquare++;
                        break;
                    default:
                        if (o.Status != "canceled") stillOpen++;
                        break;
                }
            }
            catch (Exception ex)
            {
                // Contained to this order on purpose. Before this, one 429 or one
                // rotated token returned the entire sweep as an error and the
                // refund replay — which needs nothing from Square — never ran.
                // An order we could not ask about is simply not closable this run
                // (see VerifiedUnpaid), so nothing unsafe follows from carrying on.
                //
                // A host shutdown arrives here as a cancellation and is NOT a
                // Square error: it is not counted as one, and the loop check above
                // stops the run rather than logging one line per remaining order.
                checks.Add(new OrderCheck(o.OrderId, SquareReply.Unreachable, false));
                if (ct.IsCancellationRequested)
                    _log.LogWarning("SquareReconcile: stopped at order {OrderId} — the host is shutting down; nothing was closed on it", o.OrderId);
                else
                {
                    squareErrors++;
                    _log.LogError(ex, "SquareReconcile: order {OrderId} could not be checked at Square — skipped; it stays open and nothing is closed on it this run", o.OrderId);
                }
            }
        }

        // ---- The run's own health, before anything acts on it. ----
        // A host shutdown stops the sweep here rather than three database calls
        // later with an OperationCanceledException nobody asked for. Everything
        // pass 1 committed is committed; every pass is idempotent; the next run
        // does the rest.
        if (ct.IsCancellationRequested)
        {
            _log.LogWarning("SquareReconcile: the host is shutting down — the sweep stopped after asking Square about {Asked} of {Window} order(s). Nothing was closed, deleted or replayed this run; the next run picks it up.",
                checks.Count, window.Count);
            return new ObjectResult(new
            {
                aborted = "host shutdown", asked = checks.Count, window = window.Count,
                healed, paidNothingSold, reopened, reopenedNothingSold, canceledAtSquare, stillOpen, squareErrors,
            })
            { StatusCode = 503 };
        }

        // THE MISCONFIGURATION ALARM. Every order Square answered about came back
        // "no such order" — we only ever ask about orders we created, so our own
        // data cannot produce that. The credential is looking at another merchant,
        // and our buyers' money is sitting untouched at ours where this run cannot
        // see it. Nothing below may close or delete on that evidence.
        bool squareAnswered = SquareAnswered(checks);
        if (LooksLikeWrongMerchant(checks))
            _log.LogError(
                "SquareReconcile: {NotFound} of the {Asked} order(s) this run asked Square about came back 404, not one readable order came back with them, and that is most of the run rather than a bad row or two. That is what a token pointed at the wrong Square merchant or location looks like — the orders and the payments are still at ours, we just cannot see them. NOTHING was closed and NO link was deleted this run. Check SQUARE_ENVIRONMENT, the access token and the location id.",
                checks.Count(c => c.Reply == SquareReply.NotFound), checks.Count);

        // ---- 2. Close on database state, restricted to what pass 1 verified. ----
        var closable = ClosableOrderIds(checks);
        var ruleCanceled = closable.Count == 0
            ? new List<CanceledLink>()
            : (await conn.QueryAsync(new CommandDefinition(ReconcileRuleCancelSql,
                new { ids = closable, ageDays = ReconcileLinkMaxAgeDays }, cancellationToken: ct)))
                .Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();

        // ---- 3. Square deletes for anything canceled but not confirmed. ----
        // This run's cancels go first: those links are payable RIGHT NOW. The rest
        // of the budget samples the standing queue at random rather than walking it
        // oldest-first — a fixed order lets a handful of links Square will not
        // confirm sit at the head forever and starve every link behind them, which
        // is how "a link nobody retires stays payable forever" happens.
        //
        // RetireLinksAsync stamps link_deleted_at only where Square CONFIRMED the
        // delete, and never throws, so the count is read back from the database
        // rather than from what the calls appeared to return.
        //
        // AND NOT AT ALL IF THIS RUN NEVER REACHED OUR OWN MERCHANT. A delete that
        // 404s is read as confirmation, so at the wrong merchant every delete
        // "succeeds" and stamps link_deleted_at — which is what takes a row out of
        // the backlog recovery window for good. The rows in this queue are exactly
        // the ones a paid-but-closed order hides in, so they are the last rows that
        // may be retired on an unverified 404. No corroboration, no deletes.
        // (Skipping costs one run of a dead link staying payable; it is retried
        // every run. Note this also skips on a run where every Square call failed
        // outright, which is right for the same reason and costs the same.)
        // The guard is around BOTH sources on purpose, and it lives in
        // LinksToRetire rather than in an if around these two reads: this is the
        // branch that actually closes the defect, so it is a pure function with a
        // test on it instead of a paragraph of prose between two database calls.
        //
        // Skipping the queue read when there is nothing to spend it on is a cost
        // saving and NOT the safety property. LinksToRetire refuses to retire
        // anything on an uncorroborated run whatever it is handed, which is what
        // the test asserts.
        var queueSample = !squareAnswered || ruleCanceled.Count >= ReconcileMaxSquareCalls
            ? new List<CanceledLink>()
            : (await conn.QueryAsync(new CommandDefinition(@"
SELECT TOP (@cap) square_order_id, square_link_id FROM dbo.checkout_orders
WHERE status = 'canceled' AND kind = 'link' AND link_deleted_at IS NULL
ORDER BY NEWID()", new { cap = ReconcileMaxSquareCalls }, cancellationToken: ct)))
                .Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();
        var retire = LinksToRetire(squareAnswered, ruleCanceled, queueSample, ReconcileMaxSquareCalls);
        await _fulfill.RetireLinksAsync(conn, retire, ct);
        int linksDeleted = retire.Count == 0 ? 0 : await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.checkout_orders WHERE square_order_id IN @ids AND link_deleted_at IS NOT NULL",
            new { ids = retire.Select(p => p.OrderId).ToArray() }, cancellationToken: ct));

        // ---- 4. Orphaned refunds. ----
        //    A refund can arrive before the payment it belongs to, or for a
        //    payment we never recorded at all — a Dashboard refund against a
        //    counter sale carries no application_details, so nothing filters it
        //    out. RecordRefundAsync writes the audit row regardless (dropping it
        //    would lose a real refund forever), the payments UPDATE then matches
        //    nothing, and the claimed refund id means no redelivery can ever
        //    replay it. This pass runs AFTER pass 1 on purpose: a heal there may
        //    have just created the payments row that makes one attributable, and
        //    before the needs_refund count below, so the number staff are shown
        //    reflects the flags this sweep just cleared.
        //
        //    Capped like every other pass, on PAYMENTS rather than rows: the
        //    cents come from the payment-level totals, so a payment has to arrive
        //    with all of its refund rows or the reported row count under-states
        //    it. Nothing starves behind the cap — a repaired payment has recorded
        //    == applied and leaves the set for good, so the backlog drains.
        var orphanRows = (await conn.QueryAsync<RecordedRefund>(new CommandDefinition(@"
SELECT r.square_payment_id AS PaymentId, r.square_refund_id AS RefundId, r.amount_cents AS AmountCents,
       t.recorded_cents AS RecordedCents, t.applied_cents AS AppliedCents
FROM (SELECT TOP (@cap) r2.square_payment_id AS square_payment_id,
             SUM(r2.amount_cents) AS recorded_cents,
             MIN(p2.refunded_cents) AS applied_cents
      FROM dbo.payment_refunds r2
      JOIN dbo.payments p2 ON p2.square_payment_id = r2.square_payment_id
      GROUP BY r2.square_payment_id
      HAVING SUM(r2.amount_cents) > MIN(p2.refunded_cents)
      ORDER BY MAX(r2.created_at) DESC) t
JOIN dbo.payment_refunds r ON r.square_payment_id = t.square_payment_id
ORDER BY r.square_payment_id, r.created_at, r.square_refund_id",
            new { cap = ReconcileMaxRefundReplays }, cancellationToken: ct))).ToList();

        int refundsApplied = 0, refundErrors = 0;
        long refundCentsApplied = 0;
        foreach (var plan in PlanRefundReplay(orphanRows))
        {
            // A host shutdown ends this loop the way it ends pass 1's: cleanly,
            // and reported as a shutdown. It used to propagate as an unhandled
            // exception — a hard failure out of the one pass that needs nothing
            // from Square — and worse, a cancelled database call usually surfaces
            // as a database exception rather than an OperationCanceledException,
            // so the old `when (ex is not OperationCanceledException)` filter
            // would have counted a shutdown as a failed refund and announced that
            // our books under-state a refund when nothing of the sort happened.
            // THE TOKEN DECIDES, NOT THE EXCEPTION TYPE, which is what makes that
            // miscount impossible rather than unlikely.
            if (ct.IsCancellationRequested) break;

            // Contained per payment, for the same reason pass 1's loop is: these
            // payments are independent of one another, so one deadlock or one bad
            // row must not skip the forty behind it — and must not skip the
            // needs_refund count below, which is the number staff act on.
            try
            {
                // The guard inside ApplyRecordedRefundAsync is what makes this safe on a
                // schedule: it refuses to push refunded_cents past the total of the
                // refunds actually recorded, so a second sweep moves nothing.
                if (await _fulfill.ApplyRecordedRefundAsync(conn, plan.PaymentId, plan.AmountCents, ct) > 0)
                {
                    refundsApplied += plan.RefundCount;
                    refundCentsApplied += plan.AmountCents;
                    _log.LogWarning(
                        "SquareReconcile: applied {Cents}c of refunds recorded against payment {PaymentId} that had never reached its total ({Rows} refund row(s)) — our books and Square agree again",
                        plan.AmountCents, plan.PaymentId, plan.RefundCount);
                }
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    _log.LogWarning(
                        "SquareReconcile: stopped at payment {PaymentId} — the host is shutting down; its {Cents}c of recorded refunds were not applied and the next run picks them up",
                        plan.PaymentId, plan.AmountCents);
                    break;
                }
                refundErrors++;
                _log.LogError(ex,
                    "SquareReconcile: could not apply {Cents}c of recorded refunds to payment {PaymentId} — our books still under-state what Square refunded on it; it is retried next run",
                    plan.AmountCents, plan.PaymentId);
            }
        }

        // The same clean abort pass 1 takes, at the other end of the sweep: a 503
        // with what was committed, and no further database calls — they would only
        // throw on a tripped token. Every pass is idempotent, so the next run does
        // the rest.
        if (ct.IsCancellationRequested)
        {
            _log.LogWarning("SquareReconcile: the host is shutting down — the sweep stopped during the refund replay, having applied {Applied} refund(s). The rest, and this run's counts, are left for the next run.",
                refundsApplied);
            return new ObjectResult(new
            {
                aborted = "host shutdown", asked = checks.Count, window = window.Count,
                healed, paidNothingSold, reopened, reopenedNothingSold, canceledAtSquare, stillOpen, squareErrors,
                canceledByRule = ruleCanceled.Count, linksDeleted, refundsApplied, refundCentsApplied, refundErrors,
            })
            { StatusCode = 503 };
        }

        var flagged = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.payments WHERE needs_refund = 1", cancellationToken: ct));
        if (squareErrors > 0)
            _log.LogError("SquareReconcile: {Errors} of {Asked} order(s) could not be checked at Square this run — they stay open and will be re-asked; nothing was closed on them",
                squareErrors, window.Count);

        // ---- The standing unconfirmed-delete queue, measured. ----
        // A DIAGNOSTIC, WHICH IS WHY IT RUNS LAST AND WHY IT IS WRAPPED. It used
        // to sit between the deletion pass and the refund replay with no error
        // handling of its own, so one deadlock or one timeout on a COUNT(*) that
        // exists only to produce a warning line took the refund replay and the
        // needs_refund count down with it — the silent dead sweep the comments
        // above warn about, caused by a query that moves no money. Both fixes are
        // applied, because either alone still leaves a diagnostic standing in
        // front of something that matters.
        //
        // What it measures: a link that is unpaid, not cancelled at Square and
        // permanently unconfirmable never leaves the queue — the random sample
        // stops it blocking the queue but gives it no terminal state, so the pile
        // grows and crowds out the reserved draw that finds a wrongly closed paid
        // order. A real terminal state wants an attempt counter on the row, which
        // is a schema change this task may not make.
        int? retireQueue = null;
        try
        {
            retireQueue = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.checkout_orders WHERE status = 'canceled' AND kind = 'link' AND link_deleted_at IS NULL",
                cancellationToken: ct));
            if (retireQueue > ReconcileRetireQueueWarnAt)
                _log.LogWarning(
                    "SquareReconcile: {Queue} canceled link(s) are still waiting for Square to confirm a delete — more than the {Window} rows one run's reserved recheck draw can take, so the queue can no longer be covered in a single run. Every row past that stretches how long a wrongly closed PAID order takes to surface, and they have no terminal state — look for links Square will never confirm and clear them by hand.",
                    retireQueue, ReconcileRetireQueueWarnAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "SquareReconcile: could not measure the unconfirmed-delete queue — it is reported as null. Nothing this run did depends on it; everything above is already committed.");
        }
        return new OkObjectResult(new
        {
            canceledByRule = ruleCanceled.Count, linksDeleted, healed, canceledAtSquare, stillOpen,
            refundsApplied, refundCentsApplied, needsRefund = flagged,
            // New in the fix round: how many closed-but-undeleted links were
            // re-asked about, how many of those turned out to be paid after all,
            // and how many orders Square would not answer for.
            closedRechecked, reopened, squareErrors,
            // healed means boxes were handed over. paidNothingSold means we took
            // the money and had nothing left to sell — a full refund owed, not a
            // repair, and never folded into healed. reopenedNothingSold is that
            // same split applied to reopened: a cancelled row we recovered a charge
            // on and could hand nothing over is counted here and NOT as a reopen,
            // so neither headline number flatters the sweep.
            paidNothingSold, reopenedNothingSold, refundErrors,
            // The standing unconfirmed-delete queue (null if the diagnostic COUNT
            // could not be run — it is not allowed to fail the sweep), and whether
            // this run had any proof at all that our credential can see our own
            // merchant. A false squareAnswered with a non-zero window is the
            // wrong-merchant alarm.
            retireQueue, squareAnswered,
        });
    }
}
