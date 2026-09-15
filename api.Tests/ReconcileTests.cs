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
/// two pure functions and tested properly below — the verdict rule that decides
/// what happens to one open order (including the one that must never fire on an
/// invoice), and the arithmetic that decides how much of a payment's recorded
/// refunds never reached its total. Plus the refusal that must answer before any
/// connection is taken, which a closed SQL port and an exploding HTTP factory
/// prove between them.
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
}
