using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NSL.Api.Functions;

namespace NSL.Api.Services;

/// <summary>
/// What one fulfilment did. <paramref name="Sold"/> counts every box this order
/// owns (including ones an earlier call already sold); <paramref name="NewlySold"/>
/// counts only the ones THIS call transitioned. Notify buyers off NewlySold —
/// Sold on a re-run would send the confirmation twice.
/// </summary>
public sealed record FulfillResult(string Outcome, int Sold, int NewlySold, int Unavailable,
    long RefundDueCents, List<int> PalletNumbers, List<int> UnavailablePalletNumbers);

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

    /// <summary>
    /// CALLER CONTRACT: this method assumes the payment has ALREADY been filtered
    /// to one of ours. The Square merchant account is shared with the floor POS,
    /// so a cash sale at the counter raises payment.updated too — callers MUST
    /// gate on <see cref="SquareEvents.IsOurProduct"/> (ECOMMERCE_API | INVOICES)
    /// before calling. Nothing below re-checks it: an unfiltered floor payment
    /// reaches the zero-boxes branch and gets flagged for a refund that is not
    /// owed. db/hotfix-floor-payments.sql exists because that already happened
    /// once; do not regress it.
    /// </summary>
    /// <remarks>
    /// <paramref name="orderId"/> is nullable because a payment from one of our
    /// own products can still arrive with no order_id at all. Every lookup below
    /// then misses, the recovery call is skipped, and the payment lands on the
    /// UNMATCHED / needs_refund path — money in, nothing sold, a human told.
    /// </remarks>
    public async Task<FulfillResult> FulfillOrderAsync(SqlConnection conn, string? orderId, string paymentId,
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
                    return new FulfillResult("duplicate", 0, 0, 0, 0, new List<int>(), new List<int>());
                }

                var order = await conn.QueryFirstOrDefaultAsync(
                    "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                if (order == null)
                {
                    // Fallback correlation: the line-item uids ARE our manifest ids.
                    // Read the WHOLE order, not just the uids: spec §8.6 — we never
                    // compute the authoritative tax, and every money column has to be
                    // what the buyer was actually charged, because §8.8's refunds are
                    // computed straight off these rows. Re-pricing the boxes from
                    // today's ask price would refund the wrong amount for any box
                    // whose price moved after the link was minted.
                    var recovered = orderId != null && _square.Configured
                        ? await _square.OrderLinesAsync(orderId, ct)
                        : new SquareService.RecoveredOrder(new List<SquareService.OrderLine>(), null, null, null);
                    if (recovered.Lines.Count == 0)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                            new { pid = paymentId }, transaction: tx);
                        tx.Commit();
                        _log.LogError("Fulfill: payment {PaymentId} matched no order and no line uids (order {OrderId})", paymentId, orderId);
                        return new FulfillResult("unmatched", 0, 0, 0, 0, new List<int>(), new List<int>());
                    }

                    // Recovery path: we never saw the create response, so the order row
                    // is rebuilt from Square's own figures. Delivery is Square's service
                    // charge; a line whose money Square didn't return falls back to the
                    // box's current price and the 7.25% rate as a LAST resort, and that
                    // fallback is what the "recovered order" warning below asks a human
                    // to reconcile against the Square dashboard.
                    long recoveredTotal = recovered.TotalCents ?? amountCents ?? 0;
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, status, total_cents, delivery_cents) VALUES (@oid, 'link', 'open', @total, @delivery)",
                        new { oid = orderId, total = recoveredTotal, delivery = recovered.DeliveryCents ?? 0 }, transaction: tx);
                    // One statement per line so each box can carry its OWN money. Dapper
                    // runs the command once per element of the list.
                    var boxRows = recovered.Lines
                        .Select(l => new { oid = orderId, mid = l.ManifestId, amt = l.AmountCents, tax = l.TaxCents, rate = TaxRate })
                        .ToList();
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents)
SELECT @oid, p.manifest_id, amt.amount_cents,
       COALESCE(@tax, CAST(ROUND(amt.amount_cents * @rate, 0) AS BIGINT))
