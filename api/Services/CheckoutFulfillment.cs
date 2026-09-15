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
    /// <summary>
    /// NC + Wake County combined sales tax — the same rate the cart payload
    /// quotes. Used ONLY as recovery's last resort, for a rebuilt line whose tax
    /// Square did not return; every other figure in this file is Square's own.
    ///
    /// ONE OF FOUR COPIES, and changing the rate means changing all of them plus
    /// deploying: SquareFunction's <c>taxPercent</c> in the checkout-status
    /// response, <c>TAX_PCT</c> in js/site.js, and SquarePayloads.TaxPercent.
    /// Editing the Square catalog tax object changes what the buyer is CHARGED
    /// and nothing here follows it.
    /// </summary>
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
                    "SELECT kind, status, total_cents, subtotal_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                // How many lines Square returned that we could NOT write an order
                // line for, because the box they name no longer exists. Only the
                // recovery path below can learn this — on every other path the
                // order row is ours and its lines were written when the link was
                // minted — so it stays 0 elsewhere and the goods backstop, which is
                // exact on those paths, remains the whole of the check there.
                int recoveredLinesWithNoBox = 0;

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
                    // COUNT WHAT THIS ACTUALLY WROTE. Dapper runs the statement once
                    // per element and returns the SUM of the rowcounts, and the
                    // statement selects FROM dbo.v_pallets keyed on the manifest —
                    // a view that is one row per dbo.manifests row, with no filter
                    // (db/wishlist4.sql). So it writes exactly one row per line
                    // whose box still exists and NO row for one whose box has been
                    // hard-deleted, and the shortfall is an exact count of boxes
                    // this buyer paid for that we cannot record a line for.
                    //
                    // This is the ONLY signal that works on both recovery branches.
                    // The goods backstop after the loop compares the order's
                    // subtotal against the surviving lines, and on the no-order-tax
                    // branch below the subtotal is SET to the sum of these very
                    // rows — so the missing box's money is erased from the subtotal
                    // by the same statement that should have exposed it, the
                    // shortfall computes to zero, and the payment completes
                    // silently. A row count needs no arithmetic and does not care
                    // which branch ran.
                    int boxRowsWritten = await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents)
SELECT @oid, p.manifest_id, amt.amount_cents,
       COALESCE(@tax, CAST(ROUND(amt.amount_cents * @rate, 0) AS BIGINT))
