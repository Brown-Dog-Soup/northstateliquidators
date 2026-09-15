using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// The reconciliation sweep (spec §4).
///
/// WHAT IS NOT HERE, and why. Three of the four passes are SQL: the set-based
/// cancel is a single UPDATE … OUTPUT whose whole meaning lives in its WHERE
/// clause, the pending-link and open-order reads are SELECTs, and the orphaned
/// refund is applied by a guarded UPDATE that SQL Server evaluates against the
/// row's current values. There is no seam that can stand in for a database
/// without re-implementing those predicates in C#, and a test of a
/// re-implementation proves only that the re-implementation agrees with itself.
/// There is no SQL Server in this environment, so those are left for the staging
/// pass and named here rather than faked.
///
/// What IS provable without a database or a Square account is pulled out into
/// pure functions and tested properly below — the verdict rule that decides what
/// happens to one open order (including the one that must never fire on an
/// invoice), the predicate that decides whether an order may be CLOSED at all,
/// and the arithmetic that decides how much of a payment's recorded refunds
/// never reached its total. Plus the refusal that must answer before any
/// connection is taken, which a closed SQL port and an exploding HTTP factory
/// prove between them, and the two SQL texts whose WHERE clauses carry the
/// money rule, checked as text in the manner of SchemaContractTests.
///
/// STILL NOT COVERED, and worth being plain about rather than implying
/// otherwise:
///  * THE PASS ORDER ITSELF. That the Square-asking pass runs BEFORE the closing
///    pass is control flow inside one method with a live SqlConnection in it;
///    there is no seam to observe it from. What is defended instead is the thing
///    the order exists to guarantee — the closing SQL cannot name an order that
///    is not in the verified list, and the verified list cannot contain an order
///    nobody asked Square about.
///  * EXCEPTION CONTAINMENT. That one order's 429 no longer aborts the sweep
///    needs a Square that answers 429 and a database behind it; the sweep opens
///    its connection before the first Square call, so the failure cannot even be
///    staged here. Staging pass.
///  * The window and delete-queue SAMPLING, the refund-replay cap, and the
///    closed_at/link_deleted_at COALESCE — all SQL Server behaviour.
/// </summary>
public class ReconcileTests
{
    // Closed port, 1s timeout: any path that reaches SQL fails loudly rather than
    // hanging, and any path that must NOT reach SQL is proved by not failing.
    private const string DeadSql =
        "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True";

