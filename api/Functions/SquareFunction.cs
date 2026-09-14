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
///   GET  /api/public/checkout-status   — is online buying on? (Buy buttons render off this)
///   POST /api/public/checkout/{id}     — mint/reuse the box's Square payment link
///   POST /api/square/webhook           — payment.updated → mark box SOLD (HMAC-authed, anonymous route)
///   POST /api/square-reconcile         — staff-triggered sweep healing missed webhooks
///
/// Design invariants: one single-use link per box (deterministic idempotency
/// key + stored link columns); SOLD only ever set via sp_SetPublishState;
/// webhook handler idempotent (UNIQUE square_payment_id + event replays no-op);
/// "paid" judged by tenders/net-due, never order state (stays OPEN forever).
/// </summary>
public sealed class SquareFunction
{
    private readonly SqlService _sql;
    private readonly SquareService _square;
    private readonly ILogger<SquareFunction> _log;

    public SquareFunction(SqlService sql, SquareService square, ILogger<SquareFunction> log)
    {
        _sql = sql;
        _square = square;
        _log = log;
    }

    [Function("CheckoutStatus")]
    public IActionResult Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/checkout-status")] HttpRequest req)
        => new OkObjectResult(new { enabled = _square.CheckoutEnabled && _square.Configured });

    [Function("CreateCheckout")]
    public async Task<IActionResult> CreateCheckout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout/{id}")] HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        if (!_square.CheckoutEnabled || !_square.Configured)
            return new ObjectResult(new { error = "Online checkout is not available right now." }) { StatusCode = 503 };

        await using var conn = await _sql.OpenAsync(ct);
        var box = await conn.QueryFirstOrDefaultAsync(@"
SELECT p.manifest_id, p.pallet_number, p.display_name, p.publish_state, p.is_ghost,
       p.archived_at, m.checkout_link_id, m.checkout_order_id, m.checkout_url,
       COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p
JOIN dbo.manifests m ON m.id = p.manifest_id
WHERE p.manifest_id = @id", new { id });

        if (box == null) return new NotFoundResult();
        bool ghost = box.publish_state == "ghost" || box.is_ghost == true;
        if (box.publish_state != "live" || ghost || box.archived_at != null)
            return new ConflictObjectResult(new { error = "This box is no longer available." });

        decimal? ask = (decimal?)box.ask_price;
        if (ask is null or <= 0)
            return new BadRequestObjectResult(new { error = "This box doesn't have a price yet — call us instead." });

        // Reuse the box's existing link: single-use on Square's side, so two
        // shoppers holding the same URL can still only produce one payment.
        if (box.checkout_url != null)
            return new OkObjectResult(new { url = (string)box.checkout_url });

        var name = $"BOX #{box.pallet_number} — {(string?)box.display_name ?? "NSL Box"}";
        var redirect = $"https://northstateliquidators.com/thanks.html?box={box.pallet_number}";
        var link = await _square.CreatePaymentLinkAsync(
            name,
            (long)Math.Round(ask.Value * 100m),
            redirect,
            idempotencyKey: $"nsl-{id}-link-v1",
            note: $"NSL BOX #{box.pallet_number} ({id})",
            ct);

        await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET checkout_link_id = @lid, checkout_order_id = @oid,
       checkout_url = @url, checkout_created_at = SYSUTCDATETIME()
WHERE id = @id AND checkout_link_id IS NULL",
            new { id, lid = link.Id, oid = link.OrderId, url = link.Url });

        _log.LogInformation("CreateCheckout: BOX #{Num} -> link {LinkId} order {OrderId}",
            (object?)box.pallet_number, link.Id, link.OrderId);
        return new OkObjectResult(new { url = link.Url });
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

