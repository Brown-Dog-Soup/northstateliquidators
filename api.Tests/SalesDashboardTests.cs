using NSL.Api.Functions;
using Xunit;

/// <summary>
/// The two money decisions behind the staff sales page, both pulled out of
/// their endpoints precisely so they can be executed rather than described:
/// what a click of Refund is allowed to send, and what a payment contributes to
/// revenue and margin.
///
/// WHAT IS NOT HERE, and why. The rest of this task is SQL issued against a
/// live database (the payments list, the goods-per-payment roll-up, the
/// compare-and-swap that fills in a missing amount) and DOM built by
/// staff/js/sales.js, and this repository has no database in test and no
/// JavaScript test runner. A C# re-implementation of a GROUP BY, or a regex
/// asserting that a line of JavaScript exists, would only test the
/// re-implementation or the regex — it would pass against a broken page. Those
/// are covered by the staging pass and by rendering the page, not by a test
/// that cannot fail for the right reason.
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
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: null, requestedCents: null);
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
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 0, refundDueCents: 1608, requestedCents: null);
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
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: null);
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
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 1608, refundDueCents: 1608, requestedCents: null);
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
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 4612, refundDueCents: null, requestedCents: null);
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
        var plan = SquareFunction.PlanRefund(total, refundedCents: 0, refundDueCents: null, requestedCents: requested);
        Assert.False(plan.Allowed);
        Assert.Equal(0, plan.Amount);
        // The refusal must name the exit, or this is the row that stays flagged
        // forever after a staff member refunds it by hand in Square.
        Assert.Contains("Get amount from Square", plan.Refusal);
    }

    /// <summary>
    /// A staff-typed amount is honoured, and clamped to what is left on the
    /// payment — never to the owed figure, because typing a different number is
    /// how a goodwill refund is made.
    /// </summary>
    [Theory]
    [InlineData(500L, 500L)]       // under the remainder: sent as typed
    [InlineData(99999L, 13236L)]   // over the remainder: clamped to it
    public void A_typed_amount_is_clamped_to_the_payment_remainder(long typed, long expected)
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 600, refundDueCents: 1608, requestedCents: typed);
        Assert.True(plan.Allowed);
        Assert.Equal(expected, plan.Amount);
    }

    /// <summary>
    /// A typed amount is also the escape hatch from "nothing outstanding": the
    /// debt is settled, but a goodwill refund is still a thing a person may
    /// decide to do, and the endpoint must not refuse it on the row's behalf.
    /// </summary>
    [Fact]
    public void A_typed_amount_is_allowed_even_when_nothing_is_outstanding()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 13836, refundedCents: 1608, refundDueCents: 1608, requestedCents: 1000);
        Assert.True(plan.Allowed);
        Assert.Equal(1000, plan.Amount);
    }

    /// <summary>
    /// refund_due_cents can only ever be capped by the payment: a debt larger
    /// than the payment is not a debt we can settle through Square's refund API,
    /// which will not return more than was taken.
    /// </summary>
    [Fact]
    public void An_owed_figure_larger_than_the_payment_is_capped_at_the_payment()
    {
        var plan = SquareFunction.PlanRefund(totalCents: 4612, refundedCents: 0, refundDueCents: 99999, requestedCents: null);
        Assert.True(plan.Allowed);
        Assert.Equal(4612, plan.Amount);
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
