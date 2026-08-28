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

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != "payment.updated" && type != "payment.created")
            return new OkObjectResult(new { ignored = type });   // subscribed but not relevant

        var payment = root.GetProperty("data").GetProperty("object").GetProperty("payment");
        var status = payment.TryGetProperty("status", out var st) ? st.GetString() : null;
        if (status != "COMPLETED")
            return new OkObjectResult(new { ignored = status });

        var paymentId = payment.GetProperty("id").GetString()!;
        var orderId   = payment.TryGetProperty("order_id", out var o) ? o.GetString() : null;
        long? amount  = payment.TryGetProperty("amount_money", out var am) &&
                        am.TryGetProperty("amount", out var amv) ? amv.GetInt64() : null;

        await using var conn = await _sql.OpenAsync(ct);

        // Idempotency anchor: one row per Square payment, ever.
        var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, amount_cents, currency, status, event_json)
SELECT @pid, @oid, @amt, 'USD', 'COMPLETED', @json
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
            new { pid = paymentId, oid = orderId, amt = amount, json = raw });
        if (inserted == 0)
            return new OkObjectResult(new { duplicate = true });   // retry/replay — already handled

        var box = orderId == null ? null : await conn.QueryFirstOrDefaultAsync(
            "SELECT id, pallet_number, publish_state FROM dbo.manifests WHERE checkout_order_id = @oid",
            new { oid = orderId });

        if (box == null)
        {
            // Money arrived for an order we can't match — flag for a human.
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                new { pid = paymentId });
            _log.LogError("SquareWebhook: COMPLETED payment {PaymentId} matched no box (order {OrderId})",
                paymentId, orderId);
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

        _log.LogInformation("SquareWebhook: BOX #{Num} SOLD via payment {PaymentId}",
            (object?)box.pallet_number, paymentId);
        return new OkObjectResult(new { sold = true, box = (int?)box.pallet_number });
    }

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
        var open = (await conn.QueryAsync(@"
SELECT TOP 50 id, pallet_number, publish_state, archived_at, checkout_link_id, checkout_order_id
FROM dbo.manifests
WHERE checkout_order_id IS NOT NULL AND publish_state <> 'sold'
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
