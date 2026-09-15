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
    // The zip list is read on every public page load, so keep an in-process
    // copy. Rob's edits show up within the TTL, and the checkout call itself
    // always re-reads dbo.delivery_zips, so the authority never goes stale.
    private static readonly TimeSpan ZipCacheTtl = TimeSpan.FromMinutes(5);
    private static (List<DeliveryZip> Zips, DateTime At)? _zipCache;
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
            if (cached != null && DateTime.UtcNow - cached.Value.At < ZipCacheTtl)
            {
                zips = cached.Value.Zips;
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
                    _zipCache = (zips, DateTime.UtcNow);
                }
                // Only OUR cancellation is left alone. Nothing inside this try
                // makes an HTTP call today, but the next person to add one must
                // inherit the right shape: a transport timeout arrives as
                // TaskCanceledException with ct un-signalled, and belongs here.
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Serve the last list we had rather than nothing: a quiet
                    // SQL hiccup here would otherwise hide the cart site-wide.
                    _log.LogError(ex, "CheckoutStatus: delivery_zips query failed — serving the cached list ({N} zips)", cached?.Zips.Count ?? 0);
                    zips = cached?.Zips ?? new List<DeliveryZip>();
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
            if (!System.Text.RegularExpressions.Regex.IsMatch(zip, @"^\d{5}$"))
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
            bool ok = Availability.ForLink((string?)b.publish_state, (DateTime?)b.archived_at, b.is_ghost == true, (string?)b.invoice_id)
                      && ask is > 0;
            if (!ok) { unavailable.Add(id); continue; }
            lines.Add(new CartLine(id, SquarePayloads.BoxLineName((int)b.pallet_number, (string?)b.display_name),
                (long)Math.Round(ask!.Value * 100m)));
            numbers.Add((int)b.pallet_number);
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
                    recorded = await CheckoutFulfillment.RecordRefundAsync(
                        rconn, refund.RefundId, refund.PaymentId, refund.AmountCents!.Value);
                    _log.LogInformation("SquareWebhook: refund {RefundId} COMPLETED for payment {PaymentId} ({Amt}c) recorded={Rec}",
                        refund.RefundId, refund.PaymentId, refund.AmountCents, recorded);
                }
                else
                {
                    // The money did NOT go back. Re-raise the flag so the refund
                    // stays on someone's list; an already-REFUNDED payment is left
                    // alone, since a later failed attempt cannot un-refund it.
                    await rconn.ExecuteAsync(
                        "UPDATE dbo.payments SET needs_refund = 1, status = 'REFUND_' + @rs WHERE square_payment_id = @pid AND status <> 'REFUNDED'",
                        new { pid = refund.PaymentId, rs = refund.Status });
                    _log.LogError("SquareWebhook: refund {RefundId} {Status} for payment {PaymentId} — re-flagged",
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
    /// invoice (card or ACH) for this box. The invoice's order_id lands in the
    /// same checkout_order_id column the webhook matches, so payment.updated
    /// COMPLETED marks the box SOLD with no new logic (ACH-safe: PENDING
    /// doesn't sell the box; a failed ACH never completes). Invoicing retires
    /// any public Buy link and parks the box in draft (reserved, off-site).
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
       m.checkout_link_id, m.invoice_id,
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

        // Retire the public Buy link (its order would be a second sale channel).
        if (box.checkout_link_id != null)
            await _square.DeletePaymentLinkAsync((string)box.checkout_link_id, ct);

        var customerId = await _square.FindOrCreateCustomerAsync(body.email.Trim(), body.name, ct);
        var name = $"BOX #{box.pallet_number} — {(string?)box.display_name ?? "NSL Box"}";
        var inv = await _square.CreateInvoiceAsync(
            name, (long)Math.Round(price.Value * 100m), customerId,
            title: "North State Liquidators", invoiceNumber: $"BOX-{box.pallet_number}", ct);

        await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET
    invoice_id = @iid, invoice_url = @iurl,
    checkout_link_id = NULL, checkout_url = NULL,
    checkout_order_id = @oid, checkout_created_at = SYSUTCDATETIME()
WHERE id = @id", new { id, iid = inv.InvoiceId, iurl = inv.PublicUrl, oid = inv.OrderId });
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
    /// POST /api/pallets/{id}/invoice-cancel — cancel the outstanding invoice
    /// and clear the correlation so the box can go back on the site.
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
        await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET invoice_id = NULL, invoice_url = NULL,
    checkout_order_id = NULL, checkout_created_at = NULL WHERE id = @id", new { id });
        _log.LogInformation("CancelBoxInvoice: BOX #{Num} invoice canceled", (object?)box.pallet_number);
        return new OkObjectResult(new { canceled = true });
    }

    /// <summary>
    /// GET /api/square-payments — staff view of our payment audit trail, box
    /// context joined in. Flagged rows (needs_refund) first.
    /// </summary>
    [Function("ListSquarePayments")]
    public async Task<IActionResult> ListPayments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "square-payments")] HttpRequest req,
        CancellationToken ct)
    {
        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(@"
SELECT TOP 100 p.square_payment_id, p.square_order_id, p.manifest_id,
       p.amount_cents, p.status, p.needs_refund, p.created_at,
       m.pallet_number, m.display_name
FROM dbo.payments p
LEFT JOIN dbo.manifests m ON m.id = p.manifest_id
ORDER BY p.needs_refund DESC, p.created_at DESC")).ToList();
        return new OkObjectResult(rows);
    }

    public sealed record RefundRequest(string? paymentId, string? reason);

    /// <summary>
    /// POST /api/square-refund — full refund of one payment from the admin.
    /// Staff-authenticated route; refund amount comes from OUR audit row, not
    /// the request, so the UI can't fat-finger an amount. The refund.updated
    /// webhook flips the row to REFUNDED when Square confirms.
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
        var row = await conn.QueryFirstOrDefaultAsync(
            "SELECT square_payment_id, amount_cents, status FROM dbo.payments WHERE square_payment_id = @pid",
            new { pid = body.paymentId });
        if (row == null) return new NotFoundObjectResult(new { error = "Payment not found in our records." });
        if ((string)row.status == "REFUNDED")
            return new ConflictObjectResult(new { error = "Already refunded." });
        long? cents = (long?)row.amount_cents;
        if (cents is null or <= 0)
            return new ConflictObjectResult(new { error = "No amount on record — refund this one in the Square Dashboard." });

        using var result = await _square.RefundPaymentAsync(body.paymentId, cents.Value, body.reason, ct);
        var refundStatus = result.RootElement.GetProperty("refund").TryGetProperty("status", out var rs)
            ? rs.GetString() : "PENDING";
        await conn.ExecuteAsync(
            "UPDATE dbo.payments SET status = 'REFUND_' + @rs, needs_refund = 0 WHERE square_payment_id = @pid",
            new { rs = refundStatus, pid = body.paymentId });
        _log.LogInformation("SquareRefund: payment {PaymentId} -> {Status}", body.paymentId, refundStatus);
        return new OkObjectResult(new { paymentId = body.paymentId, refundStatus });
    }

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
        var webRows = (await conn.QueryAsync(@"
SELECT p.square_payment_id, p.manifest_id, m.pallet_number, m.display_name,
       v.total_cost, v.total_cost_units
FROM dbo.payments p
JOIN dbo.manifests m ON m.id = p.manifest_id
LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE p.created_at >= @begin", new { begin })).ToList();
        var webByPaymentId = webRows
            .GroupBy(r => (string)r.square_payment_id)
            .ToDictionary(g => g.Key, g => g.First());

        var sales = new List<SaleRow>();
        long squareCents = 0, webCents = 0, floorCents = 0, refundedCents = 0;
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

            squareCents += amt;
            refundedCents += refunded;
            if (isWeb) webCents += amt; else floorCents += amt;

            decimal? cost = null;
            if (isWeb) cost = (decimal?)(web!.total_cost ?? web.total_cost_units);
            sales.Add(new SaleRow(
                payment_id: pid,
                created_at: created,
                amount_cents: amt,
                refunded_cents: refunded,
                channel: isWeb ? "web" : "floor",
                source: "square",
                pallet_number: isWeb ? (int?)web!.pallet_number : null,
                display_name: isWeb ? (string?)web!.display_name : null,
                cost: cost,
                margin_cents: isWeb && cost.HasValue ? (long?)(amt - (long)Math.Round(cost.Value * 100)) : null,
                note: null));
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
                  WHERE p.manifest_id = m.id
                    AND p.needs_refund = 0
                    AND p.status LIKE 'COMPLETED%')
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
                channel: "admin",
                source: "admin",
                pallet_number: (int?)a.pallet_number,
                display_name: (string?)a.display_name,
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
            gross_cents = squareCents,          // what Square actually collected (web + floor)
            square_cents = squareCents,
            web_cents = webCents,
            floor_cents = floorCents,
            admin_cents = adminCents,           // listed separately — may overlap floor_cents (see above)
            refunded_cents = refundedCents,
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
        string channel, string source, int? pallet_number, string? display_name,
        decimal? cost, long? margin_cents, string? note);

    private static DateTimeOffset ParseWhen(string? iso) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue;

    /// <summary>
    /// Reconciliation sweep — SWA-managed Functions are HTTP-only (no timers),
    /// so this runs when staff hit it (falls under authenticated /api/*).
    /// Heals missed webhooks: any live box with an open link whose order is
    /// paid gets marked SOLD; links on archived/pulled boxes are deleted.
    /// </summary>
    [Function("SquareReconcile")]
    public async Task<IActionResult> Reconcile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-reconcile")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };

        await using var conn = await _sql.OpenAsync(ct);
        // Fake-sold boxes (Sold → inventory) are included so a Buy link that
        // could not be retired at the time (Square unconfigured / hiccup) is
        // swept here instead of staying payable forever.
        var open = (await conn.QueryAsync(@"
SELECT TOP 50 id, pallet_number, publish_state, archived_at, checkout_link_id, checkout_order_id, invoice_id
FROM dbo.manifests
WHERE checkout_order_id IS NOT NULL
  AND (publish_state <> 'sold' OR sold_to_inventory_at IS NOT NULL)
ORDER BY checkout_created_at ASC")).ToList();

        int healed = 0, retired = 0, stillOpen = 0;
        foreach (var b in open)
        {
            using var order = await _square.RetrieveOrderAsync((string)b.checkout_order_id, ct);
            if (order == null) continue;
            var el = order.RootElement.GetProperty("order");

            if (SquareService.IsOrderPaid(el))
            {
                // Paid but never marked sold — the webhook we missed.
                await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @mid, @publish_state = 'sold'",
                    new { mid = (Guid)b.id });
                await PalletsFunction.InsertHistoryAsync(conn, (Guid)b.id, "publish_state", (string?)b.publish_state, "sold", "square");
                var tenderPayment = el.TryGetProperty("tenders", out var tenders) && tenders.GetArrayLength() > 0 &&
                                    tenders[0].TryGetProperty("payment_id", out var tp) ? tp.GetString() : $"reconciled-{b.checkout_order_id}";
                await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, manifest_id, status, event_json)
SELECT @pid, @oid, @mid, 'COMPLETED_RECONCILED', NULL
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
                    new { pid = tenderPayment, oid = (string)b.checkout_order_id, mid = (Guid)b.id });
                _log.LogWarning("SquareReconcile: healed missed webhook — BOX #{Num} marked SOLD", (object?)b.pallet_number);
                healed++;
            }
            else if (b.invoice_id != null)
            {
                // Outstanding wholesale invoice, unpaid — the reserved-in-draft
                // state is intentional; cancellation is explicit, never swept.
                stillOpen++;
            }
            else if ((DateTime?)b.archived_at != null || (string)b.publish_state != "live")
            {
                // Box was pulled after a link existed — retire the link so it can't be paid.
                if (b.checkout_link_id != null)
                    await _square.DeletePaymentLinkAsync((string)b.checkout_link_id, ct);
                await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET checkout_link_id = NULL, checkout_order_id = NULL,
       checkout_url = NULL, checkout_created_at = NULL WHERE id = @mid",
                    new { mid = (Guid)b.id });
                _log.LogInformation("SquareReconcile: retired link for pulled BOX #{Num}", (object?)b.pallet_number);
                retired++;
            }
            else stillOpen++;
        }

        var flagged = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.payments WHERE needs_refund = 1");
        return new OkObjectResult(new { healed, retired, stillOpen, needsRefund = flagged });
    }
}