        // Refunds: mark our audit row REFUNDED (and clear the attention flag)
        // when Square confirms the money went back — whether we triggered it
        // via the admin button or someone did it in the Square Dashboard.
        if (type is "refund.updated" or "refund.created")
        {
            if (!SquareEvents.TryParseRefund(root, out var refund))
                return new OkObjectResult(new { ignored = "malformed" });
            if (refund.Status == "COMPLETED" && refund.PaymentId != null)
            {
                await using var rconn = await _sql.OpenAsync(ct);
                var n = await rconn.ExecuteAsync(
                    "UPDATE dbo.payments SET status = 'REFUNDED', needs_refund = 0 WHERE square_payment_id = @pid",
                    new { pid = refund.PaymentId });
                _log.LogInformation("SquareWebhook: refund COMPLETED for payment {PaymentId} ({N} row updated)", refund.PaymentId, n);
            }
            return new OkObjectResult(new { refund = refund.Status });
        }

        if (type != "payment.updated" && type != "payment.created")
            return new OkObjectResult(new { ignored = type });   // subscribed but not relevant

        if (!SquareEvents.TryParsePayment(root, out var pay))
            return new OkObjectResult(new { ignored = "malformed" });
        if (pay.Status != "COMPLETED")
            return new OkObjectResult(new { ignored = pay.Status });

        var paymentId = pay.PaymentId;
        var orderId   = pay.OrderId;
        long? amount  = pay.AmountCents;

        await using var conn = await _sql.OpenAsync(ct);

        // The merchant account is shared with the floor POS. Match by order
        // first; a payment that matches no box AND did not come from our
        // payment links / invoices is a counter sale — not ours, not logged.
        var box = orderId == null ? null : await conn.QueryFirstOrDefaultAsync(
            "SELECT id, pallet_number, publish_state FROM dbo.manifests WHERE checkout_order_id = @oid",
            new { oid = orderId });
        if (box == null && !SquareEvents.IsOurProduct(pay.Product))
        {
            _log.LogInformation("SquareWebhook: ignoring {Product} payment {PaymentId} (floor/other)", pay.Product ?? "?", paymentId);
            return new OkObjectResult(new { ignored = "floor" });
        }

        // Idempotency anchor: one row per Square payment, ever.
        var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, amount_cents, currency, status, event_json)
SELECT @pid, @oid, @amt, 'USD', 'COMPLETED', @json
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
            new { pid = paymentId, oid = orderId, amt = amount, json = raw });
        if (inserted == 0)
            return new OkObjectResult(new { duplicate = true });   // retry/replay — already handled

        if (box == null)
        {
            // Money arrived from OUR product for an order we can't match — flag for a human.
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                new { pid = paymentId });
            _log.LogError("SquareWebhook: COMPLETED {Product} payment {PaymentId} matched no box (order {OrderId})",
                pay.Product, paymentId, orderId);
            return new OkObjectResult(new { unmatched = true });
        }

        Guid manifestId = (Guid)box.id;
        if ((string)box.publish_state == "sold")
        {
            // The documented delete-race: second payment on an already-sold
            // box. Keep the money trail, flag for refund.
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET manifest_id = @mid, needs_refund = 1, status = 'REFUND_FLAGGED' WHERE square_payment_id = @pid",
                new { mid = manifestId, pid = paymentId });
            _log.LogError("SquareWebhook: payment {PaymentId} for ALREADY-SOLD box #{Num} — flagged for refund",
                paymentId, (object?)box.pallet_number);
            return new OkObjectResult(new { refundFlagged = true });
        }

        await conn.ExecuteAsync(
            "UPDATE dbo.payments SET manifest_id = @mid WHERE square_payment_id = @pid",
            new { mid = manifestId, pid = paymentId });
        await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @mid, @publish_state = 'sold'",
            new { mid = manifestId });
        await PalletsFunction.InsertHistoryAsync(conn, manifestId, "publish_state", (string?)box.publish_state, "sold", "square");

        _log.LogInformation("SquareWebhook: BOX #{Num} SOLD via payment {PaymentId}",
            (object?)box.pallet_number, paymentId);
        return new OkObjectResult(new { sold = true, box = (int?)box.pallet_number });
    }

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
