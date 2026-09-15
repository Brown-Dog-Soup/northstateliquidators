using NSL.Api.Services;
using Xunit;

/// <summary>
/// The refund arithmetic of a fulfilment, tested where it is actually decidable.
///
/// WHAT THIS COVERS AND WHAT IT DOES NOT, because the last round's test count went
/// up without covering the money change and that is worse than not testing it.
/// <see cref="CheckoutFulfillment.FulfillOrderAsync"/> needs a live SqlConnection
/// and there is no database here, so the loop, the locking and the UPDATEs are NOT
/// exercised by anything below. What is exercised is the pair of pure functions the
/// decision was pulled into: how much of an order's goods no surviving line accounts
/// for, and what that plus the line arithmetic writes on dbo.payments. Those are the
/// choices that move money. That the route calls them with the right arguments, and
/// that the row it then writes is the row SQL Server stores, is a staging-pass
/// question — see the report.
/// </summary>
public class FulfillMoneyDecisionTests
{
    // A round three-box order: three $100 boxes, 7.25% tax, $10 delivery taxed too.
    private const long Box = 10_000;
    private const long BoxTax = 725;
    private const long Subtotal = 3 * Box;              // 30000 goods
    private const long Total = Subtotal + 3 * BoxTax + 1_000 + 73;   // + tax + delivery + its tax

    // ---- UnaccountedGoodsCents -------------------------------------------------

    [Fact]
    public void Nothing_is_unaccounted_when_every_line_is_still_there()
        => Assert.Equal(0, CheckoutFulfillment.UnaccountedGoodsCents(Subtotal, new[] { Box, Box, Box }));

    [Fact]
    public void A_line_that_has_vanished_is_reported_at_its_own_value()
        => Assert.Equal(Box, CheckoutFulfillment.UnaccountedGoodsCents(Subtotal, new[] { Box, Box }));

    [Fact]
    public void Every_line_gone_reports_the_whole_goods_figure()
        => Assert.Equal(Subtotal, CheckoutFulfillment.UnaccountedGoodsCents(Subtotal, Array.Empty<long>()));

    /// <summary>
    /// The harmless direction, clamped. Lines summing to MORE than the recorded
    /// subtotal means nobody is owed anything, and a negative "shortfall" leaking
    /// into the caller's `> 0` test would be a flag raised by arithmetic rather
    /// than by a missing line.
    /// </summary>
    [Fact]
    public void Lines_worth_more_than_the_subtotal_owe_nobody_anything()
        => Assert.Equal(0, CheckoutFulfillment.UnaccountedGoodsCents(Subtotal, new[] { Box, Box, Box, Box }));

    /// <summary>
    /// subtotal_cents was added to an existing table with DEFAULT 0, so every row
    /// written before it reads zero. Those orders must not be reported as entirely
    /// unaccounted for — an attention flag on every historical order is an
    /// attention flag nobody reads.
    /// </summary>
    [Fact]
    public void A_pre_migration_order_with_no_recorded_subtotal_never_flags()
        => Assert.Equal(0, CheckoutFulfillment.UnaccountedGoodsCents(0, new[] { Box, Box, Box }));

    // ---- DecidePaymentOutcome --------------------------------------------------

    private static CheckoutFulfillment.PaymentVerdict Decide(
        bool duplicateTender = false, int sold = 0, long refundDueFromLines = 0,
        long orderTotalCents = Total, long unaccountedGoodsCents = 0, long? paymentAmountCents = Total)
        => CheckoutFulfillment.DecidePaymentOutcome(
            duplicateTender, sold, refundDueFromLines, orderTotalCents, unaccountedGoodsCents, paymentAmountCents);

    [Fact]
    public void Everything_sold_and_every_line_present_is_a_completed_payment()
    {
        var v = Decide(sold: 3);
        Assert.Equal(0, v.RefundDueCents);
        Assert.Null(v.RecordedDueCents);
        Assert.False(v.NeedsRefund);
        Assert.Equal("COMPLETED", v.Status);
    }

    /// <summary>
    /// THE SCENARIO. Three boxes, one of them deleted before the buyer paid. With
    /// the delete route retaining its order line as 'unavailable', fulfilment sells
    /// two, marks the third unavailable and arrives here with that box's price and
    /// tax owed. Every line is still present, so the backstop is silent and the
    /// figure recorded is the line arithmetic's — which is the money the buyer is
    /// owed, to the cent.
    /// </summary>
    [Fact]
    public void Two_boxes_sell_and_the_deleted_third_is_owed_back_with_its_tax()
    {
        var v = Decide(sold: 2, refundDueFromLines: Box + BoxTax);
        Assert.Equal(Box + BoxTax, v.RefundDueCents);
        Assert.Equal(Box + BoxTax, v.RecordedDueCents);
        Assert.True(v.NeedsRefund);
        Assert.Equal("PARTIAL_REFUND_FLAGGED", v.Status);
    }

