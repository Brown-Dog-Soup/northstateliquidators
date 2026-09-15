using System.Text.Json;
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
/// invoice, and the paid-and-canceled contradiction that must never be stamped
/// dead), the predicate that decides whether an order may be CLOSED at all
/// (including the corroboration a 404 needs before it counts as evidence about
/// our own merchant), the reading of one order object's payment signal, and the
/// arithmetic that decides how much of a payment's recorded refunds never reached
/// its total. Plus the refusal that must answer before any connection is taken,
/// which a closed SQL port and an exploding HTTP factory prove between them, and
/// the two SQL texts whose WHERE clauses carry the money rule, checked as text in
/// the manner of SchemaContractTests.
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
///  * WHAT AN UNCORROBORATED RUN DOES TO link_deleted_at. The decision itself is
///    no longer prose — LinksToRetire is a pure function and is tested below with
///    BOTH sources handed to it full — but that RetireLinksAsync is then called
///    with its result, and that Square is never rung, is control flow around a
///    live connection. Likewise the wrong-merchant ERROR line: the condition is
///    tested, the logging is not.
///  * THE RESERVED-DRAW CALL SITE. That the recovery queue is read first with its
///    own cap and the open orders get OpenBacklogCap of what remains is two
///    QueryAsync calls in a row; the cap arithmetic and both SQL texts are tested,
///    the sequencing is a staging-pass observation.
///  * THE DIAGNOSTIC'S try/catch, and its move to the end of the sweep. Proving a
///    COUNT(*) can no longer take the refund replay down with it needs a database
///    that can be made to deadlock.
///  * THE REFUND LOOP'S SHUTDOWN ABORT. That a cancelled database call is read
///    from the token rather than from the exception type — so a shutdown can never
///    be miscounted as a refund error — needs a token tripped inside a live
///    UPDATE. What can be said here is that the type-based filter is gone.
///  * THE WRONG-MERCHANT ALARM'S REMAINING BLIND SPOTS, which the minimum sample
///    reduces rather than removes: a floor genuinely carrying ten or more rows
///    that 404 forever and nothing readable still repeats every run, and a floor
///    that never has ten open orders at once cannot trip the detector at all. The
///    credential is meant to be proved once at setup — that gate is on the
///    production verification list — not discovered by this sweep.
///  * THE HOST-SHUTDOWN 503. The early return needs a cancellation token tripped
///    partway through a live Square loop with a database behind it.
///  * THE HEALED/PAID-NOTHING-SOLD SPLIT and the per-payment containment on the
///    refund replay. Both are branches inside the sweep's own loops, around a
///    FulfillResult and an UPDATE that only a database produces. What is checked
///    here is the arithmetic and the rules they hang off, not the counters.
///  * THAT A DELETE 404 STILL CONFIRMS A DELETE. It no longer does, anywhere.
///    This sweep's gate (no corroboration, no pass 3) was the first half; the
///    second is inside CheckoutFulfillment.RetireLinksAsync, which now asks
///    Square for one order of ours before it stamps link_deleted_at on any
///    delete — so the staff-action callers (PalletsFunction, InvoiceBox) are
///    covered by the same rule this file's predicates express, without needing
///    a run to reason about. Pinned in RetireLinksCorroborationTests.
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
    [InlineData("CANCELED", "link")]   // and so is a canceled one: see below
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
    /// CANCELED AND PAID AT THE SAME TIME, which Square should not produce and
    /// which the sweep must not resolve in favour of the cancel. The cancel branch
    /// writes link_deleted_at, and a row that is closed AND stamped matches
    /// neither arm of the backlog window — so taking that branch here would remove
    /// the order from the only pass that could still find the payment, silently.
    /// Money wins: it is healed, whatever kind it is, and nothing stamps it dead.
    /// </summary>
    [Theory]
    [InlineData("link")]
    [InlineData("invoice")]
    public void A_canceled_order_that_is_also_paid_is_healed_not_retired(string kind)
        => Assert.Equal(SquareFunction.ReconcileVerdict.Heal,
            SquareFunction.VerdictFor("CANCELED", paid: true, kind));

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
        => Assert.False(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Order, paid: true, squareAnswered: true));

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
        => Assert.False(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Unreachable, paid, squareAnswered: true));

    /// <summary>Square answered, there is no payment on it: this is the case the age rule exists for.</summary>
    [Fact]
    public void An_unpaid_order_square_answered_for_is_closable()
        => Assert.True(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.Order, paid: false, squareAnswered: true));

    /// <summary>
    /// A 404 counts as established — but only alongside proof that we are looking
    /// at our own merchant. Without this an order Square has forgotten could never
    /// be retired and would occupy a slot in every future window forever.
    /// </summary>
    [Fact]
    public void An_order_square_has_no_record_of_is_closable_when_the_run_reached_our_merchant()
        => Assert.True(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.NotFound, paid: false, squareAnswered: true));

    // ---- The wrong merchant: a 404 that is not about us ---------------------
    //
    // THE DEFECT THESE EXIST FOR, and it is the same catastrophe as the age rule
    // reached from the other side. A credential pointed at the wrong Square
    // merchant — a sandbox token in production, a re-created application, a
    // location change after a migration — does not move a single cent of anybody's
    // money. Our buyers' orders and their payments stay exactly where they were.
    // Only our ability to SEE them moves, and what comes back instead is a 404 on
    // every call.
    //
    // A 404 was then trusted twice: as proof the order was never paid (so pass 2
    // closed it) and as proof the link was gone (so pass 3 stamped
    // link_deleted_at). Closed AND stamped matches neither arm of the backlog
    // window, so the row was unreachable by every pass forever — including after
    // somebody fixed the credential — while the sweep reported zero errors.

    /// <summary>
    /// THE ONE THAT MUST NEVER COME BACK. Every call 404s, so there is no readable
    /// order anywhere in the run to prove the credential reaches our merchant, and
    /// not one of those orders may be closed.
    /// </summary>
    [Fact]
    public void A_run_where_every_order_404s_closes_nothing()
        => Assert.Empty(SquareFunction.ClosableOrderIds(new[]
        {
            new SquareFunction.OrderCheck("A", SquareFunction.SquareReply.NotFound, false),
            new SquareFunction.OrderCheck("B", SquareFunction.SquareReply.NotFound, false),
            new SquareFunction.OrderCheck("C", SquareFunction.SquareReply.NotFound, false),
        }));

    /// <summary>The same predicate, stated directly: no 404 is evidence on its own.</summary>
    [Fact]
    public void An_uncorroborated_404_establishes_nothing()
        => Assert.False(SquareFunction.VerifiedUnpaid(SquareFunction.SquareReply.NotFound, paid: false, squareAnswered: false));

    /// <summary>
    /// And the corroboration is a real order, not merely a reply. A run of nothing
    /// but rate limits and 404s has still never seen our merchant, so its 404s
    /// stay worthless — an unreachable answer cannot vouch for anything.
    /// </summary>
    [Fact]
    public void Unreachable_replies_do_not_corroborate_a_404()
    {
        var checks = new[]
        {
            new SquareFunction.OrderCheck("GONE",  SquareFunction.SquareReply.NotFound, false),
            new SquareFunction.OrderCheck("429-A", SquareFunction.SquareReply.Unreachable, false),
            new SquareFunction.OrderCheck("429-B", SquareFunction.SquareReply.Unreachable, false),
        };
        Assert.False(SquareFunction.SquareAnswered(checks));
        Assert.Empty(SquareFunction.ClosableOrderIds(checks));

        // And this exact set — one 404 among rate limits — is the partial outage
        // the alarm used to shout about. It is not evidence about credentials.
        Assert.False(SquareFunction.LooksLikeWrongMerchant(checks));
    }

    /// <summary>
    /// The other half of the same rule, which is what keeps the fix from being a
    /// new way to jam the sweep: ONE readable order is enough. A genuinely
    /// forgotten order alongside orders that answer is still retired on schedule,
    /// so the ordinary sweep is unchanged.
    /// </summary>
    [Fact]
    public void One_readable_order_is_enough_to_make_the_run_s_404s_count()
    {
        var checks = new[]
        {
            new SquareFunction.OrderCheck("READABLE", SquareFunction.SquareReply.Order, false),
            new SquareFunction.OrderCheck("GONE",     SquareFunction.SquareReply.NotFound, false),
        };
        Assert.True(SquareFunction.SquareAnswered(checks));
        Assert.False(SquareFunction.LooksLikeWrongMerchant(checks));
        Assert.Equal(new[] { "READABLE", "GONE" }, SquareFunction.ClosableOrderIds(checks));
    }

    /// <summary>
    /// A PAID order among the readable ones corroborates just as well as an unpaid
    /// one — the question the flag answers is "can this credential see our
    /// merchant", not "is anything unpaid". And the paid one is still not closable.
    /// </summary>
    [Fact]
    public void A_paid_order_corroborates_the_run_without_becoming_closable()
    {
        var checks = new[]
        {
            new SquareFunction.OrderCheck("PAID", SquareFunction.SquareReply.Order, true),
            new SquareFunction.OrderCheck("GONE", SquareFunction.SquareReply.NotFound, false),
        };
        Assert.True(SquareFunction.SquareAnswered(checks));
        Assert.Equal(new[] { "GONE" }, SquareFunction.ClosableOrderIds(checks));
    }

    private static SquareFunction.OrderCheck[] NotFound(int n)
        => Enumerable.Range(0, n)
            .Select(i => new SquareFunction.OrderCheck($"GONE-{i}", SquareFunction.SquareReply.NotFound, false))
            .ToArray();

    private static SquareFunction.OrderCheck[] Unreachable(int n)
        => Enumerable.Range(0, n)
            .Select(i => new SquareFunction.OrderCheck($"429-{i}", SquareFunction.SquareReply.Unreachable, false))
            .ToArray();

    /// <summary>
    /// THE ALARM, which is the only live misconfiguration detector this system
    /// has. A whole run's worth of 404s with no readable order among them is a
    /// state our own data cannot produce: we only ever ask about orders we created.
    /// </summary>
    [Fact]
    public void An_all_404_run_is_reported_as_the_wrong_merchant()
        => Assert.True(SquareFunction.LooksLikeWrongMerchant(
            NotFound(SquareFunction.ReconcileWrongMerchantMinSample)));

    /// <summary>
    /// THE QUIET FLOOR — the shape that made this detector worth nothing. A couple
    /// of stale rows that 404 corroborate no run, so by this sweep's own money rule
    /// they can never be closed or retired: they are drawn again next run, and the
    /// old condition shouted about credentials EVERY run, indefinitely. A minimum
    /// sample is what stops that, and it is why the alarm is not "any 404".
    /// </summary>
    [Fact]
    public void A_quiet_floor_of_a_few_stale_404s_does_not_shout()
    {
        // Three, as a flat number and not as a fraction of the constant: whatever
        // the minimum is set to, a floor carrying a handful of rows Square has
        // forgotten must never be read as a credential pointing elsewhere.
        Assert.False(SquareFunction.LooksLikeWrongMerchant(NotFound(3)));
        Assert.False(SquareFunction.LooksLikeWrongMerchant(
            NotFound(SquareFunction.ReconcileWrongMerchantMinSample - 1)));
    }

    /// <summary>
    /// A PARTIAL OUTAGE IS NOT A CREDENTIAL PROBLEM. One genuinely forgotten order
    /// plus a rate-limit storm on everything else satisfied the old condition
    /// outright. The 404s must outnumber everything else the run saw, and a tie is
    /// not "most" — half the run unreachable is an outage, whatever the other half
    /// did.
    /// </summary>
    [Fact]
    public void A_run_half_lost_to_rate_limits_is_not_the_wrong_merchant_alarm()
    {
        var checks = NotFound(SquareFunction.ReconcileWrongMerchantMinSample)
            .Concat(Unreachable(SquareFunction.ReconcileWrongMerchantMinSample)).ToArray();
        Assert.False(SquareFunction.SquareAnswered(checks));
        Assert.False(SquareFunction.LooksLikeWrongMerchant(checks));
    }

    /// <summary>
    /// But it still fires through noise, which is the point of a ratio rather than
    /// a demand for perfection: a wrong credential during a bad afternoon 404s most
    /// of what it asks about, and that is precisely the run this exists for.
    /// </summary>
    [Fact]
    public void A_wrong_credential_still_alarms_through_some_rate_limiting()
        => Assert.True(SquareFunction.LooksLikeWrongMerchant(
            NotFound(SquareFunction.ReconcileWrongMerchantMinSample + 2).Concat(Unreachable(5)).ToArray()));

    /// <summary>
    /// And one readable order silences it however many 404s came with it — the
    /// credential demonstrably reaches our merchant, so the 404s are just orders
    /// Square has forgotten.
    /// </summary>
    [Fact]
    public void One_readable_order_silences_the_alarm_however_many_404s_came_with_it()
        => Assert.False(SquareFunction.LooksLikeWrongMerchant(
            NotFound(SquareFunction.ReconcileWrongMerchantMinSample * 3)
                .Append(new SquareFunction.OrderCheck("READABLE", SquareFunction.SquareReply.Order, false))
                .ToArray()));

    /// <summary>
    /// And it must not cry wolf on the two states that look similar and are not: a
    /// run that asked about nothing at all (an idle floor), and a run where Square
    /// was simply unreachable (a 429 storm, a 5xx). Neither saw a 404, so neither
    /// is evidence of a wrong credential — they are just quiet or broken.
    /// </summary>
    [Fact]
    public void A_quiet_or_unreachable_run_is_not_the_wrong_merchant_alarm()
    {
        Assert.False(SquareFunction.LooksLikeWrongMerchant(Array.Empty<SquareFunction.OrderCheck>()));
        Assert.False(SquareFunction.LooksLikeWrongMerchant(new[]
        {
            new SquareFunction.OrderCheck("429", SquareFunction.SquareReply.Unreachable, false),
        }));
    }

    // ---- LinksToRetire: the branch that actually closes the defect ----------
    //
    // The closing predicate above is the tested half of the fix and the lesser
    // half. Pass 3 does not only delete what this run closed: it samples a
    // STANDING QUEUE built by earlier healthy runs. A delete that 404s reads as
    // confirmation, so at the wrong merchant every delete "succeeds" and stamps
    // link_deleted_at — and a stamped row is out of the backlog recovery window
    // for good, which is where a wrongly closed PAID order was hiding. That branch
    // used to be an inline condition between two database calls, defended by
    // prose. Last round's defect got through as confident prose.

    private static CanceledLink Link(string id) => new(id, "LNK-" + id);

    /// <summary>
    /// THE ONE THIS EXTRACTION EXISTS FOR. Nothing corroborated the run, so nothing
    /// is retired — not the standing queue, and not this run's own cancels either,
    /// even when both are handed over full. Handing it a non-empty closedThisRun is
    /// the whole point: with no corroboration pass 2 closes nothing, so that list is
    /// empty in practice today, and the guard must not be leaning on that.
    /// </summary>
    [Fact]
    public void An_uncorroborated_run_retires_nothing_from_either_source()
        => Assert.Empty(SquareFunction.LinksToRetire(
            squareAnswered: false,
            closedThisRun: new[] { Link("CLOSED-NOW") },
            standingQueueSample: new[] { Link("QUEUED-A"), Link("QUEUED-B") },
            budget: SquareFunction.ReconcileMaxSquareCalls));

    /// <summary>A corroborated run retires both sources, this run's cancels first — those links are payable right now.</summary>
    [Fact]
    public void A_corroborated_run_retires_this_runs_cancels_before_the_queue()
        => Assert.Equal(new[] { "CLOSED-NOW", "QUEUED-A" }, SquareFunction.LinksToRetire(
            true, new[] { Link("CLOSED-NOW") }, new[] { Link("QUEUED-A") },
            SquareFunction.ReconcileMaxSquareCalls).Select(l => l.OrderId));

    /// <summary>A link in both lists is one delete, not two.</summary>
    [Fact]
    public void A_link_in_both_sources_is_retired_once()
        => Assert.Equal(new[] { "BOTH", "QUEUED" }, SquareFunction.LinksToRetire(
            true, new[] { Link("BOTH") }, new[] { Link("BOTH"), Link("QUEUED") },
            SquareFunction.ReconcileMaxSquareCalls).Select(l => l.OrderId));

    /// <summary>
    /// The budget caps the standing queue and never this run's cancels: a link we
    /// closed a minute ago is payable RIGHT NOW, while a queue row has already
    /// waited runs and will be sampled again.
    /// </summary>
    [Fact]
    public void The_budget_caps_the_queue_sample_and_not_this_runs_cancels()
    {
        var mine = new[] { Link("A"), Link("B") };
        Assert.Equal(new[] { "A", "B", "Q1" }, SquareFunction
            .LinksToRetire(true, mine, new[] { Link("Q1"), Link("Q2") }, budget: 3)
            .Select(l => l.OrderId));
        Assert.Equal(new[] { "A", "B" }, SquareFunction
            .LinksToRetire(true, mine, new[] { Link("Q1") }, budget: 1)
            .Select(l => l.OrderId));
    }

    // ---- PaymentOf: "unpaid" must be evidenced, not merely absent -----------
    //
    // The same thesis as SquareReply.Unreachable, one level down. A response we
    // could not understand tells us nothing about payment — and an order object
    // carrying neither a tenders array nor a net_amount_due_money is exactly that:
    // a truncated body, a proxy-mangled response, an empty object. It used to
    // return a bare false, which the sweep reads as VERIFIED UNPAID and closes on.

    private static JsonElement Order(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>The ordinary paid link: Square attaches a tender.</summary>
    [Fact]
    public void An_order_with_a_tender_is_paid()
        => Assert.Equal(SquareService.PaymentSignal.Paid,
            SquareService.PaymentOf(Order("""{"tenders":[{"payment_id":"PAY1"}]}""")));

    /// <summary>
    /// The GOTCHA this method exists for: a paid payment-link order stays
    /// state=OPEN forever, so nothing about payment may be read off the state.
    /// Zero due is the second positive proof of payment.
    /// </summary>
    [Fact]
    public void An_order_with_nothing_left_due_is_paid()
        => Assert.Equal(SquareService.PaymentSignal.Paid,
            SquareService.PaymentOf(Order("""{"state":"OPEN","net_amount_due_money":{"amount":0,"currency":"USD"}}""")));

    /// <summary>Money still owed is positive proof of NON-payment. This is the ordinary open link.</summary>
    [Fact]
    public void An_order_with_money_still_due_is_unpaid()
        => Assert.Equal(SquareService.PaymentSignal.Unpaid,
            SquareService.PaymentOf(Order("""{"state":"OPEN","net_amount_due_money":{"amount":4500,"currency":"USD"}}""")));

    /// <summary>An empty tenders array is Square saying, positively, that nothing was tendered.</summary>
    [Fact]
    public void An_order_with_an_empty_tenders_array_is_unpaid()
        => Assert.Equal(SquareService.PaymentSignal.Unpaid,
            SquareService.PaymentOf(Order("""{"state":"OPEN","tenders":[]}""")));

    /// <summary>
    /// THE ONE THAT MUST NOT READ AS UNPAID. Neither signal present — so we know
    /// nothing, and the sweep must treat it exactly as it treats a 5xx. A bare
    /// false here is a closable order and a buried charge.
    /// </summary>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"state":"OPEN"}""")]
    [InlineData("""{"id":"ORD1","location_id":"LOC1","line_items":[]}""")]
    [InlineData("""{"net_amount_due_money":{}}""")]
    [InlineData("""{"net_amount_due_money":{"amount":"4500"}}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void An_order_object_with_no_payment_signal_is_unknown(string json)
        => Assert.Equal(SquareService.PaymentSignal.Unknown, SquareService.PaymentOf(Order(json)));

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
    /// The recovery route, and it has its OWN draw now. A link whose order was paid
    /// is exactly the link Square refuses to cancel, so an unconfirmed deletion is
    /// where a wrongly closed paid order surfaces. One run must keep reaching those
    /// rows — it is what makes a closed order reachable by a pass that can still
    /// discover payment, and what lets that row leave the delete queue instead of
    /// blocking every link behind it.
    /// </summary>
    [Fact]
    public void The_recovery_queue_has_its_own_reserved_draw()
    {
        Assert.Contains("status = 'canceled'", SquareFunction.ReconcileRetireRecheckSql);
        Assert.Contains("link_deleted_at IS NULL", SquareFunction.ReconcileRetireRecheckSql);
        Assert.True(SquareFunction.ReconcileRetireRecheckWindow > 0,
            "the recovery queue's share of the backlog may never be zero");
        Assert.True(SquareFunction.ReconcileRetireRecheckWindow < SquareFunction.ReconcileBacklogWindow,
            "nor may it swallow the whole backlog window and starve the open orders");
    }

    /// <summary>
    /// AND IT IS NOT SAMPLED TOGETHER WITH THE OPEN ORDERS ANY MORE. That was the
    /// dilution this round introduced and did not cost: widening the backlog from
    /// aged orders to ALL open orders also widened what the queue competes with,
    /// from a small self-draining subset to every open cart on the floor. A few
    /// hundred carts against a handful of queue rows and a given queue row's odds
    /// of being drawn in a run collapse — which is a charged buyer's wait, not a
    /// tidiness point. Put the queue back into this SELECT and it returns.
    /// </summary>
    [Fact]
    public void The_open_backlog_draw_does_not_sample_the_recovery_queue_with_it()
    {
        Assert.DoesNotContain("canceled", SquareFunction.ReconcileBacklogSql);
        Assert.DoesNotContain("link_deleted_at", SquareFunction.ReconcileBacklogSql);
    }

    /// <summary>
    /// Reserved, not ring-fenced. An empty queue — the healthy floor — hands the
    /// whole backlog window back to the open orders, so the split costs nothing
    /// when there is nothing to recover; a deep queue can never take more than its
    /// share, so the open orders cannot be starved either.
    /// </summary>
    [Fact]
    public void The_queues_reserved_share_costs_the_open_backlog_nothing_when_it_is_empty()
    {
        Assert.Equal(SquareFunction.ReconcileBacklogWindow, SquareFunction.OpenBacklogCap(0));
        Assert.Equal(SquareFunction.ReconcileBacklogWindow - 1, SquareFunction.OpenBacklogCap(1));
        Assert.Equal(SquareFunction.ReconcileBacklogWindow - SquareFunction.ReconcileRetireRecheckWindow,
            SquareFunction.OpenBacklogCap(SquareFunction.ReconcileRetireRecheckWindow));
        Assert.Equal(SquareFunction.ReconcileBacklogWindow - SquareFunction.ReconcileRetireRecheckWindow,
            SquareFunction.OpenBacklogCap(SquareFunction.ReconcileRetireRecheckWindow * 5));
        Assert.True(SquareFunction.OpenBacklogCap(int.MaxValue) > 0,
            "the open-order draw may never be starved to nothing by the queue");
    }

    /// <summary>
    /// And the open orders a newest-first window can never reach — the invoice
    /// that drifts past the cap and would otherwise never heal again.
    ///
    /// OF ANY AGE, which is the gap this round closed. The backlog used to take
    /// only orders older than the link age limit, so during a burst an order a few
    /// days old sitting outside the newest-first half was asked about by NEITHER
    /// half and a missed notification on it waited until it aged in. An age filter
    /// reappearing here re-opens that gap.
    /// </summary>
    [Fact]
    public void The_backlog_window_reaches_open_orders_of_any_age()
    {
        Assert.Contains("status = 'open'", SquareFunction.ReconcileBacklogSql);
        Assert.DoesNotContain("DATEADD", SquareFunction.ReconcileBacklogSql);
        Assert.DoesNotContain("@ageDays", SquareFunction.ReconcileBacklogSql);
    }

    /// <summary>
    /// The queue-dilution alarm (there is no terminal state to give a link Square
    /// will never confirm without a schema change, so the pile is made visible
    /// instead), and the threshold whose justification this round had to repair.
    ///
    /// "Past this many rows the sample can no longer reach the whole queue in one
    /// run" was true of the aged-only backlog and FALSE the moment the queue shared
    /// one sample with every open cart on the floor: coverage broke down well below
    /// the number, at a point that moved with the open-order count. This test
    /// encoded that wrong premise by allowing the whole backlog window. It is
    /// arithmetic again only because the queue has a reserved draw — more rows than
    /// that draw can take is exactly when one run stops covering the queue.
    /// </summary>
    [Fact]
    public void The_retire_queue_warns_exactly_when_one_run_stops_covering_it()
    {
        Assert.True(SquareFunction.ReconcileRetireQueueWarnAt > 0);
        Assert.Equal(SquareFunction.ReconcileRetireRecheckWindow, SquareFunction.ReconcileRetireQueueWarnAt);
    }

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
        Assert.Equal(SquareFunction.ReconcileBacklogWindow,
            SquareFunction.ReconcileRetireRecheckWindow + SquareFunction.OpenBacklogCap(SquareFunction.ReconcileRetireRecheckWindow));
    }
}
