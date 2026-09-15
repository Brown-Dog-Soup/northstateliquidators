using System;
using NSL.Api.Functions;
using Xunit;

/// <summary>
/// The three money decisions behind the staff sales page, all pulled out of
/// their endpoints precisely so they can be executed rather than described:
/// what a click of Refund is allowed to send, WHAT ONE PAYMENT REPORTS OFF THE
/// ORDER IT HIT, and what that then contributes to revenue and margin.
///
/// The refund half is the one that matters most, and the reason it is worth
/// its length: the amount on the page's button is a render-time snapshot, so
/// the ceiling has to live on the server or it does not exist. Every test below
/// that names a stale amount is asserting that no caller — the page, a script,
/// a retry, a second browser — can hand back more than is owed at the moment
/// the money moves.
///
/// WHAT IS NOT HERE, and why.
///
///   * THE SQL. The payments list, the tender ranking that makes a second
///     payment report nothing, the LEFT JOIN that keeps an unmatched payment
///     out of floor revenue, the compare-and-swap that fills in a missing
///     amount, and the one that lowers the flag on an acknowledged row are all
///     statements issued against a live database, and this repository has no
///     database in test. A C# re-implementation of a GROUP BY would test the
///     re-implementation. What IS testable is the decision the SQL feeds —
///     ShareOfOrder — and that is tested here; what the SQL must do is supply
///     it with tender_seq and order_matched, which only staging can show.
///   * THE PAGE. Nothing in this repository runs staff/js/sales.js, so
///     rendering it is the only evidence for that half — which two of this
///     round's defects were found by, and neither was findable any other way.
///     A regex asserting a line of JavaScript exists would test the regex.
/// </summary>
public class SalesDashboardTests
{
    // ---------------------------------------------------------------- refunds