FROM dbo.v_pallets p
CROSS APPLY (SELECT COALESCE(@amt, CAST(ROUND(COALESCE(p.sale_price, p.list_price, p.total_wholesale, 0) * 100, 0) AS BIGINT)) AS amount_cents) amt
WHERE p.manifest_id = @mid",
                        boxRows, transaction: tx);
                    // Strictly less, never "not equal": a duplicated uid or a view
                    // that ever returned two rows would read as a surplus, and a
                    // surplus is not a missing box. Only a shortfall flags.
                    recoveredLinesWithNoBox = recovered.Lines.Count - boxRowsWritten;
                    if (recoveredLinesWithNoBox < 0) recoveredLinesWithNoBox = 0;
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
                        "SELECT kind, status, total_cents, subtotal_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
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

                // A payment against an order WE had already closed. Until now the
                // only status this method looked at was 'paid' (the split-tender
                // test below), so this arrived looking exactly like an ordinary
                // first payment on an open order and left no trace of the one
                // thing that makes it interesting: something on our side — a
                // staff cancel, a price change, a box sold elsewhere, a sweep —
                // decided this order was dead while the link was still payable at
                // Square, and it was paid anyway. The boxes have very likely moved
                // on since, so the refund arithmetic below is the part that
                // matters; this line is what tells whoever reads it WHY. Logged,
                // not refused: the money is real and fulfilment is still the right
                // thing to attempt.
                if (orderStatus == "canceled")
                    _log.LogError("Fulfill: payment {PaymentId} landed on order {OrderId}, which our row already had CANCELED — the link outlived our cancel and was paid. Fulfilling anyway; anything no longer available below is a refund the buyer is owed.",
                        paymentId, orderId);
                // ORDER BY manifest_id is deadlock safety, not cosmetics: every
                // fulfilment locks the boxes of an order in the same sequence, so two
                // concurrent orders that share a box queue behind one another instead
                // of each holding what the other needs. This SELECT itself takes NO
                // lock — it is the work list, not the decision. The decision is made
                // per box against a locked re-read inside the loop (see below).
                //
                // LEFT JOIN, and that is the money-safe direction, not a style
                // choice. dbo.checkout_order_boxes has no FK to manifests, so an
                // order line can outlive the box it names — three ways, all of
                // them real:
                //   * PalletsFunction.Delete marks these rows 'unavailable' and
                //     leaves them (it used to delete them, which is the hole this
                //     join and that change close between them);
                //   * ops scripts delete manifests and never touch order lines
                //     (db/reset-test-inventory.sql, db/ghost-backstock-category-filter.sql);
                //   * anything else that removes a manifest row by hand.
                // An inner join DROPPED such a line from this list entirely: on a
                // multi-box order whose link outlived our cancel, fulfilment would
                // sell the survivors, compute nothing owed for the box that no
                // longer exists, and record the payment COMPLETED — the buyer paid
                // for a box they can never get and NOTHING marked it. (Not even
                // the amount-mismatch warning below: that compares the payment
                // against the ORDER total, and the buyer paid the order total
                // exactly.) Kept in the list, the row falls through the locked
                // re-read below (which finds no manifest), is marked 'unavailable'
                // and its money — price AND tax — is added to refundDue, which is
                // what the buyer is actually owed. Its pallet_number comes back
                // NULL: a deleted manifest has no BOX # to report, so it is
                // omitted from the reported numbers while the counts and the
                // refund total still include it (see AddPallet).
                //
                // This join can only defend a line that still EXISTS. The shape
                // where the line itself is gone is caught by the goods-accounted
                // backstop after the loop, which needs no line at all.
                var boxes = (await conn.QueryAsync(@"
SELECT b.manifest_id, b.amount_cents, b.tax_cents, b.outcome, m.pallet_number
FROM dbo.checkout_order_boxes b LEFT JOIN dbo.manifests m ON m.id = b.manifest_id
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
                        // A hard-deleted box has neither a pallet_number nor a
                        // publish_state to name, so both degrade to a literal
                        // rather than logging "BOX #" with a hole in it.
                        _log.LogWarning("Fulfill: BOX #{Num} on order {OrderId} no longer available (state {State}) — refund due {Due}c incl tax",
                            (object?)palletNumber ?? "(deleted)", orderId, publishState ?? "(row gone)", boxDue);
                    }
                }

                long total = (long)order.total_cents;

                // THE BACKSTOP. Everything above reasons from the order lines we
                // can see; this one asks whether they are all still there, and it
                // needs no line to do it. The order's own goods figure still
                // carries the money for a line that has since vanished — a
                // hand-edit, an ops script, a row lost to something nobody
                // anticipated — and without this, that shortfall is invisible: the
                // buyer paid, the survivors sold, refundDue came out 0 and the
                // payment recorded COMPLETED.
                long unaccountedGoods = UnaccountedGoodsCents(
                    (long)order.subtotal_cents, boxes.Select(b => (long)b.amount_cents));
                // I1: a SECOND, distinct payment id against an order we already
                // fulfilled. Every box reads 'sold' — sold by US, on the earlier
                // payment — so nothing transitioned here and the naive arithmetic
                // says refundDue = 0, COMPLETED. That is money in with nothing newly
                // sold, which always gets an attention row. Square split tender lands
                // here too and is the genuine ambiguity, so flag rather than refund
                // automatically: clearing a flag is one click, silently keeping a
                // double charge is not.
                bool duplicateTender = newlySold == 0 && sold > 0 && orderStatus == "paid";
                var verdict = DecidePaymentOutcome(duplicateTender, sold, refundDue, total, unaccountedGoods, amountCents,
                    linesKnownMissing: recoveredLinesWithNoBox > 0);
                refundDue = verdict.RefundDueCents;
                if (duplicateTender)
                    _log.LogError("Fulfill: payment {PaymentId} sold NOTHING new on order {OrderId}, already paid — possible double charge or split tender, flagged for review ({Amt}c)",
                        paymentId, orderId, amountCents);
                if (recoveredLinesWithNoBox > 0)
                    _log.LogError(
                        "Fulfill: order {OrderId} payment {PaymentId} — Square returned {Missing} line(s) whose box no longer exists, so no order line could be written for them and nothing above can owe the buyer for them. FLAGGED for a human; refund_due_cents reads {Due}. That figure is NOT the bill — work out what is owed from the Square receipt.",
                        orderId, paymentId, recoveredLinesWithNoBox, (object?)verdict.RecordedDueCents ?? "(unset, on purpose)");
                if (unaccountedGoods > 0)
                    _log.LogError(
                        "Fulfill: order {OrderId} payment {PaymentId} is SHORT {Missing}c of goods — its {N} remaining line(s) account for {Accounted}c of a recorded {Recorded}c subtotal. A box this buyer paid for has no order line left at all, so nothing above could owe them for it. FLAGGED for a human; refund_due_cents now reads {Due}. Do not treat that as the bill — the line data is incomplete, so no figure computed from it is the whole debt. Work out what is owed from the Square receipt.",
                        orderId, paymentId, unaccountedGoods, boxes.Count, (long)order.subtotal_cents - unaccountedGoods, (long)order.subtotal_cents,
                        (object?)verdict.RecordedDueCents ?? "(unset, on purpose)");
                Guid? single = boxes.Count == 1 ? (Guid)boxes[0].manifest_id : null;
                await conn.ExecuteAsync(@"
UPDATE dbo.payments SET manifest_id = @mid, refund_due_cents = @due, needs_refund = @flag, status = @status
WHERE square_payment_id = @pid",
                    new { mid = single, due = verdict.RecordedDueCents, flag = verdict.NeedsRefund, status = verdict.Status, pid = paymentId },
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
    /// How much of the order's recorded GOODS figure no surviving order line
    /// accounts for. Zero means every cent of it is on a line fulfilment just
    /// looked at; a positive number means at least one line the buyer paid for is
    /// no longer in dbo.checkout_order_boxes at all.
    ///
    /// WHY GOODS AND NOT THE ORDER TOTAL, which is the obvious comparison and the
    /// one the fix was asked for. The total is subtotal + tax + delivery, and
    /// checking against it means reconstructing the tax and the delivery tax on
    /// this side. Neither is ours to reconstruct. Delivery goes to Square as a
    /// taxed service charge (SquarePayloads.CartLink), so checkout_orders.tax_cents
    /// is Square's total_tax_money INCLUDING the tax on that charge, while the per
    /// box tax_cents cover only the line items — the difference has to be guessed
    /// at 7.25% and rounded, and Square's rounding is not ours to predict. A check
    /// that is off by a cent on every delivery order is a check nobody reads.
    /// The goods figure needs none of that: it is tax-free and delivery-free on
    /// both sides.
    ///
    /// AND IT IS EXACT, on every path that writes these rows — this is what makes
    /// a zero tolerance honest rather than optimistic:
    ///   * cart link — subtotal is written as Square's total MINUS its tax MINUS
    ///     its service charge, which is Square's own arithmetic on the very
    ///     line amounts we sent and stored per box. Integers throughout.
    ///   * cart link with no related_resources.orders — tax and delivery record
    ///     as 0 and subtotal falls back to the sum of our own line amounts.
    ///   * invoice — one box, subtotal written as the same figure as the line.
    ///   * recovery, Square gave an order-level tax — subtotal is total - tax -
    ///     delivery, the boxes are Square's own line amounts.
    ///   * the pre-cart migration backfill (db/cart-checkout.sql) — writes the
    ///     order and its single box from the same expression.
    ///
    /// THE ONE PATH IT IS STRUCTURALLY BLIND ON, and the reason the caller does
    /// not rely on this function alone. Recovery with NO order-level tax sets the
    /// subtotal to SUM(amount_cents) over the rows it just inserted — and that
    /// insert selects FROM dbo.v_pallets keyed on the manifest, so a box that has
    /// been hard-deleted produces no row. Its money never enters the subtotal in
    /// the first place, both sides of the subtraction shrink together, and this
    /// returns 0 for an order that IS short a box. Nothing about this function can
    /// see that; the missing line is missing from its input. The caller therefore
    /// also counts the rows that insert wrote against the lines Square returned
    /// (linesKnownMissing on <see cref="DecidePaymentOutcome"/>), which is exact
    /// on both recovery branches and needs no arithmetic at all.
    ///
    /// THE ONE WAY IT CAN OVERSTATE, and it is why the caller only flags. On the
    /// recovery path a line whose money Square did not return is priced from the
    /// box's CURRENT ask instead. If that ask has dropped since the link was
    /// minted, the stored line is smaller than the buyer's share of the subtotal
    /// and this reads as a shortfall with nothing actually missing. That is not
    /// rounding and no tolerance bounds it, so a positive answer here is a reason
    /// to LOOK, never a figure to refund. The same call already logs "recovered
    /// order ... N missing money", which is what a reader will find next to it.
    ///
    /// Negative differences are clamped to zero and never flag anything. They are
    /// the harmless direction — lines summing to more than the recorded subtotal
    /// is nobody owed anything — and they are how rows predating the
    /// subtotal_cents column (added with DEFAULT 0) read. A legacy order whose
    /// subtotal is 0 must not be reported as fully unaccounted for.
    /// </summary>
    internal static long UnaccountedGoodsCents(long recordedSubtotalCents, IEnumerable<long> lineAmountsCents)
    {
        long accounted = 0;
        foreach (var a in lineAmountsCents) accounted += a;
        long missing = recordedSubtotalCents - accounted;
        return missing > 0 ? missing : 0;
    }

    /// <summary>
    /// What we write on dbo.payments once the boxes have been walked: the refund
    /// arithmetic, the attention flag and the status, in one place with no
    /// database behind it so the decisions can be tested rather than described.
    /// </summary>
    /// <param name="RefundDueCents">What the lines say is owed — reported on
    /// <see cref="FulfillResult"/> and logged.</param>
    /// <param name="RecordedDueCents">What goes in payments.refund_due_cents, which
    /// is NOT always the same number: see <see cref="DecidePaymentOutcome"/>.</param>
    internal readonly record struct PaymentVerdict(long RefundDueCents, long? RecordedDueCents, bool NeedsRefund, string Status);

    /// <summary>
    /// The money decision, pure. Three inputs can each raise the attention flag
    /// and they do not mean the same thing:
    ///
    /// duplicateTender — a second payment id on an order every box of which we
    /// already sold. The whole of THIS payment is the candidate refund and it is
    /// a complete figure, so it is recorded as owed.
    ///
    /// refundDueFromLines — the price and tax of the boxes this order could not
    /// deliver. When nothing at all sold there is no delivery to make either, so
    /// the whole of the PAYMENT goes back rather than just the goods.
    ///
    /// paymentAmountCents — and that word is the fix. WHAT THIS RECORDS IS A DEBT
    /// AGAINST ONE PAYMENT, while the order total is a fact about the ORDER, and
    /// the two are different numbers on a split tender (which the
    /// duplicate-tender branch above exists because of) or on a legacy link
    /// migrated at a different price during a deploy window. This branch used to
    /// record the order total regardless, and every rule that reads the figure
    /// back reasons about the payment, so it disagreed in both directions:
    ///
    ///   * ORDER TOTAL ABOVE THE PAYMENT — needs_refund clears at
    ///     refunded_cents >= refund_due_cents (<see cref="ApplyRefundSql"/>) and
    ///     Square cannot refund more than the payment, so the flag can NEVER come
    ///     down. PlanRefund clamps what is owed to the payment, so once the whole
    ///     payment is back it answers "nothing outstanding" and disables the
    ///     button; Acknowledge refuses a row that HAS a recorded figure; and "Get
    ///     amount from Square" refuses a row that HAS a recorded amount. The row
    ///     nags forever and the only way out is a hand-edit of the database — the
    ///     cry-wolf failure db/hotfix-floor-payments.sql exists to punish,
    ///     arriving from a third direction.
    ///   * ORDER TOTAL BELOW THE PAYMENT — quieter and worse: refunding the
    ///     recorded figure clears the flag with the difference still in our
    ///     account, and nobody ever learns. Nothing sold means we are entitled to
    ///     none of this payment.
    ///
    /// So the figure is the payment when its amount is known, exactly as the
    /// duplicate-tender branch has always done, and the order total only as the
    /// fallback for when it is not. An unknown (null) amount concludes nothing —
    /// we do not reason from a number we do not have — and neither does a zero,
    /// which would erase the debt and with it the flag.
    ///
    /// The same ceiling applies when some boxes DID sell: what the lines say is
    /// owed is still a sum over the order, so it is capped at the payment too.
    ///
    /// unaccountedGoodsCents — the backstop (see
    /// <see cref="UnaccountedGoodsCents"/>). It raises the flag and it
    /// deliberately does NOT raise refund_due_cents. Two reasons, and the second
    /// is the one that matters:
    ///
    ///   1. The figure can overstate. Its one failure mode inflates it, and an
    ///      inflated owed-figure is a figure a human might pay out. A person
    ///      refunding an amount they can see on the Square receipt is a fine
    ///      outcome; our books quoting them a number we cannot stand behind is
    ///      not.
    ///   2. Recording the SMALLER, line-derived figure instead would be worse
    ///      than recording nothing. payments.needs_refund clears automatically
    ///      once refunded_cents reaches COALESCE(refund_due_cents, amount_cents)
    ///      — so quoting a debt we already know is incomplete would let a partial
    ///      refund satisfy it and switch the flag off with money still owed.
    ///      Leaving it NULL makes the whole payment the bar, which is the same
    ///      "we decline to conclude anything from a number we do not have" this
    ///      file applies to a payment of unknown amount.
    ///
    /// linesKnownMissing — the same conclusion reached by COUNTING instead of by
    /// arithmetic: fulfilment rebuilt this order from Square and one of the lines
    /// Square returned named a box that no longer exists, so no order line could
    /// be written for it at all. It means exactly what a positive
    /// unaccountedGoodsCents means and is treated identically, but it is exact
    /// and it survives a branch the subtotal check cannot: when Square gives no
    /// order-level tax, the recovered subtotal is SET to the sum of the rows just
    /// inserted, and the missing box's money is erased from the subtotal by the
    /// very statement meant to expose it. The arithmetic then reads zero. The
    /// count does not.
    ///
    /// duplicateTender keeps its figure regardless: it is the payment amount, not
    /// a sum over lines, so missing lines cannot make it incomplete.
    /// </summary>
    internal static PaymentVerdict DecidePaymentOutcome(
        bool duplicateTender, int sold, long refundDueFromLines, long orderTotalCents,
        long unaccountedGoodsCents, long? paymentAmountCents, bool linesKnownMissing = false)
    {
        long refundDue = refundDueFromLines;
        if (duplicateTender)
            refundDue = paymentAmountCents ?? refundDue;
        else if (sold == 0 && refundDue > 0)
            // Nothing was handed over, so the whole of THIS PAYMENT goes back —
            // the same sentence, and the same line, as the branch above. The
            // order total is the fallback for when the payment's amount is not
            // known, not the answer: it is a fact about the ORDER and this row
            // is a debt against ONE PAYMENT.
            refundDue = paymentAmountCents is > 0 ? paymentAmountCents.Value
                      : orderTotalCents > 0 ? orderTotalCents
                      : refundDue;
        else if (paymentAmountCents is > 0 && refundDue > paymentAmountCents.Value)
            // Some boxes did sell, so what is owed is the lines’ figure — but it
            // is still a sum over the ORDER, and a payment that only part-paid
            // that order can give back no more than it took.
            refundDue = paymentAmountCents.Value;

        bool linesMissing = unaccountedGoodsCents > 0 || linesKnownMissing;
        bool needsRefund = refundDue > 0 || duplicateTender || linesMissing;
        string status = duplicateTender ? "REFUND_FLAGGED"
            : refundDue > 0 ? (sold == 0 ? "REFUND_FLAGGED" : "PARTIAL_REFUND_FLAGGED")
            : linesMissing ? "PARTIAL_REFUND_FLAGGED"
            : "COMPLETED";

        long? recorded = refundDue > 0 ? refundDue : (long?)null;
        if (linesMissing && !duplicateTender) recorded = null;
        return new PaymentVerdict(refundDue, recorded, needsRefund, status);
    }

    /// <summary>
    /// manifests.pallet_number is nullable, and a box with no number would silently
    /// vanish from the buyer's list. Log it rather than drop it quietly — the counts
    /// on <see cref="FulfillResult"/> stay authoritative either way.
    ///
    /// Two causes now reach here: a real box whose number was never set, and a
    /// hard-deleted one, which the work list's LEFT JOIN keeps (so its money stays
    /// in the refund) with a NULL pallet_number. Neither has a BOX # to report, and
    /// neither is allowed to throw — the alternative is an NRE that rolls back a
    /// payment we have already taken.
    /// </summary>
    private void AddPallet(List<int> into, int? palletNumber, Guid manifestId)
    {
        if (palletNumber.HasValue) into.Add(palletNumber.Value);
        else _log.LogWarning("Fulfill: manifest {ManifestId} has no pallet_number — omitted from the box list", manifestId);
    }

    /// <summary>
    /// SQL Server's two ways of saying "that key is already there": 2627 is a
    /// PRIMARY KEY or UNIQUE constraint violation, 2601 a unique-index one.
    /// Deliberately narrow. Widening this to SqlException, or to a deadlock
    /// (1205) or a timeout (-2), would swallow a refund that genuinely failed to
    /// record and report it to Square as handled. Only "the row you are inserting
    /// already exists" is a replay.
    /// </summary>
    internal static bool IsDuplicateKey(int sqlErrorNumber) => sqlErrorNumber is 2627 or 2601;

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
    ///
    /// BOTH statements run in ONE transaction, and that is not housekeeping. The
    /// INSERT is the dedupe and the UPDATE is the money, so a failure BETWEEN
    /// them — an Azure SQL transient failover is routine, not exotic — would
    /// leave the refund claimed and the total unmoved: every later delivery of
    /// that event sees the row, returns false and does nothing, needs_refund
    /// stays 1 forever, the cash really did go back to the customer, and NO code
    /// path can detect or heal it. Self-sealing corruption is strictly worse than
    /// the double-count the dedupe exists to prevent, so the claim and the money
    /// commit together or neither does, and true is returned only once the commit
    /// has succeeded.
    /// </summary>
    /// <returns>true if this refund was newly recorded; false on a replay.</returns>
    public async Task<bool> RecordRefundAsync(SqlConnection conn, string refundId, string paymentId, long amountCents)
    {
        using var tx = conn.BeginTransaction();
        int inserted;
        try
        {
            inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payment_refunds (square_refund_id, square_payment_id, amount_cents)
SELECT @rid, @pid, @amt
WHERE NOT EXISTS (SELECT 1 FROM dbo.payment_refunds WHERE square_refund_id = @rid)",
                new { rid = refundId, pid = paymentId, amt = amountCents }, transaction: tx);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex.Number))
        {
            // WHERE NOT EXISTS does not serialise under read-committed snapshot
            // isolation, which is the Azure SQL default — the same caveat this
            // file already makes at the availability re-check. Two simultaneous
            // deliveries of one refund both pass the NOT EXISTS and the loser
            // hits the primary key. That loser IS a replay — the winner is
            // committing the identical row — so it gets a replay's answer rather
            // than an uncaught SqlException, which would be a 500 and about eleven
            // Square retries over 24 hours on the one path that exists to give a
            // customer their money back.
            _log.LogInformation(
                "RecordRefund: refund {RefundId} lost the insert race (SQL error {Number}) — another delivery is recording it; answering as a replay",
                refundId, ex.Number);
            tx.Rollback();
            return false;
        }
        if (inserted == 0)
        {
            tx.Rollback();
            return false;
        }

        // refunded_cents + @amt, not @amt: every column below is the running
        // total after this refund, so two partials add up instead of the second
        // overwriting the first.
        //
        // amount_cents is NULLABLE on dbo.payments (db/square-payments.sql), and
        // an unknown total is not a zero total. COALESCE(amount_cents, 0) made the
        // status test "refunded >= 0", always true, so a $1 refund against an
        // unknown-amount payment declared the whole payment REFUNDED and cleared
        // the attention flag — reachable whenever Square omits amount_money on an
        // UNMATCHED payment. Unknown now means we decline to conclude anything:
        // PARTIAL_REFUNDED, and the flag stays up for a human. Same for what is
        // OWED — with both refund_due_cents and amount_cents NULL there is no
        // figure that could have been satisfied, so no arithmetic against a total
        // we do not know is allowed to clear the flag.
        var matched = await conn.ExecuteAsync(ApplyRefundSql,
            new { pid = paymentId, amt = amountCents }, transaction: tx);

        // `transaction: tx` is not optional here and not merely tidy. SqlClient
        // REFUSES to run a command with no transaction on a connection that has a
        // pending local one ("requires the command to have a transaction when the
        // connection ... is in a pending local transaction"), so omitting it does
        // not quietly leave this statement outside the transaction — it throws
        // InvalidOperationException on EVERY completed refund. That is not a
        // SqlException, so the duplicate-key filter above does not catch it; it
        // escapes the webhook as a 500 and Square retries about eleven times over
        // 24 hours while no refund is ever recorded at all. Caught in review: the
        // whole 203-test suite was green, because nothing executes this SQL.
        //
        // The claim and the money land together. Past this line the refund is
        // durably recorded AND accumulated, so a later delivery that returns false
        // is telling the truth.
        tx.Commit();

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
    /// The money half of a refund, and the ONLY copy of it. Both the webhook
    /// (<see cref="RecordRefundAsync"/>) and Reconcile's orphan replay
    /// (<see cref="ApplyRecordedRefundAsync"/>) run this exact statement, so the
    /// cumulative total, the REFUNDED/PARTIAL_REFUNDED rule and the
    /// clear-the-flag test cannot drift apart between the two paths.
    ///
    /// Note what this does NOT do: default the owed figure to zero. With both
    /// refund_due_cents and amount_cents NULL there is no total that could have
    /// been satisfied, so the flag stays up for a human rather than being cleared
    /// by arithmetic against a number we do not have. The long comment above the
    /// call site in RecordRefundAsync is the history of that.
    /// </summary>
    private const string ApplyRefundSql = @"
