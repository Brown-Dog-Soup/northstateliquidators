using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NSL.Api.Functions;

namespace NSL.Api.Services;

public sealed record FulfillResult(string Outcome, int Sold, int Unavailable, long RefundDueCents, List<int> PalletNumbers);
public sealed record CanceledLink(string OrderId, string? LinkId);

/// <summary>
/// The one routine that turns "Square says this order is paid" into SOLD
/// boxes (spec §4). Called by the webhook and by Reconcile. Everything from
/// the payments INSERT (idempotency anchor) to the competing-link cancel
/// happens in ONE SqlTransaction, so a crash mid-way rolls back the anchor
/// and Square's retry redoes the whole thing. Square deletes of the canceled
/// links happen AFTER commit, best-effort; Reconcile sweeps the rest.
/// </summary>
public sealed class CheckoutFulfillment
{
    /// <summary>NC + Wake County combined sales tax — the same rate the cart payload quotes.</summary>
    private const decimal TaxRate = 0.0725m;

    private readonly SquareService _square;
    private readonly ILogger<CheckoutFulfillment> _log;

    public CheckoutFulfillment(SquareService square, ILogger<CheckoutFulfillment> log)
    {
        _square = square;
        _log = log;
    }

    public async Task<FulfillResult> FulfillOrderAsync(SqlConnection conn, string orderId, string paymentId,
        long? amountCents, string? rawJson, string source, CancellationToken ct)
    {
        List<CanceledLink> canceled = new();
        FulfillResult result;
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                // Idempotency anchor. UNIQUE(square_payment_id) plus this guard mean a
                // webhook replay inserts nothing and we bail out as a duplicate. It is
                // inside the transaction on purpose: if anything below throws, the
                // anchor rolls back with it and Square's retry redoes the whole thing.
                var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, amount_cents, currency, status, event_json)
SELECT @pid, @oid, @amt, 'USD', 'COMPLETED', @json
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
                    new { pid = paymentId, oid = orderId, amt = amountCents, json = rawJson }, transaction: tx);
                if (inserted == 0)
                {
                    tx.Rollback();
                    return new FulfillResult("duplicate", 0, 0, 0, new List<int>());
                }

                var order = await conn.QueryFirstOrDefaultAsync(
                    "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                if (order == null)
                {
                    // Fallback correlation: the line-item uids ARE our manifest ids.
                    var ids = _square.Configured ? await _square.OrderLineUidsAsync(orderId, ct) : new List<Guid>();
                    if (ids.Count == 0)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                            new { pid = paymentId }, transaction: tx);
                        tx.Commit();
                        _log.LogError("Fulfill: payment {PaymentId} matched no order and no line uids (order {OrderId})", paymentId, orderId);
                        return new FulfillResult("unmatched", 0, 0, 0, new List<int>());
                    }
                    // Recovery path: we never saw the create response, so the
                    // tax/delivery split is unknown. Record the paid amount as
                    // the total and DERIVE the tax from the same 7.25% rate the
                    // payload uses, so that sum(box.tax_cents) == order.tax_cents
                    // still holds on a recovered order, and a later partial refund
                    // reads the box rows correctly (spec §8.8). Delivery stays 0: a
                    // recovered order cannot tell a $10 fee from $10 of goods. The
                    // row is flagged by the "recovered order" warning below for a
                    // human to reconcile against the Square dashboard.
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, status, total_cents) VALUES (@oid, 'link', 'open', @total)",
                        new { oid = orderId, total = amountCents ?? 0 }, transaction: tx);
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents)
SELECT @oid, p.manifest_id, amt.amount_cents,
       CAST(ROUND(amt.amount_cents * @rate, 0) AS BIGINT)