    private sealed class ExplodingHttpClientFactory : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("no Square call was expected on this path");
        }
    }

    private static SquareFunction Square(IHttpClientFactory http, bool squareConfigured)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = squareConfigured ? "test-token" : null,
            ["SQUARE_SANDBOX_LOCATION_ID"] = squareConfigured ? "LOC1" : null,
            ["SqlConnectionString"] = DeadSql,
        }).Build();
        var square = new SquareService(http, cfg, NullLogger<SquareService>.Instance);
        return new SquareFunction(new SqlService(cfg, NullLogger<SqlService>.Instance), square,
            new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance),
            NullLogger<SquareFunction>.Instance);
    }

    // ---- The refusal, before any connection --------------------------------

    /// <summary>
    /// Square off means the sweep cannot do its job, and must say so instead of
    /// opening a connection and half-doing it: pass 1 would cancel links that
    /// pass 2 could then never delete.
    /// </summary>
    [Fact]
    public async Task A_sweep_with_square_off_is_a_503_before_any_sql_or_square()
    {
        var http = new ExplodingHttpClientFactory();
        var r = Assert.IsType<ObjectResult>(await Square(http, squareConfigured: false)
            .Reconcile(new DefaultHttpContext().Request, CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(0, http.Calls);
    }

    // ---- VerdictFor: what to do with one open order -------------------------

    /// <summary>Paid and not canceled is a missed webhook, whatever kind of order it is.</summary>
    [Theory]
    [InlineData("OPEN", "link")]
    [InlineData("COMPLETED", "link")]
    [InlineData("OPEN", "invoice")]
    [InlineData(null, "link")]      // Square omitted state — paid is still paid
    public void A_paid_order_is_healed(string? state, string kind)
        => Assert.Equal(SquareFunction.ReconcileVerdict.Heal,
            SquareFunction.VerdictFor(state, paid: true, kind));

    /// <summary>A cart link Square has canceled is closed in our books too.</summary>
    [Fact]
    public void A_canceled_link_is_closed()
        => Assert.Equal(SquareFunction.ReconcileVerdict.CancelLink,
            SquareFunction.VerdictFor("CANCELED", paid: false, "link"));

    /// <summary>
    /// THE RULE THIS SWEEP MUST NOT BREAK. An invoice order is canceled by
    /// explicit staff action (CancelBoxInvoice) and never by a sweep. Closing one
    /// here would strand a real customer holding an invoice we emailed them, and
    /// leave the box drafted with nothing pointing at it — so even Square's own
    /// CANCELED leaves an invoice order open and visible instead.
    /// </summary>
    [Fact]
    public void A_canceled_invoice_order_is_never_canceled_by_the_sweep()
        => Assert.Equal(SquareFunction.ReconcileVerdict.StillOpen,
            SquareFunction.VerdictFor("CANCELED", paid: false, "invoice"));

    /// <summary>
    /// And not even a PAID one: CANCELED is checked first, so a canceled invoice
    /// order is reported open for a human rather than swept either way.
    /// </summary>
    [Fact]
    public void A_canceled_invoice_order_is_not_swept_even_when_paid()
        => Assert.Equal(SquareFunction.ReconcileVerdict.StillOpen,
            SquareFunction.VerdictFor("CANCELED", paid: true, "invoice"));

    /// <summary>An unpaid, uncanceled order is the ordinary case: leave it alone.</summary>
    [Theory]
    [InlineData("OPEN", "link")]
    [InlineData("OPEN", "invoice")]
    [InlineData("DRAFT", "invoice")]
    [InlineData(null, null)]
    public void An_unpaid_open_order_is_left_alone(string? state, string? kind)
        => Assert.Equal(SquareFunction.ReconcileVerdict.StillOpen,
            SquareFunction.VerdictFor(state, paid: false, kind));

    // ---- PlanRefundReplay: how much of a refund never landed ----------------

    private static SquareFunction.RecordedRefund Row(string pid, string rid, long amt, long recorded, long applied)
        => new(pid, rid, amt, recorded, applied);

    /// <summary>
    /// IDEMPOTENCE, the property the whole pass turns on. Everything recorded has
    /// already been applied, so a second sweep plans nothing — no payment is
    /// touched and the same money cannot go back to the books twice. (The
    /// database enforces the same rule independently, in the guard on
    /// ApplyRecordedRefundAsync's UPDATE, so two sweeps racing land here too.)
    /// </summary>
    [Fact]
    public void A_payment_whose_refunds_all_landed_is_not_replayed()
        => Assert.Empty(SquareFunction.PlanRefundReplay(new[] { Row("PAY1", "REF1", 500, recorded: 500, applied: 500) }));

    /// <summary>Nothing recorded, nothing to do.</summary>
    [Fact]
    public void No_refunds_at_all_plan_nothing()
        => Assert.Empty(SquareFunction.PlanRefundReplay(Array.Empty<SquareFunction.RecordedRefund>()));

    /// <summary>
    /// The case this pass exists for: the refund arrived before the payment row
    /// did, so it is recorded and has never moved the total. Apply all of it.
    /// </summary>
    [Fact]
    public void An_orphaned_refund_is_planned_in_full()
    {
        var plan = Assert.Single(SquareFunction.PlanRefundReplay(
            new[] { Row("PAY1", "REF1", 500, recorded: 500, applied: 0) }));
        Assert.Equal("PAY1", plan.PaymentId);
        Assert.Equal(500, plan.AmountCents);
        Assert.Equal(1, plan.RefundCount);
    }

    /// <summary>
    /// The one that would double-refund the books if the amount came from the
    /// rows instead of the totals: two refunds recorded, only one of them ever
    /// applied. The shortfall is 100c — NOT the 150c the rows add up to.
    /// </summary>
    [Fact]
    public void Only_the_shortfall_is_applied_when_some_refunds_already_landed()
    {
        var plan = Assert.Single(SquareFunction.PlanRefundReplay(new[]
        {
            Row("PAY1", "REF-OLD", 100, recorded: 150, applied: 50),
            Row("PAY1", "REF-NEW", 50,  recorded: 150, applied: 50),
        }));
        Assert.Equal(100, plan.AmountCents);
        Assert.Equal(1, plan.RefundCount);   // the oldest row accounts for it exactly
    }

    /// <summary>Two orphans against one payment: one repair, both counted.</summary>
    [Fact]
    public void Two_orphans_on_one_payment_are_one_repair()
    {
        var plan = Assert.Single(SquareFunction.PlanRefundReplay(new[]
        {
            Row("PAY1", "REF1", 300, recorded: 800, applied: 0),
            Row("PAY1", "REF2", 500, recorded: 800, applied: 0),
        }));
        Assert.Equal(800, plan.AmountCents);
        Assert.Equal(2, plan.RefundCount);
    }

    /// <summary>Payments are repaired independently, and a square one in the middle is skipped.</summary>
    [Fact]
    public void Each_payment_is_planned_separately()
    {
        var plans = SquareFunction.PlanRefundReplay(new[]
        {
            Row("PAY1", "REF1", 100, recorded: 100, applied: 0),
            Row("PAY2", "REF2", 250, recorded: 250, applied: 250),   // already square
            Row("PAY3", "REF3", 700, recorded: 700, applied: 200),
        });
        Assert.Equal(2, plans.Count);
        Assert.Equal(new[] { "PAY1", "PAY3" }, plans.Select(p => p.PaymentId).ToArray());
        Assert.Equal(new long[] { 100, 500 }, plans.Select(p => p.AmountCents).ToArray());
    }

    /// <summary>
    /// A total that already exceeds what we have recorded is not something to
    /// "fix" by adding more: the shortfall is negative, so nothing is planned and
    /// the discrepancy is left standing for a human rather than papered over.
    /// </summary>
    [Fact]
    public void An_over_applied_payment_is_never_topped_up()
        => Assert.Empty(SquareFunction.PlanRefundReplay(
            new[] { Row("PAY1", "REF1", 100, recorded: 100, applied: 400) }));

    // ---- VerifiedUnpaid: what the sweep is allowed to close ----------------
    //
    // THE DEFECT THESE EXIST FOR. The age rule closed orders on database state
    // alone, and ran before the pass that asks Square, which only ever looked at
    // orders still open. A buyer who paid on day six with a missed webhook had
    // their order closed on day seven and became permanently invisible: charged,
    // nothing sold, no flag, and the sweep's own counters reporting all clear.
    // Closing is now gated on this predicate.

    /// <summary>
    /// The one that must never come back. Square answered and the order is PAID,
    /// so no rule may close it — not the age rule, not the price-drift rule, not
    /// anything. Healing is the only thing that may happen to a paid order.
    /// </summary>
    [Fact]
    public void A_paid_order_is_never_closable()
        => Assert.False(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Order, paid: true));

    /// <summary>
    /// The second half of the same defect. A rate limit, a rotated token, a 5xx
    /// or a body we could not parse tells us NOTHING about whether the buyer
    /// paid — and "we could not ask" must never be read as "nobody paid". The
    /// order stays open and is asked again next run, which costs one more run of
    /// a link staying payable and saves a charge from being buried.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_order_square_could_not_be_asked_about_is_never_closable(bool paid)
        => Assert.False(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Unreachable, paid));

    /// <summary>Square answered, there is no payment on it: this is the case the age rule exists for.</summary>
    [Fact]
    public void An_unpaid_order_square_answered_for_is_closable()
        => Assert.True(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Order, paid: false));

    /// <summary>
    /// A 404 counts as established: Square holds no such order, so no tender can
    /// exist against it. Without this an order Square has forgotten could never
    /// be retired and would occupy a slot in every future window forever.
    /// </summary>
    [Fact]
    public void An_order_square_has_no_record_of_is_closable()
        => Assert.True(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.NotFound, paid: false));

    /// <summary>
    /// The list pass 2's UPDATE is restricted to, over a realistic mixed run:
    /// only the two orders this run actually established are unpaid survive. The
    /// paid one and the one Square rate-limited us on are both left open.
    /// </summary>
    [Fact]
    public void Only_orders_this_run_verified_reach_the_closing_rule()
    {
        var ids = SquareFunction.ClosableOrderIds(new[]
        {
            new SquareFunction.OrderCheck("PAID",     SquareFunction.SquareReply.Order, true),
            new SquareFunction.OrderCheck("UNPAID",   SquareFunction.SquareReply.Order, false),
            new SquareFunction.OrderCheck("RATE-429", SquareFunction.SquareReply.Unreachable, false),
            new SquareFunction.OrderCheck("GONE",     SquareFunction.SquareReply.NotFound, false),
        });
        Assert.Equal(new[] { "UNPAID", "GONE" }, ids);
    }

    /// <summary>Nothing asked, nothing closed — a sweep whose every Square call failed closes no order at all.</summary>
    [Fact]
    public void A_sweep_that_could_not_reach_square_closes_nothing()
        => Assert.Empty(SquareFunction.ClosableOrderIds(new[]
        {
            new SquareFunction.OrderCheck("A", SquareFunction.SquareReply.Unreachable, false),
            new SquareFunction.OrderCheck("B", SquareFunction.SquareReply.Unreachable, false),
        }));

    // ---- The SQL contracts, as text ----------------------------------------
    //
    // WHAT THESE PROVE AND DO NOT PROVE, in the spirit of SchemaContractTests.
    // They compare strings. They cannot execute a statement, so they cannot tell
    // you the WHERE clause means what it reads as. What they catch is exactly the
    // regression this fix round exists to prevent: someone restoring the
    // unconditional close, or narrowing the window back to orders that are still
    // open. A reverter who kept `IN @ids` and bound a different list to it would
    // slip past these — the C# predicate tests above are the other half.

    /// <summary>
    /// THE GATE. Pass 2 may only close orders this run verified at Square. Strip
    /// the id filter and the day-seven rule can once again bury a day-six
    /// payment where no pass will ever find it.
    /// </summary>
    [Fact]
    public void The_closing_rule_is_restricted_to_orders_verified_at_square()
        => Assert.Contains("square_order_id IN @ids", SquareFunction.ReconcileRuleCancelSql);

    /// <summary>And still only ever touches an open cart LINK — never an invoice, never a paid row.</summary>
    [Fact]
    public void The_closing_rule_still_only_touches_open_links()
    {
        Assert.Contains("o.status = 'open'", SquareFunction.ReconcileRuleCancelSql);
        Assert.Contains("o.kind = 'link'", SquareFunction.ReconcileRuleCancelSql);
    }

    /// <summary>
    /// The recovery route. A link whose order was paid is exactly the link Square
    /// refuses to cancel, so an unconfirmed deletion is where a wrongly closed
    /// paid order surfaces. The backlog window must keep reaching those rows —
    /// it is what makes a closed order reachable by a pass that can still
    /// discover payment, and what lets that row leave the delete queue instead of
    /// blocking every link behind it.
    /// </summary>
    [Fact]
    public void The_backlog_window_reaches_closed_links_whose_delete_was_never_confirmed()
    {
        Assert.Contains("status = 'canceled'", SquareFunction.ReconcileBacklogSql);
        Assert.Contains("link_deleted_at IS NULL", SquareFunction.ReconcileBacklogSql);
    }

    /// <summary>
    /// And the aged open orders a newest-first window can never reach — the
    /// invoice that drifts past the cap and would otherwise never heal again.
    /// </summary>
    [Fact]
    public void The_backlog_window_reaches_aged_open_orders()
        => Assert.Contains("created_at < DATEADD(DAY, -@ageDays", SquareFunction.ReconcileBacklogSql);

    /// <summary>
    /// The budget split, which is the anti-starvation guarantee expressed as a
    /// number: the backlog half may never be zero (that is a newest-first window
    /// again, with the far end of the list locked out forever), and the two
    /// halves may not together overspend the per-pass Square budget.
    /// </summary>
    [Fact]
    public void Every_sweep_spends_part_of_its_budget_on_the_backlog()
    {
        Assert.True(SquareFunction.ReconcileBacklogWindow > 0, "the backlog half of the window may never be zero");
        Assert.True(SquareFunction.ReconcileFreshWindow > 0, "the newest-first half of the window may never be zero");
        Assert.Equal(SquareFunction.ReconcileMaxSquareCalls,
            SquareFunction.ReconcileFreshWindow + SquareFunction.ReconcileBacklogWindow);
    }
}