UPDATE dbo.payments SET
    refunded_cents = refunded_cents + @amt,
    status = CASE WHEN amount_cents IS NOT NULL AND refunded_cents + @amt >= amount_cents
                  THEN 'REFUNDED' ELSE 'PARTIAL_REFUNDED' END,
    needs_refund = CASE WHEN COALESCE(refund_due_cents, amount_cents) IS NOT NULL
                         AND refunded_cents + @amt >= COALESCE(refund_due_cents, amount_cents)
                  THEN 0 ELSE needs_refund END
WHERE square_payment_id = @pid";

    /// <summary>
    /// Reconcile only: apply refunds that are recorded in dbo.payment_refunds but
    /// were never added to the payment's running total — the refund that arrived
    /// before its payment row existed. <see cref="RecordRefundAsync"/> recorded
    /// the row (unconditionally, on purpose), the payments UPDATE matched nothing,
    /// and because the refund id is now claimed no later delivery can ever replay
    /// it. Without this method that row is evidence nobody acts on while our books
    /// and Square's disagree about money a customer already has back.
    ///
    /// IDEMPOTENCE, and why it is a property of the statement rather than of the
    /// caller. The webhook's dedupe is the INSERT: a second delivery inserts
    /// nothing and never reaches the money. A replay has no INSERT to dedupe on —
    /// the row is already there by definition — so the guard below is its
    /// substitute, and it is a compare-and-swap, not a pre-check the caller makes
    /// and then hopes is still true. refunded_cents may never exceed the total of
    /// the refunds we have actually recorded for that payment, which is the
    /// invariant this whole pass exists to restore: after the first apply the two
    /// are equal, so a second apply of the same amount fails the test, matches
    /// zero rows and moves no money. Two sweeps racing each other land the same
    /// way — the loser reads the winner's committed total under the UPDATE's own
    /// lock and backs off. A single UPDATE is atomic, so no transaction is opened
    /// here; there is nothing to tie together.
    ///
    /// The guard is deliberately NOT added to the webhook's path. There the row
    /// was inserted in the same transaction moments earlier, so the guard would
    /// always pass and could only ever misfire — and a misfire there would look
    /// like the UNATTRIBUTABLE-refund warning, which means something entirely
    /// different.
    ///
    /// Settled-only is honoured upstream, by construction: the webhook records a
    /// payment_refunds row ONLY for a COMPLETED refund with a positive amount, so
    /// a PENDING, FAILED or REJECTED refund has no row here to replay.
    ///
    /// <paramref name="ct"/> is not optional and has no default: this is called
    /// from a sweep that runs unattended on a schedule, and a host shutdown that
    /// cannot cancel the database half of it leaves the process waiting on SQL
    /// with nothing left to wait for. Cancelling mid-statement is safe here
    /// precisely because the statement is a single guarded UPDATE — it either
    /// committed or it did not, and the next sweep re-plans whatever did not.
    /// </summary>
    /// <returns>rows affected: 1 if the payment was repaired, 0 if it was already square.</returns>
    public Task<int> ApplyRecordedRefundAsync(SqlConnection conn, string paymentId, long amountCents, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(ApplyRefundSql + @"
  AND refunded_cents + @amt <= (SELECT COALESCE(SUM(amount_cents), 0)
                                FROM dbo.payment_refunds WHERE square_payment_id = @pid)",
            new { pid = paymentId, amt = amountCents }, cancellationToken: ct));

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
    /// How many times ONE call may ask Square to prove, out of its own mouth,
    /// that our credential is looking at the merchant these orders belong to.
    ///
    /// A failed proof is not a neutral outcome worth retrying a fourth time:
    /// "Square holds no such order" is itself the evidence that points at a wrong
    /// merchant, so three of them in a row have already answered the question.
    /// The cap is what keeps the extra traffic bounded when the sweep hands this
    /// method its whole 40-link budget at once — past it we stop asking, stamp
    /// nothing, and the next run (or the next staff action) starts over.
    /// </summary>
    private const int MerchantProofBudget = 3;

    /// <summary>
    /// Best-effort Square deletes for DB-canceled links. link_deleted_at is
    /// stamped ONLY when Square confirms AND this call has corroborated that the
    /// credential reaches the merchant holding these orders; anything else is
    /// left for Reconcile. Never throws — the sale is already committed.
    ///
    /// WHY A CONFIRMED DELETE IS NOT ENOUGH ON ITS OWN.
    /// <see cref="SquareService.DeletePaymentLinkAsync"/> returns true for two
    /// different answers: a 200 carrying cancelled_order_id (Square cancelled OUR
    /// link) and a 404 (Square has no such link). Those are the same word for
    /// opposite facts when the credential points at the wrong merchant — a
    /// sandbox token in production, a re-created application, a location change
    /// after a migration. None of that moves a cent of anybody's money: the
    /// buyers' links stay payable and their payments stay where they are, at OUR
    /// merchant. Only our ability to see them moves, and what we see instead is a
    /// 404 on every call. Read as confirmation, every one of those 404s stamps
    /// link_deleted_at — and a stamped row no longer matches
    /// SquareFunction.ReconcileRetireRecheckSql, the recovery queue's reserved
    /// draw, so it becomes unreachable by the one pass that could still discover
    /// a payment against it, permanently, including after somebody fixes the
    /// credential.
    ///
    /// SquareFunction.Reconcile already refuses to call this at all without a
    /// readable order somewhere in the same run (SquareAnswered). The gate below
    /// is that rule applied where it belongs — inside the method, so it also
    /// covers the five callers with no run-level evidence to offer: three in
    /// PalletsFunction, one in InvoiceBox, and FulfillOrderAsync itself. Their
    /// blast radius is smaller (each passes the links of one request's boxes, not
    /// every aged order) but it is not zero.
    ///
    /// WHAT COUNTS AS PROOF, and why it is a second call rather than a cleverer
    /// reading of the first. Square answering RetrieveOrder with an order object
    /// for an id WE minted can only happen at the merchant that holds it; a token
    /// on another merchant 404s. One such answer proves the credential for the
    /// whole call, so it is asked for lazily — at the first link that actually
    /// wants a stamp — and never asked again once given. The cheaper signal is a
    /// 200-with-cancelled_order_id on the delete itself, which is equally
    /// conclusive; it is unused here only because the bool hides it. If
    /// DeletePaymentLinkAsync ever distinguishes its two trues, a body-confirmed
    /// delete should count as proof and skip the probe.
    ///
    /// THE COST, paid on purpose: one extra GET per call that has a stamp to
    /// make, and — when the proof cannot be had — a link that really was deleted
    /// keeps its row in the retire queue for another round. That round re-deletes
    /// it, which is harmless, and Reconcile samples that queue at random so
    /// nothing is starved. The opposite mistake costs a customer their money.
    ///
    /// <paramref name="ct"/> reaches the Square calls and deliberately NOT the
    /// stamp below, which is the one database call in the sweep that should
    /// finish regardless: by the time it runs, Square has already deleted the
    /// link, and the row is the only record that it happened. Cancelling it
    /// buys a few milliseconds of shutdown and costs a wasted delete call on
    /// the next run. (Nothing breaks either way — a missing stamp re-deletes and
    /// re-corroborates on the next pass.)
    /// </summary>
    public async Task RetireLinksAsync(SqlConnection conn, IEnumerable<CanceledLink> links, CancellationToken ct)
    {
        bool merchantProven = false;
        int proofsAttempted = 0;
        int unprovenDeletes = 0;
        var noLinkId = new List<string>();

        foreach (var l in links)
        {
            try
            {
                // NO LINK ID, NO STAMP — the deliberate reversal of what this
                // branch used to do, which was to treat "nothing to delete" as
                // confirmation and stamp the row without asking Square anything.
                //
                // That reading was right about the link and wrong about the
                // column. There is indeed nothing here for us to cancel: we never
                // recorded the id, so no delete call is even expressible. But
                // link_deleted_at no longer means only "the link is gone" — it is
                // what takes a canceled row OUT of ReconcileRetireRecheckSql —
                // the recovery queue's own reserved draw, and the one pass that
                // still asks Square about the ORDER and heals it if it comes back
                // paid (SquareFunction.VerdictFor: CANCELED + paid => Heal).
                // A row with no link id is precisely the row where we cannot have
                // cancelled anything at Square, so whatever link the buyer was
                // given may still be payable. And these rows are real, not
                // theoretical: db/cart-checkout.sql backfills legacy orders
                // from manifests.checkout_link_id, which is nullable while
                // checkout_url is not, and the recovery path in FulfillOrderAsync
                // inserts kind='link' rows carrying no link id at all.
                //
                // So the honest answer is that we know nothing, and the honest
                // record of knowing nothing is to leave the column NULL and let
                // the sweep keep asking. The price is that these rows have no
                // terminal state: they sit in the retire queue being handed back
                // here every run (costing a loop iteration and no Square call)
                // and they count toward ReconcileRetireQueueWarnAt. Giving them
                // one needs evidence this method does not have — the sweep's own
                // "verified unpaid at Square this run", or a per-row attempt
                // counter, which is a schema change. Both belong outside this
                // file; the warning below is what keeps the pile visible until
                // one of them lands.
                if (l.LinkId == null)
                {
                    noLinkId.Add(l.OrderId);
                    continue;
                }
                if (!_square.Configured) continue;                       // leave for Reconcile
                if (!await _square.DeletePaymentLinkAsync(l.LinkId, ct)) continue;

                // Square says the link is dead. Whether that word is about OUR
                // merchant is a separate question, asked once per call.
                if (!merchantProven && proofsAttempted < MerchantProofBudget)
                {
                    proofsAttempted++;
                    merchantProven = await SquareHoldsOrderAsync(l.OrderId, ct);
                }
                if (!merchantProven)
                {
                    unprovenDeletes++;
                    continue;
                }

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

        // Square called these links dead and could not show us a single order of
        // ours to prove it was talking about our merchant. Either reading is
        // survivable — the links really are gone and we re-delete them next
        // round, or the credential is wrong and we have just avoided burying that
        // many orders — but only one of them is a configuration fault, and it is
        // the one worth saying out loud.
        if (unprovenDeletes > 0)
            _log.LogError(
                "RetireLinks: Square reported {N} link(s) deleted but answered nothing readable about any of the {Asked} order(s) we asked after, so the credential is not proven to be on our merchant and NOT ONE link_deleted_at was stamped. Those rows stay in the retire queue and in the backlog window, which is the safe direction. If this repeats, check SQUARE_ENVIRONMENT, the access token and the location id.",
                unprovenDeletes, proofsAttempted);

        if (noLinkId.Count > 0)
            _log.LogWarning(
                "RetireLinks: {N} canceled link order(s) carry no square_link_id, so there is nothing we can cancel at Square and nothing we can confirm: {Orders}. Left UNSTAMPED on purpose — the stamp would drop them out of the sweep's backlog window, and a link we never held the id for is exactly the one that could still be payable. They will be handed back here every run until something with better evidence retires them.",
                noLinkId.Count, string.Join(", ", noLinkId.Take(5)) + (noLinkId.Count > 5 ? ", …" : ""));
    }

    /// <summary>
    /// Did Square just show us an order we minted? That is the whole proof: a
    /// credential on another merchant cannot read our order ids back, so one
    /// order object is enough to say the 404s in the same call are about our
    /// links and not about our configuration.
    ///
    /// Never throws, and every failure reads as "not proven" — a 429, a rotated
    /// token, a 5xx and a host shutdown all leave us knowing nothing, which for
    /// the purposes of stamping is the same position a 404 leaves us in. The two
    /// are logged apart because only one of them is a misconfiguration.
    /// </summary>
    private async Task<bool> SquareHoldsOrderAsync(string orderId, CancellationToken ct)
    {
        try
        {
            using var order = await _square.RetrieveOrderAsync(orderId, ct);
            if (order != null) return true;
            _log.LogWarning(
                "RetireLinks: Square has no record of order {OrderId}, so a delete it answers for cannot be read as confirmation until some order of ours reads back",
                orderId);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "RetireLinks: could not ask Square about order {OrderId} — no deletion stamp is written on an unproven merchant this round",
                orderId);
            return false;
        }
    }
}