FROM dbo.v_pallets p
CROSS APPLY (SELECT CAST(ROUND(COALESCE(p.sale_price, p.list_price, p.total_wholesale, 0) * 100, 0) AS BIGINT) AS amount_cents) amt
WHERE p.manifest_id IN @ids",
                        new { oid = orderId, ids, rate = TaxRate }, transaction: tx);
                    // Bring the order row into agreement with its boxes. tax_cents is
                    // DERIVED here, not quoted by Square: we never saw the create
                    // response for a recovered order. Leaving it at the column default
                    // of 0 would under-refund the buyer's tax on a later partial refund
                    // (spec §8.8). SUM the per-box values rather than rounding the order
                    // total separately, or the two disagree by a cent or two and the
                    // sum(box.tax_cents) == order.tax_cents invariant fails.
                    await conn.ExecuteAsync(@"
UPDATE dbo.checkout_orders
SET tax_cents      = COALESCE((SELECT SUM(tax_cents)    FROM dbo.checkout_order_boxes WHERE square_order_id = @oid), 0),
    subtotal_cents = COALESCE((SELECT SUM(amount_cents) FROM dbo.checkout_order_boxes WHERE square_order_id = @oid), 0)
WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                    _log.LogWarning("Fulfill: recovered order {OrderId} from {N} line uids", orderId, ids.Count);
                    order = await conn.QueryFirstOrDefaultAsync(
                        "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                }

                if (order == null)
                {
                    // Unreachable in practice — the recovery path above inserts the row
                    // it then re-reads. Handled anyway because the alternative is an NRE
                    // that rolls the payment anchor back and has Square retry forever:
                    // money in with nothing sold ALWAYS leaves an attention row instead.
                    await conn.ExecuteAsync(
                        "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                        new { pid = paymentId }, transaction: tx);
                    tx.Commit();
                    _log.LogError("Fulfill: order {OrderId} vanished between recovery insert and re-read — payment {PaymentId} flagged UNMATCHED", orderId, paymentId);
                    return new FulfillResult("unmatched", 0, 0, 0, new List<int>());
                }

                string kind = (string)order.kind;
                // ORDER BY manifest_id is deadlock safety, not cosmetics: every
                // fulfilment locks the boxes of an order in the same sequence, so two
                // concurrent orders that share a box queue behind one another instead
                // of each holding what the other needs.
                var boxes = (await conn.QueryAsync(@"
SELECT b.manifest_id, b.amount_cents, b.tax_cents, b.outcome, m.pallet_number, m.publish_state, m.archived_at, m.is_ghost, m.invoice_id
FROM dbo.checkout_order_boxes b JOIN dbo.manifests m ON m.id = b.manifest_id
WHERE b.square_order_id = @oid ORDER BY b.manifest_id",
                    new { oid = orderId }, transaction: tx)).ToList();

                if (boxes.Count == 0)
                {
                    // Money in with nothing sold ALWAYS raises an attention row.
                    // Never mark this COMPLETED and walk away.
                    await conn.ExecuteAsync(
                        "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                        new { pid = paymentId }, transaction: tx);
                    await conn.ExecuteAsync(
                        "UPDATE dbo.checkout_orders SET status = 'paid', closed_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                    tx.Commit();
                    _log.LogError("Fulfill: order {OrderId} payment {PaymentId} resolved to ZERO boxes — flagged UNMATCHED, refund owed", orderId, paymentId);
                    return new FulfillResult("unmatched", 0, 0, 0, new List<int>());
                }

                int sold = 0, unavailable = 0;
                long refundDue = 0;
                var soldIds = new List<Guid>();
                var pallets = new List<int>();
                foreach (var b in boxes)
                {
                    Guid mid = (Guid)b.manifest_id;
                    int? palletNumber = (int?)b.pallet_number;
                    if (palletNumber.HasValue) pallets.Add(palletNumber.Value);
                    string? outcome = (string?)b.outcome;
                    // refundDue is always TAX-INCLUSIVE: the buyer paid tax on a
                    // box they are not getting, and we cannot remit it against a
                    // sale that did not happen (spec §8.8).
                    long boxDue = (long)b.amount_cents + (long)b.tax_cents;
                    if (outcome == "sold") { sold++; continue; }                     // re-entrant: already ours
                    if (outcome == "unavailable") { unavailable++; refundDue += boxDue; continue; }

                    string? publishState = (string?)b.publish_state;
                    bool isGhost = b.is_ghost == true;
                    bool ok = Availability.For(kind, publishState, (DateTime?)b.archived_at, isGhost, (string?)b.invoice_id);
                    if (ok)
                    {
                        await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @mid, @publish_state = 'sold'",
                            new { mid }, transaction: tx);
                        await PalletsFunction.InsertHistoryAsync(conn, mid, "publish_state", publishState, "sold", source, tx);
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'sold', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        sold++;
                        soldIds.Add(mid);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'unavailable', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        unavailable++;
                        refundDue += boxDue;
                        _log.LogWarning("Fulfill: BOX #{Num} on order {OrderId} no longer available (state {State}) — refund due {Due}c incl tax",
                            (object?)palletNumber, orderId, publishState, boxDue);
                    }
                }

                long total = (long)order.total_cents;
                if (sold == 0 && refundDue > 0)
                {
                    // Nothing sold: there is no delivery to make either, so the
                    // delivery fee and its own tax go back too. total_cents is
                    // Square's total_money, so this is the whole payment.
                    refundDue = total > 0 ? total : refundDue;
                }
                string status = refundDue == 0 ? "COMPLETED" : (sold == 0 ? "REFUND_FLAGGED" : "PARTIAL_REFUND_FLAGGED");
                Guid? single = boxes.Count == 1 ? (Guid)boxes[0].manifest_id : null;
                await conn.ExecuteAsync(@"
UPDATE dbo.payments SET manifest_id = @mid, refund_due_cents = @due, needs_refund = @flag, status = @status
WHERE square_payment_id = @pid",
                    new { mid = single, due = refundDue > 0 ? refundDue : (long?)null, flag = refundDue > 0, status, pid = paymentId },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE dbo.checkout_orders SET status = 'paid', closed_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                // ORDER total, not the line-item sum: with 7.25% tax and a $10
                // delivery charge the paid amount is legitimately larger than
                // the boxes (spec §8.7). total_cents IS Square's total_money.
                if (amountCents.HasValue && total > 0 && amountCents.Value != total)
                    _log.LogWarning("Fulfill: payment {PaymentId} amount {Amt} != order total {Total} (subtotal+tax+delivery)", paymentId, amountCents, total);

                // DB first, Square second: the fence goes down inside the transaction
                // so a crash before the Square call still leaves no payable link.
                if (soldIds.Count > 0)
                    canceled = await CancelOpenLinksForBoxesAsync(conn, tx, soldIds, exceptOrderId: orderId);

                tx.Commit();
                result = new FulfillResult("fulfilled", sold, unavailable, refundDue, pallets);
                _log.LogInformation("Fulfill: order {OrderId} payment {PaymentId} via {Source}: {Sold} sold, {Unav} unavailable, refund due {Due}",
                    orderId, paymentId, source, sold, unavailable, refundDue);
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }

        await RetireLinksAsync(conn, canceled, ct);
        return result;
    }

    /// <summary>
    /// DB-first fence: mark every OTHER open cart link that contains any of
    /// these boxes as canceled. Runs inside the caller's transaction. The
    /// Square deletes happen later via RetireLinksAsync.
    /// </summary>
    public async Task<List<CanceledLink>> CancelOpenLinksForBoxesAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyCollection<Guid> manifestIds, string? exceptOrderId)
    {
        if (manifestIds.Count == 0) return new List<CanceledLink>();
        var rows = await conn.QueryAsync(@"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
OUTPUT inserted.square_order_id, inserted.square_link_id
FROM dbo.checkout_orders o
WHERE o.status = 'open' AND o.kind = 'link'
  AND (@except IS NULL OR o.square_order_id <> @except)
  AND EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
              WHERE b.square_order_id = o.square_order_id AND b.manifest_id IN @ids)",
            new { except = exceptOrderId, ids = manifestIds.ToArray() }, transaction: tx);
        return rows.Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();
    }

    /// <summary>
    /// Best-effort Square deletes for DB-canceled links. link_deleted_at is
    /// stamped ONLY when Square confirms; anything else is left for Reconcile.
    /// Never throws — the sale is already committed.
    /// </summary>
    public async Task RetireLinksAsync(SqlConnection conn, IEnumerable<CanceledLink> links, CancellationToken ct)
    {
        foreach (var l in links)
        {
            try
            {
                bool confirmed;
                if (l.LinkId == null) confirmed = true;                 // nothing at Square to delete
                else if (!_square.Configured) confirmed = false;         // leave for Reconcile
                else confirmed = await _square.DeletePaymentLinkAsync(l.LinkId, ct);
                if (confirmed)
                    await conn.ExecuteAsync(
                        "UPDATE dbo.checkout_orders SET link_deleted_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                        new { oid = l.OrderId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "RetireLinks: could not delete link {LinkId} (order {OrderId}) — Reconcile will retry", l.LinkId, l.OrderId);
            }
        }
    }
}