    /// <summary>
    /// The ordinary single-box case: nothing owed on record, nothing refunded
    /// yet, so the whole payment is the candidate and the button sends it.
    /// </summary>
    [Fact]
    public void With_nothing_owed_on_record_the_default_is_the_whole_payment()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: null, requestedCents: null,
                                             needsRefund: true, orderBoxLines: 0, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(4612, plan.Amount);
    }

    /// <summary>
    /// One box of three unavailable. refund_due_cents is that box's price plus
    /// its sales tax (spec §8.8, stored tax-inclusive), and that — NOT the
    /// payment — is what goes back. Sending the whole payment here would refund
    /// the two boxes the buyer is keeping.
    /// </summary>
    [Fact]
    public void A_partial_cart_refunds_what_is_owed_not_the_whole_payment()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: 1608, requestedCents: null,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(1608, plan.Amount);
    }

    /// <summary>
    /// REFUNDS ACCUMULATE, so the owed figure is a bar, not an instruction.
    /// With $6.00 of a $16.08 debt already back, a second click sends the
    /// REMAINING $10.08. Sending 1608 again would put $22.08 back on a $16.08
    /// debt — the endpoint's own clamp would not catch it, because an explicit
    /// amount is only capped against the payment remainder ($132.36 here).
    /// </summary>
    [Fact]
    public void A_second_click_sends_only_what_is_still_owed()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: null,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(1008, plan.Amount);
    }

    /// <summary>
    /// A SETTLED PARTIAL REFUND IS NEVER ESCALATED into a refund of the rest of
    /// the payment. The debt is square; the buyer is keeping the other boxes.
    /// The refusal has to be a sentence, because the person reading it is
    /// mid-task with a customer waiting.
    /// </summary>
    [Fact]
    public void A_fully_settled_debt_refuses_rather_than_refunding_the_rest()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 1608, refundDueCents: 1608, requestedCents: null,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Contains("Nothing outstanding", plan.Refusal);
        Assert.Equal(0, plan.Amount);
    }

    /// <summary>
    /// The guard that used to trip only on status == 'REFUNDED'. A payment
    /// refunded in full through several partials has status PARTIAL_REFUNDED,
    /// so the old test let the click through, Square capped it at a refundable
    /// balance of zero, SquareService threw, and staff got an opaque 500.
    /// </summary>
    [Fact]
    public void A_payment_already_fully_refunded_in_parts_refuses_in_words()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 4612, refundDueCents: null, requestedCents: null,
                                             needsRefund: false, orderBoxLines: 0, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Contains("Already refunded in full", plan.Refusal);
    }

    /// <summary>
    /// AN UNKNOWN TOTAL CONCLUDES NOTHING — the same ruling CheckoutFulfillment
    /// applies to the attention flag. Zero is not "no amount", and an explicit
    /// typed amount does not buy past it either: we still do not know the
    /// refundable balance we would be clamping against.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 500L)]
    [InlineData(0L, null)]
    [InlineData(-1L, 500L)]
    public void An_unknown_or_impossible_total_refuses_and_points_at_the_way_out(long? total, long? requested)
    {
        var plan = SquareFunction.PlanRefund(total, refundedCents: 0, refundDueCents: null, requestedCents: requested,
                                             needsRefund: true, orderBoxLines: 0, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        // The refusal must name the exit, or this is the row that stays flagged
        // forever after a staff member refunds it by hand in Square.
        Assert.Contains("Get amount from Square", plan.Refusal);
    }

    /// <summary>
    /// An explicit amount AT OR BELOW what is still owed is honoured as typed.
    /// Smaller is always safe, and refunding one box of two unavailable ones is
    /// a real thing to do.
    /// </summary>
    [Theory]
    [InlineData(500L)]
    [InlineData(1008L)]   // exactly what is owed
    public void A_typed_amount_within_the_debt_is_sent_as_typed(long typed)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: typed,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(typed, plan.Amount);
    }

    /// <summary>
    /// THE BUG THIS ROUND EXISTS FOR, and it is the click that actually happens.
    /// The Sales page renders the owed figure into the button and always sends
    /// it, so the owed-subtraction above used to run on a branch production never
    /// took: an explicit amount was clamped against the PAYMENT remainder
    /// ($132.36 here) and the owed figure was never consulted at all.
    ///
    /// The row renders owing $16.08 with nothing back, so the button carries
    /// 1608. A $6.00 refund then lands from the webhook, the sweep's orphan
    /// replay or another browser. The stale button is clicked. The old code sent
    /// all 1608 — $22.08 back on a $16.08 debt — because 1608 is far below the
    /// payment remainder it was checked against.
    ///
    /// It is REFUSED, not silently reduced: a clamp would send a different number
    /// from the one the person read, and the honest answer to "this row moved
    /// under you" is to say so. The refusal carries all three figures, because
    /// the person reading it is mid-task with a customer in front of them.
    /// </summary>
    [Fact]
    public void A_stale_button_amount_above_the_debt_is_refused_not_clamped()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: 1608,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        Assert.Contains("$16.08", plan.Refusal);   // what was asked for
        Assert.Contains("$10.08", plan.Refusal);   // what is actually still owed
        Assert.Contains("$6.00", plan.Refusal);    // what went back in between
    }

    /// <summary>
    /// The same ceiling for a caller that is not the page at all — a script, a
    /// retry, a second dashboard. There is no amount large enough to buy past
    /// what is owed, and in particular the payment remainder is NOT the bar.
    /// </summary>
    [Fact]
    public void No_caller_can_exceed_the_debt_by_asking_for_the_payment_remainder()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: 13236,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
    }

    /// <summary>
    /// The goodwill escape hatch is CLOSED, deliberately. A typed amount used to
    /// be allowed even with the debt settled, which is the same hole from the
    /// other end: an explicit number walking past the owed figure. A refund
    /// beyond what is owed is a person's decision and belongs in the Square
    /// Dashboard, where it is made once and on purpose; the page never offered
    /// one, because the only amount it sends is the owed figure it drew.
    /// </summary>
    [Fact]
    public void A_typed_amount_cannot_reopen_a_settled_debt()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 1608, refundDueCents: 1608, requestedCents: 1000,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        Assert.Contains("Nothing outstanding", plan.Refusal);
    }

    /// <summary>
    /// With no owed figure on record the whole remaining payment IS the debt, so
    /// a typed amount is bounded by the remainder — the old behaviour, unchanged,
    /// on the rows where the two bars coincide. This is the ordinary "refund this
    /// payment" row, and narrowing the ceiling must not have narrowed it.
    /// </summary>
    [Theory]
    [InlineData(500L, true, 500L)]
    [InlineData(4612L, true, 4612L)]
    [InlineData(99999L, false, 0L)]
    public void With_no_owed_figure_the_ceiling_is_still_the_payment(long typed, bool allowed, long expected)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: null, requestedCents: typed,
                                             needsRefund: true, orderBoxLines: 0, tenderSeq: 1, acknowledgedAt: null);
        Assert.Equal(allowed, plan.Allowed);
        Assert.Equal(expected, plan.Amount);
    }

    // ------------------------------------- the row nobody may put a figure on

    /// <summary>
    /// ITEM 4: the two halves used to disagree about one row. A flagged payment
    /// with refund_due_cents NULL, on an order we DO hold a copy of, is
    /// fulfilment declining at length to name the debt — boxes sold, but a box
    /// the buyer paid for has no line on our copy, so no figure computed from
    /// the lines is the whole bill. The page mirrors that with a disabled button
    /// whose tooltip says guessing would be guessing with the buyer's money. The
    /// server, on that same row, fell back to the payment total and refunded ALL
    /// of it — which on an order the buyer is keeping boxes from is the
    /// dangerous direction.
    ///
    /// Called with no amount and called with one both decline: the endpoint is
    /// not a way to price a debt fulfilment refused to price.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(5000L)]
    public void An_unpriceable_debt_declines_instead_of_refunding_the_whole_payment(long? requested)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: null,
                                             requestedCents: requested,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        // It must name the exit, or this is the row that stays flagged forever.
        Assert.Contains("Square Dashboard", plan.Refusal);
        Assert.Contains("Mark handled", plan.Refusal);
    }

    /// <summary>
    /// THE DEFECT THIS ROUND EXISTS FOR, and it is the sequence the page itself
    /// instructs. The old guard read status == 'PARTIAL_REFUND_FLAGGED'. Staff
    /// read the Square receipt, refund the right PARTIAL amount by hand, and
    /// that refund comes back as refund.updated — which rewrites the status to
    /// PARTIAL_REFUNDED. From that moment the old guard returned false, the row
    /// re-grew a live Refund button, and it offered THE REST OF THE PAYMENT:
    /// $126.36 of a $138.36 payment whose buyer is keeping the boxes that did
    /// arrive. Doing exactly as told armed the mistake.
    ///
    /// Nothing in the inputs below says PARTIAL_REFUND_FLAGGED, on purpose —
    /// the status is gone and is never asked for again. The flag is still up
    /// (refunded_cents has not reached the whole payment, which is the only bar
    /// a NULL owed figure leaves), the owed figure is still NULL, and the order
    /// copy still has its lines. All three outlive the refund.
    /// </summary>
    [Fact]
    public void A_partial_refund_cannot_wash_the_unpriceable_flag_off_by_moving_the_status()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 1200, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        Assert.Contains("Square Dashboard", plan.Refusal);
    }

    /// <summary>
    /// And the same row once a person has taken it. Acknowledging LOWERS the
    /// flag, so the test above stops applying at exactly that moment — the stamp
    /// is what carries the fact across. A later partial refund then rewrites the
    /// status again (PARTIAL_REFUNDED here) and changes nothing.
    ///
    /// The refusal must name who and when. This row reaches here only by having
    /// been marked handled, and a refusal that said merely "our copy is
    /// incomplete" would invite a second hand-refund of a debt already settled.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1200L)]   // a hand refund landed after the acknowledgement
    public void An_acknowledged_row_refuses_for_good_and_names_who_settled_it(long refunded)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: refunded, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1,
                                             acknowledgedAt: new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Utc),
                                             acknowledgedBy: "rob@northstateliquidators.com");
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        Assert.Contains("rob@northstateliquidators.com", plan.Refusal);
        Assert.Contains("15 Sep 2026", plan.Refusal);
    }

    /// <summary>
    /// A typed amount buys nothing past an acknowledgement either. The ceiling
    /// is the whole point: a caller that is not the page — a script, a retry, a
    /// second dashboard — gets the same answer.
    /// </summary>
    [Fact]
    public void A_typed_amount_cannot_reopen_an_acknowledged_row()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: null,
                                             requestedCents: 500,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1,
                                             acknowledgedAt: new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
    }

    /// <summary>
    /// acknowledged_by is NULLABLE — the signed-in identity arrives in a header
    /// and a missing one must never have failed the acknowledgement. The refusal
    /// then reports the absence AS an absence. Printing "handled by " and
    /// nothing, or inventing "staff", would answer the one question the column
    /// exists for with a blank or a guess.
    /// </summary>
    [Fact]
    public void An_acknowledgement_with_no_recorded_sign_in_says_so_rather_than_naming_nobody()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1,
                                             acknowledgedAt: new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
                                             acknowledgedBy: null);
        Assert.False(plan.Allowed);
        Assert.Contains("not recorded", plan.Refusal);
    }

    /// <summary>
    /// THE FLOW THE NARROW GUARD WAS PROTECTING, and the reason this was never
    /// widened to "any flagged row with a NULL owed figure". An UNMATCHED
    /// payment — money in, NOTHING sold, no order copy at all — carries exactly
    /// the same NULL owed figure and the same raised flag, and the whole payment
    /// really is owed. It is routinely handed back in instalments, and every one
    /// of those instalments must go through.
    ///
    /// orderBoxLines is what separates it, and separates it exactly:
    /// FulfillOrderAsync writes UNMATCHED on every path where it resolved ZERO
    /// order-box lines, and only reaches the declined-to-price verdict after it
    /// has at least one.
    /// </summary>
    [Theory]
    [InlineData(0L, 4612L)]      // in full
    [InlineData(500L, 4112L)]    // second instalment
    [InlineData(4000L, 612L)]    // last instalment
    public void An_unmatched_payment_still_refunds_in_full_or_in_instalments(long refunded, long expected)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: refunded, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: true, orderBoxLines: 0, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(expected, plan.Amount);
    }

    /// <summary>
    /// An ordinary settled web order — order lines on record, owed figure NULL
    /// because nothing is owed, flag DOWN — is not an unpriceable debt and must
    /// still be refundable in full. The flag is load-bearing in the test: a
    /// guard that fired on "order lines plus a NULL owed figure" alone would
    /// block every plain refund of every completed sale we have.
    /// </summary>
    [Fact]
    public void A_completed_matched_order_is_still_refundable_in_full()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: false, orderBoxLines: 3, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(13836, plan.Amount);
    }

    /// <summary>
    /// The predicate itself, as a truth table, because every row of it is a
    /// different production payment and three of them must NOT be caught.
    ///
    /// The status is absent from every case on purpose: it is a stage the row
    /// passes through, and the whole defect was reading a fact off one.
    /// </summary>
    [Theory]
    // owed NULL + flagged + we hold the order's lines + first tender on the
    // order = fulfilment declined to price it. Caught, whatever the status has
    // since become.
    [InlineData(null, true,  3, 1L, false, true)]
    [InlineData(null, true,  1, 1L, false, true)]
    // ... and once a person has taken it, the lowered flag stops mattering.
    [InlineData(null, false, 3, 1L, true,  true)]
    [InlineData(null, true,  0, 1L, true,  true)]   // stamp wins even with no lines
    // UNMATCHED: flagged, owed NULL, but no order copy. The whole payment is
    // genuinely the debt and instalments must go through.
    [InlineData(null, true,  0, 1L, false, false)]
    // A SECOND TENDER Square gave us no amount for. Same first three facts, and
    // its debt is the whole of a double charge, not something we declined to
    // price — fulfilment's duplicate ruling owns it.
    [InlineData(null, true,  3, 2L, false, false)]
    // A priced debt is priceable whatever else is true.
    [InlineData(1608L, true, 3, 1L, false, false)]
    // A settled, unflagged matched order. Not a debt at all.
    [InlineData(null, false, 3, 1L, false, false)]
    public void The_predicate_reads_facts_that_a_refund_cannot_move(
        long? due, bool flagged, int boxLines, long tenderSeq, bool acknowledged, bool expected)
        => Assert.Equal(expected, SquareFunction.DebtIsUnpriceable(
               due, flagged, boxLines, tenderSeq,
               acknowledged ? new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc) : null));

    /// <summary>
    /// The second legitimate flow, end to end. A Square split tender or a double
    /// charge arrives as a SECOND payment on an order every box of which we had
    /// already sold; fulfilment prices its debt at the payment itself, but when
    /// the webhook carried no amount_money there is no figure to record, so the
    /// row lands flagged with refund_due_cents NULL and order lines on the
    /// order — the first three facts of an unpriceable debt exactly.
    ///
    /// It is not one. Once "Get amount from Square" fills the amount in, the
    /// whole of it is refundable from this page, and a guard that answered "our
    /// copy of the order is incomplete" here would be wrong about the row AND
    /// would push a genuine double charge out into the Square Dashboard.
    /// </summary>
    [Fact]
    public void A_second_tender_with_no_recorded_amount_is_refundable_once_the_amount_is_known()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: null,
                                             requestedCents: null,
                                             needsRefund: true, orderBoxLines: 3, tenderSeq: 2,
                                             acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(4612, plan.Amount);
    }

    /// <summary>
    /// refund_due_cents can only ever be capped by the payment: a debt larger
    /// than the payment is not a debt we can settle through Square's refund API,
    /// which will not return more than was taken.
    /// </summary>
    [Fact]
    public void An_owed_figure_larger_than_the_payment_is_capped_at_the_payment()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: 99999, requestedCents: null,
                                             needsRefund: true, orderBoxLines: 1, tenderSeq: 1, acknowledgedAt: null);
        Assert.True(plan.Allowed);
        Assert.Equal(4612, plan.Amount);
    }


    // -------------------------------------------- a payment is not an order

    /// <summary>One order: three boxes at $43.00, $3.12 tax, $10.00 delivery, $28 cost.</summary>
    private static SquareFunction.PaymentShare Order(long tenderSeq, bool matched = true,
                                                     int boxes = 3, int named = 3)
        => SquareFunction.ShareOfOrder(orderMatched: matched, tenderSeq: tenderSeq,
                                       orderGoodsCents: 4300, orderTaxCents: 312, orderDeliveryCents: 1000,
                                       orderBoxCount: boxes, namedBoxCount: named, orderCost: 28.00m);

    /// <summary>
    /// The ordinary web sale: one payment, one order, and it reports the order.
    /// </summary>
    [Fact]
    public void The_first_tender_on_an_order_reports_the_order()
    {
        var s = Order(tenderSeq: 1);
        Assert.Equal(4300, s.GoodsCents);
        Assert.Equal(312, s.TaxCents);
        Assert.Equal(1000, s.DeliveryCents);
        Assert.Equal(3, s.BoxCount);
        Assert.Equal(28.00m, s.Cost);
        Assert.Null(s.Note);
    }

    /// <summary>
    /// ITEM 3, the regression. The sales query groups by PAYMENT and the goods,
    /// tax, delivery and cost all live on the ORDER, so two payments against one
    /// order each reported the whole order: Gross, the Website tile, the
    /// tax-and-delivery tile and margin every one of them overstated by a full
    /// order, and the table listed the same boxes twice. The previous query
    /// carried each payment's own amount, so its total at least equalled money
    /// actually taken — this was a step backwards, and it bit on exactly the
    /// rows the system raises an attention row for.
    ///
    /// A second tender sold NOTHING: the boxes left the building once, on the
    /// first payment. Zero is not a guess about whether the money is owed back —
    /// that question is genuinely open, which is why fulfilment flags rather than
    /// refunding — it is the one thing that is certain.
    /// </summary>
    [Theory]
    [InlineData(2L)]
    [InlineData(3L)]
    public void A_later_tender_on_the_same_order_reports_nothing(long seq)
    {
        var s = Order(tenderSeq: seq);
        Assert.Equal(0, s.GoodsCents);
        Assert.Equal(0, s.TaxCents);
        Assert.Equal(0, s.DeliveryCents);
        Assert.Equal(0, s.BoxCount);          // and so the boxes are not listed twice
        Assert.Null(s.Cost);                  // nor the cost counted twice against margin
        Assert.Contains("second payment", s.Note, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("split tender", s.Note);
    }

    /// <summary>
    /// Two tenders on one order, added up the way the tiles add them up. The
    /// order's figures are reported exactly once between them — which is the
    /// whole of "a payment is not an order" as arithmetic.
    /// </summary>
    [Fact]
    public void Two_tenders_on_one_order_report_the_order_once_between_them()
    {
        var first = Order(tenderSeq: 1);
        var second = Order(tenderSeq: 2);
        Assert.Equal(4300, first.GoodsCents + second.GoodsCents);
        Assert.Equal(312 + 1000, first.TaxCents + second.TaxCents + first.DeliveryCents + second.DeliveryCents);
        Assert.Equal(3, first.BoxCount + second.BoxCount);
    }

    /// <summary>
    /// ITEM 6. A payment with no order at all — fulfilment's UNMATCHED, money in
    /// and nothing sold. The old INNER join dropped it here, the floor branch
    /// caught it, and its whole TAX-INCLUSIVE amount was counted as counter
    /// revenue with no note, while the comment beside it claimed
    /// money-in-nothing-sold was handled. Every dbo.payments row is ours by
    /// construction — the webhook refuses to record a floor POS sale — so it is a
    /// web row with no goods and a sentence, not silent floor revenue.
    /// </summary>
    [Fact]
    public void A_payment_that_matched_no_order_reports_nothing_and_says_so()
    {
        var s = Order(tenderSeq: 1, matched: false);
        Assert.Equal(0, s.GoodsCents);
        Assert.Equal(0, s.TaxCents);
        Assert.Equal(0, s.DeliveryCents);
        Assert.Equal(0, s.BoxCount);
        Assert.Null(s.Cost);
        Assert.Contains("matched no order", s.Note);
    }

    /// <summary>
    /// Paid, nothing handed over — reconcile's paidNothingSold. Zero goods, but
    /// the tax and the delivery fee were really collected on a real order, so
    /// they stay in the held-money column where the refund beside them takes them
    /// back out. Distinguishing this from the two cases above is the point: all
    /// three have no goods and they are not the same row.
    /// </summary>
    [Fact]
    public void An_order_that_handed_nothing_over_keeps_its_tax_and_delivery()
    {
        var s = Order(tenderSeq: 1, boxes: 0, named: 0);
        Assert.Equal(0, s.GoodsCents);
        Assert.Equal(312, s.TaxCents);
        Assert.Equal(1000, s.DeliveryCents);
        Assert.Null(s.Cost);
        Assert.Contains("no box could be handed over", s.Note);
    }

    /// <summary>
    /// A deleted manifest shortens the box list silently — three boxes sold, two
    /// names left to print. The money and the cost still include all three
    /// (checkout_order_boxes outlives its manifest by design), so the list is the
    /// only thing that is short, and it says so rather than looking like a
    /// miscount.
    /// </summary>
    [Fact]
    public void Fewer_names_than_boxes_is_said_out_loud()
    {
        var s = Order(tenderSeq: 1, boxes: 3, named: 2);
        Assert.Equal(4300, s.GoodsCents);
        Assert.Equal(3, s.BoxCount);
        Assert.Contains("no longer have a box record", s.Note);
    }

    // ------------------------------------------------------- revenue / margin

    /// <summary>
    /// SPEC §8.6, the ruling this whole split exists for. A $43.00 box sold
    /// online with $3.12 of sales tax and a $10.00 delivery fee is a $46.12
    /// payment and $43.00 OF REVENUE. Tax is money held for NCDOR; the $10
    /// covers Norm's fuel. The Website tile reads $43.00, not $46.12 — getting
    /// the row right while the tile still summed the payment is the exact
    /// failure this catches, and it is why the row and the tile call this one
    /// function instead of doing their own arithmetic.
    /// </summary>
    [Fact]
    public void A_web_sale_reports_goods_only_and_holds_tax_and_delivery_apart()
    {
        var split = SquareFunction.SplitSale(isWeb: true, paymentAmountCents: 4612,
            goodsCents: 4300, taxCents: 312, deliveryCents: 1000, cost: null);
        Assert.Equal(4300, split.GoodsCents);
        Assert.Equal(1312, split.TaxAndDeliveryCents);
        Assert.NotEqual(4612, split.GoodsCents);
    }

    /// <summary>
    /// Margin is goods minus cost. Neither the tax nor the delivery fee may
    /// touch it: a $10 delivery fee is not $10 of profit, it is diesel.
    /// </summary>
    [Fact]
    public void Margin_is_goods_minus_cost_with_no_tax_or_delivery_in_it()
    {
        var split = SquareFunction.SplitSale(isWeb: true, paymentAmountCents: 4612,
            goodsCents: 4300, taxCents: 312, deliveryCents: 1000, cost: 18.50m);
        Assert.Equal(4300 - 1850, split.MarginCents);
    }

    /// <summary>
    /// No cost roll-up, no margin. A zero would read as "sold at cost" and a
    /// missing one must never be rendered as a number.
    /// </summary>
    [Fact]
    public void A_box_with_no_cost_roll_up_reports_no_margin_rather_than_zero()
    {
        var split = SquareFunction.SplitSale(isWeb: true, paymentAmountCents: 4612,
            goodsCents: 4300, taxCents: 312, deliveryCents: 1000, cost: null);
        Assert.Null(split.MarginCents);
    }

    /// <summary>
    /// An order that took the money and could hand nothing over (reconcile's
    /// paidNothingSold) contributes ZERO revenue — not its tax-inclusive total —
    /// while its tax and delivery still show as collected, because they were.
    /// </summary>
    [Fact]
    public void A_web_payment_that_sold_nothing_adds_no_revenue()
    {
        var split = SquareFunction.SplitSale(isWeb: true, paymentAmountCents: 4612,
            goodsCents: 0, taxCents: 312, deliveryCents: 1000, cost: null);
        Assert.Equal(0, split.GoodsCents);
        Assert.Equal(1312, split.TaxAndDeliveryCents);
        Assert.Null(split.MarginCents);
    }

    /// <summary>
    /// A floor sale is the honest gap and this pins it deliberately. A terminal
    /// payment has no order and no line data, so whatever tax it collected is
    /// inside the amount with no way to separate it: all of it counts as goods,
    /// and nothing is claimed as held. Changing that needs the Square order
    /// behind each terminal payment, not a different sum here — so if this test
    /// ever fails, the fix is upstream, not in SplitSale.
    /// </summary>
    [Fact]
    public void A_floor_sale_counts_the_whole_payment_and_claims_no_held_money()
    {
        var split = SquareFunction.SplitSale(isWeb: false, paymentAmountCents: 4612,
            goodsCents: 0, taxCents: 312, deliveryCents: 1000, cost: 18.50m);
        Assert.Equal(4612, split.GoodsCents);
        Assert.Equal(0, split.TaxAndDeliveryCents);
        // Margin stays unknown off-web: we have no box behind the payment, so
        // there is no cost to subtract even when one is passed.
        Assert.Null(split.MarginCents);
    }

    /// <summary>
    /// Rounding is at the cent, once, on the way in: $18.506 of cost is 1851
    /// cents and the margin is exact after that, so no fractional cents leak
    /// into a total Rob will compare against a bank deposit. (Deliberately not
    /// a midpoint — which way .5 goes is incidental, and pinning it here would
    /// be testing Math.Round rather than this.)
    /// </summary>
    [Fact]
    public void Cost_is_rounded_to_the_cent_before_it_touches_the_margin()
    {
        var split = SquareFunction.SplitSale(isWeb: true, paymentAmountCents: 4300,
            goodsCents: 4300, taxCents: 0, deliveryCents: 0, cost: 18.506m);
        Assert.Equal(4300 - 1851, split.MarginCents);
    }
}