FROM dbo.v_pallets p
CROSS APPLY (SELECT COALESCE(@amt, CAST(ROUND(COALESCE(p.sale_price, p.list_price, p.total_wholesale, 0) * 100, 0) AS BIGINT)) AS amount_cents) amt
WHERE p.manifest_id = @mid",
                        boxRows, transaction: tx);
                    if (recovered.TaxCents.HasValue)
                    {
                        // Square's own split. subtotal is what is left of the total once
                        // its tax and service charge come out, so the three still sum to
                        // total_cents exactly as the schema promises.
                        await conn.ExecuteAsync(@"
UPDATE dbo.checkout_orders SET tax_cents = @tax, subtotal_cents = @total - @tax - @delivery
WHERE square_order_id = @oid",
                            new { oid = orderId, tax = recovered.TaxCents.Value, total = recoveredTotal, delivery = recovered.DeliveryCents ?? 0 },
                            transaction: tx);
                    }
                    else
                    {
                        // No order-level tax from Square: derive it as the SUM of the
                        // per-box values just inserted, never by rounding the order total
                        // separately, or the two disagree by a cent or two and the
                        // sum(box.tax_cents) == order.tax_cents invariant fails. Leaving
                        // tax at the column default of 0 would under-refund the buyer's
                        // tax on a later partial refund (spec §8.8).
                        await conn.ExecuteAsync(@"
UPDATE dbo.checkout_orders
SET tax_cents      = COALESCE((SELECT SUM(tax_cents)    FROM dbo.checkout_order_boxes WHERE square_order_id = @oid), 0),
    subtotal_cents = COALESCE((SELECT SUM(amount_cents) FROM dbo.checkout_order_boxes WHERE square_order_id = @oid), 0)
WHERE square_order_id = @oid",
                            new { oid = orderId }, transaction: tx);
                    }
                    int derivedLines = recovered.Lines.Count(l => l.AmountCents == null || l.TaxCents == null);
                    _log.LogWarning("Fulfill: recovered order {OrderId} from {N} Square lines ({Derived} missing money, priced from the box instead)",
                        orderId, recovered.Lines.Count, derivedLines);
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
                    return new FulfillResult("unmatched", 0, 0, 0, 0, new List<int>(), new List<int>());
                }

                string kind = (string)order.kind;
                string? orderStatus = (string?)order.status;
                // ORDER BY manifest_id is deadlock safety, not cosmetics: every
                // fulfilment locks the boxes of an order in the same sequence, so two
                // concurrent orders that share a box queue behind one another instead
                // of each holding what the other needs. This SELECT itself takes NO
                // lock — it is the work list, not the decision. The decision is made
                // per box against a locked re-read inside the loop (see below).
                var boxes = (await conn.QueryAsync(@"
SELECT b.manifest_id, b.amount_cents, b.tax_cents, b.outcome, m.pallet_number
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
                    return new FulfillResult("unmatched", 0, 0, 0, 0, new List<int>(), new List<int>());
                }

                int sold = 0, newlySold = 0, unavailable = 0;
                long refundDue = 0;
                var soldIds = new List<Guid>();
                var pallets = new List<int>();
                var unavailablePallets = new List<int>();
                foreach (var b in boxes)
                {
                    Guid mid = (Guid)b.manifest_id;
                    int? palletNumber = (int?)b.pallet_number;
                    string? outcome = (string?)b.outcome;
                    // refundDue is always TAX-INCLUSIVE: the buyer paid tax on a
                    // box they are not getting, and we cannot remit it against a
                    // sale that did not happen (spec §8.8).
                    long boxDue = (long)b.amount_cents + (long)b.tax_cents;
                    if (outcome == "sold")                                           // re-entrant: already ours
                    {
                        sold++;
                        AddPallet(pallets, palletNumber, mid);
                        continue;
                    }
                    if (outcome == "unavailable")
                    {
                        unavailable++;
                        refundDue += boxDue;
                        AddPallet(unavailablePallets, palletNumber, mid);
                        continue;
                    }

                    // C1: take the row lock BEFORE deciding, and decide from what the
                    // lock returns — never from the unlocked work-list SELECT above.
                    // Azure SQL runs READ_COMMITTED_SNAPSHOT by default, so without a
                    // lock hint both fulfilments of a contested box read the same
                    // pre-transaction snapshot, both see "live", and both sell it: two
                    // buyers, one box, neither payment flagged (spec §5's "second pays
                    // anyway"). UPDLOCK opts this one statement out of row versioning:
                    // the second fulfilment blocks here until the first commits and
                    // then reads its 'sold'. Single row, by primary key, walked in the
                    // manifest_id order of the loop — so lock ordering stays
                    // deterministic. A set-level UPDLOCK,HOLDLOCK over the join would
                    // NOT give that: ORDER BY is applied after the scan, so the locks
                    // would be taken in whatever order the plan happened to scan.
                    var locked = await conn.QueryFirstOrDefaultAsync(@"
SELECT publish_state, archived_at, is_ghost, invoice_id
FROM dbo.manifests WITH (UPDLOCK, ROWLOCK)
WHERE id = @mid",
                        new { mid }, transaction: tx);

                    string? publishState = null;
                    bool ok = false;
                    if (locked != null)
                    {
                        publishState = (string?)locked.publish_state;
                        ok = Availability.For(kind, publishState,
                            (DateTime?)locked.archived_at, locked.is_ghost == true, (string?)locked.invoice_id);
                    }
                    if (ok)
                    {
                        // Belt and braces: the proc's UPDATE is unconditional and its
                        // rowcount is not surfaced, so read back the state it returns and
                        // insist the transition actually landed. Throwing rolls the whole
                        // transaction back — payment anchor included — which is the safe
                        // direction: Square retries rather than us recording a sale that
                        // did not happen.
                        var after = await conn.QueryFirstOrDefaultAsync(
                            "EXEC dbo.sp_SetPublishState @manifest_id = @mid, @publish_state = 'sold'",
                            new { mid }, transaction: tx);
                        string? afterState = after == null ? null : (string?)after.publish_state;
                        if (afterState != "sold")
                            throw new InvalidOperationException(
                                $"sp_SetPublishState did not move manifest {mid} to sold (order {orderId}, payment {paymentId})");

                        await PalletsFunction.InsertHistoryAsync(conn, mid, "publish_state", publishState, "sold", source, tx);
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'sold', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        sold++;
                        newlySold++;
                        soldIds.Add(mid);
                        AddPallet(pallets, palletNumber, mid);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'unavailable', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        unavailable++;
                        refundDue += boxDue;
                        AddPallet(unavailablePallets, palletNumber, mid);
                        _log.LogWarning("Fulfill: BOX #{Num} on order {OrderId} no longer available (state {State}) — refund due {Due}c incl tax",
                            (object?)palletNumber, orderId, publishState ?? "(row gone)", boxDue);
                    }
                }

                long total = (long)order.total_cents;
                // I1: a SECOND, distinct payment id against an order we already
                // fulfilled. Every box reads 'sold' — sold by US, on the earlier
                // payment — so nothing transitioned here and the naive arithmetic
                // says refundDue = 0, COMPLETED. That is money in with nothing newly
                // sold, which always gets an attention row. Square split tender lands
                // here too and is the genuine ambiguity, so flag rather than refund
                // automatically: clearing a flag is one click, silently keeping a
                // double charge is not.
                bool duplicateTender = newlySold == 0 && sold > 0 && orderStatus == "paid";
                if (duplicateTender)
                {
                    refundDue = amountCents ?? refundDue;
                    _log.LogError("Fulfill: payment {PaymentId} sold NOTHING new on order {OrderId}, already paid — possible double charge or split tender, flagged for review ({Amt}c)",
                        paymentId, orderId, amountCents);
                }
                else if (sold == 0 && refundDue > 0)
                {
                    // Nothing sold: there is no delivery to make either, so the
                    // delivery fee and its own tax go back too. total_cents is
                    // Square's total_money, so this is the whole payment.
                    refundDue = total > 0 ? total : refundDue;
                }
                bool needsRefund = refundDue > 0 || duplicateTender;
                string status = duplicateTender ? "REFUND_FLAGGED"
                    : refundDue == 0 ? "COMPLETED"
                    : (sold == 0 ? "REFUND_FLAGGED" : "PARTIAL_REFUND_FLAGGED");
                Guid? single = boxes.Count == 1 ? (Guid)boxes[0].manifest_id : null;
                await conn.ExecuteAsync(@"
UPDATE dbo.payments SET manifest_id = @mid, refund_due_cents = @due, needs_refund = @flag, status = @status
WHERE square_payment_id = @pid",
                    new { mid = single, due = refundDue > 0 ? refundDue : (long?)null, flag = needsRefund, status, pid = paymentId },
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
                result = new FulfillResult("fulfilled", sold, newlySold, unavailable, refundDue, pallets, unavailablePallets);
                _log.LogInformation("Fulfill: order {OrderId} payment {PaymentId} via {Source}: {Sold} sold ({New} newly), {Unav} unavailable, refund due {Due}",
                    orderId, paymentId, source, sold, newlySold, unavailable, refundDue);
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
    /// manifests.pallet_number is nullable, and a box with no number would silently
    /// vanish from the buyer's list. Log it rather than drop it quietly — the counts
    /// on <see cref="FulfillResult"/> stay authoritative either way.
    /// </summary>
    private void AddPallet(List<int> into, int? palletNumber, Guid manifestId)
    {
        if (palletNumber.HasValue) into.Add(palletNumber.Value);
        else _log.LogWarning("Fulfill: manifest {ManifestId} has no pallet_number — omitted from the box list", manifestId);
    }

    /// <summary>
    /// Apply one Square refund to our audit row exactly once (spec §8.8).
    ///
    /// Refunds ACCUMULATE: a buyer can be refunded one unavailable box today and
    /// another tomorrow, and Square sends refund.updated several times per refund
    /// besides. dbo.payment_refunds is keyed on the refund id, so the INSERT is
    /// the dedupe — only a row that did not exist is allowed to move
    /// payments.refunded_cents, and a replay is a no-op that returns false.
    ///
    /// status: REFUNDED once the whole payment is back, PARTIAL_REFUNDED while
    /// some of it is. The attention flag is a separate question and clears on a
    /// separate test — what is OWED (refund_due_cents: the unavailable boxes and
    /// their tax), not the whole payment — so refunding exactly the one box the
    /// buyer did not get clears the flag without pretending the order was voided.
    /// The caller decides what counts as returned: only a COMPLETED refund gets
    /// here, never one Square has merely accepted.
    ///
    /// The INSERT is UNCONDITIONAL, including for a payment we have no row for:
    /// it is the only durable evidence the refund happened, and skipping unknown
    /// payments would drop a genuinely out-of-order refund forever. When the
    /// payments UPDATE then matches nothing, that is our books and Square's
    /// disagreeing about real money already returned to a real customer, so it is
    /// logged as a warning naming all three figures. The return value does not
    /// change: the webhook must still answer 200, because a refund we cannot
    /// attribute is not a reason to make Square retry it eleven times.
    /// </summary>
    /// <returns>true if this refund was newly recorded; false on a replay.</returns>
    public async Task<bool> RecordRefundAsync(SqlConnection conn, string refundId, string paymentId, long amountCents)
    {
        var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payment_refunds (square_refund_id, square_payment_id, amount_cents)
SELECT @rid, @pid, @amt
WHERE NOT EXISTS (SELECT 1 FROM dbo.payment_refunds WHERE square_refund_id = @rid)",
            new { rid = refundId, pid = paymentId, amt = amountCents });
        if (inserted == 0) return false;

        // refunded_cents + @amt, not @amt: every column below is the running
        // total after this refund, so two partials add up instead of the second
        // overwriting the first.
        var matched = await conn.ExecuteAsync(@"
UPDATE dbo.payments SET
    refunded_cents = refunded_cents + @amt,
    status = CASE WHEN refunded_cents + @amt >= COALESCE(amount_cents, 0) THEN 'REFUNDED' ELSE 'PARTIAL_REFUNDED' END,
    needs_refund = CASE WHEN refunded_cents + @amt >= COALESCE(refund_due_cents, amount_cents, 0) THEN 0 ELSE needs_refund END
WHERE square_payment_id = @pid", new { pid = paymentId, amt = amountCents });

        // Zero rows means we have no payments row for this payment at all. The
        // refund row above is recorded, but nothing in dbo.payments will ever
        // reflect it: our books and Square now disagree about money that has
        // already gone back to a customer. This is NOT a replay or a retry —
        // a replay returned false several lines up — it needs a human.
        if (matched == 0)
            _log.LogWarning(
                "RecordRefund: UNATTRIBUTABLE refund — refund {RefundId} returned {Amount}c against payment {PaymentId}, which has no dbo.payments row. The refund row is recorded but no payment total was updated; our books and Square disagree about money already returned. Reconcile by hand.",
                refundId, amountCents, paymentId);
        return true;
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
            catch (Exception ex)
            {
                // Catches OperationCanceledException too, deliberately: the sale is
                // already committed, and a host shutdown cancelling this token must not
                // turn into an exception out of a method whose whole contract is "never
                // throws". The undeleted link is left for Reconcile either way.
                _log.LogError(ex, "RetireLinks: could not delete link {LinkId} (order {OrderId}) — Reconcile will retry", l.LinkId, l.OrderId);
            }
        }
    }
}