    /// <summary>
    /// Nothing sold: there is no delivery to make either, so the whole order total
    /// goes back rather than just the goods and their tax.
    /// </summary>
    [Fact]
    public void Nothing_sold_owes_the_whole_order_total_not_just_the_boxes()
    {
        var v = Decide(sold: 0, refundDueFromLines: Box + BoxTax);
        Assert.Equal(Total, v.RefundDueCents);
        Assert.Equal(Total, v.RecordedDueCents);
        Assert.Equal("REFUND_FLAGGED", v.Status);
    }

    /// <summary>An order with no recorded total cannot be grossed up to it.</summary>
    [Fact]
    public void Nothing_sold_on_an_order_with_no_total_keeps_the_line_figure()
        => Assert.Equal(Box + BoxTax, Decide(sold: 0, refundDueFromLines: Box + BoxTax, orderTotalCents: 0).RefundDueCents);

    [Fact]
    public void A_second_tender_on_a_fulfilled_order_flags_the_whole_of_that_payment()
    {
        var v = Decide(duplicateTender: true, sold: 3, paymentAmountCents: Total);
        Assert.Equal(Total, v.RefundDueCents);
        Assert.Equal(Total, v.RecordedDueCents);
        Assert.True(v.NeedsRefund);
        Assert.Equal("REFUND_FLAGGED", v.Status);
    }

    [Fact]
    public void A_second_tender_of_unknown_amount_still_flags()
    {
        var v = Decide(duplicateTender: true, sold: 3, paymentAmountCents: null);
        Assert.True(v.NeedsRefund);
        Assert.Equal("REFUND_FLAGGED", v.Status);
    }

    // ---- the backstop, which is the point of all of this ------------------------

    /// <summary>
    /// The shape no line can defend. Every box on the order sold, so the line
    /// arithmetic owes nothing and this would have recorded COMPLETED and walked
    /// away — but the order's goods figure is $100 larger than its remaining
    /// lines, which means the buyer paid for a box whose order line is gone
    /// entirely. Flagged.
    /// </summary>
    [Fact]
    public void Goods_the_lines_cannot_account_for_raise_the_flag_on_their_own()
    {
        var v = Decide(sold: 2, refundDueFromLines: 0, unaccountedGoodsCents: Box);
        Assert.True(v.NeedsRefund);
        Assert.NotEqual("COMPLETED", v.Status);
    }

    /// <summary>
    /// And it does NOT invent what is owed. The shortfall is a goods figure that
    /// can overstate (see UnaccountedGoodsCents), so it never becomes a debt: the
    /// flag goes up, the amount stays unstated, and a human reads the figure off
    /// the log and the Square receipt.
    /// </summary>
    [Fact]
    public void The_backstop_never_writes_an_owed_figure_of_its_own()
        => Assert.Null(Decide(sold: 2, refundDueFromLines: 0, unaccountedGoodsCents: Box).RecordedDueCents);

    /// <summary>
    /// The subtle one, and the reason the backstop CLEARS the recorded figure
    /// instead of leaving the smaller line-derived one in place. needs_refund
    /// clears by itself once refunded_cents reaches refund_due_cents. A debt we
    /// already know is incomplete — one box's worth of lines is missing — would
    /// therefore be satisfiable by a partial refund, switching the flag off with
    /// money still owed. NULL makes the whole payment the bar instead.
    /// </summary>
    [Fact]
    public void A_known_incomplete_debt_is_not_recorded_as_if_it_were_the_whole_debt()
    {
        var v = Decide(sold: 1, refundDueFromLines: Box + BoxTax, unaccountedGoodsCents: Box);
        Assert.Equal(Box + BoxTax, v.RefundDueCents);       // still reported and logged
        Assert.Null(v.RecordedDueCents);                     // but not offered as the bill
        Assert.True(v.NeedsRefund);
    }

    /// <summary>
    /// A duplicate tender's figure is the payment itself, not a sum over lines, so
    /// missing lines cannot make it incomplete and it keeps its amount.
    /// </summary>
    [Fact]
    public void A_second_tender_keeps_its_figure_even_with_lines_missing()
        => Assert.Equal(Total, Decide(duplicateTender: true, sold: 3, unaccountedGoodsCents: Box).RecordedDueCents);

    /// <summary>
    /// The safety property stated directly: whatever the backstop reads, it never
    /// makes us owe MORE than the line arithmetic alone concluded. An automatic
    /// refund of money nobody owes is the outcome this whole design is avoiding.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(Box)]
    [InlineData(Subtotal * 10)]
    public void No_shortfall_can_inflate_what_we_record_as_owed(long unaccounted)
    {
        var baseline = Decide(sold: 1, refundDueFromLines: Box + BoxTax);
        var withShortfall = Decide(sold: 1, refundDueFromLines: Box + BoxTax, unaccountedGoodsCents: unaccounted);
        Assert.True(withShortfall.RecordedDueCents is null ||
                    withShortfall.RecordedDueCents <= baseline.RecordedDueCents);
        Assert.True(withShortfall.RefundDueCents <= baseline.RefundDueCents);
    }
}
