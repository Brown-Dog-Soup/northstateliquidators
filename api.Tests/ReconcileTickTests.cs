using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// Task 13: the keyed public route the GitHub Actions cron calls, and the split
/// that lets one sweep serve two triggers.
///
/// WHAT IS PROVABLE HERE. The whole of the tick route's own behaviour is decided
/// before a connection is opened or a Square client is built — the key, the
/// not-configured refusal, and the order those two are answered in — so a closed
/// SQL port and an IHttpClientFactory that throws on use are between them enough
/// to prove it: every assertion below is "this answered, and it answered without
/// touching either". That is the same instrument ReconcileTests uses for the
/// sweep's own refusal, pointed at the new door.
///
/// STILL NOT COVERED, and worth saying plainly:
///  * THE SWEEP ITSELF, through this route. Once the key is accepted the route is
///    ReconcileCoreAsync and nothing else, and that needs a database — every
///    residual named at the top of ReconcileTests applies unchanged here.
///  * BOTH RESPONSE SHAPES COMING BACK OUT. The ordinary OK body and the 503
///    host-shutdown body are produced inside the sweep, so neither can be staged
///    without SQL Server. What IS pinned below is the property that makes passing
///    them through possible at all: the core returns an IActionResult, so the
///    status code travels with the body. An implementation that returned a bare
///    result object and let each trigger wrap it in OkObjectResult would turn a
///    503 abort into a 200 "all clear" on an unattended schedule, which is the
///    exact failure this task exists to avoid, and the reflection test catches it.
///    It cannot catch a trigger that ignores the result and builds its own.
///  * THE WORKFLOW. .github/workflows/square-reconcile.yml is bash and jq against
///    a live site; what it does with each answer is described in the file and
///    verified by running it, not here.
///  * THE 401 AS SEEN BY THE CALLER. staticwebapp.config.json turns a 401 into a
///    302 to the AAD login at the edge. The function returns 401 — that is what
///    is asserted — and the cron fails on any non-200, so the edge's rewrite
///    changes which number appears in the log and nothing else.
///
/// THE AMENDMENT (CancelBoxInvoice's lock order) IS NOT TESTED, and cannot be:
/// proving a deadlock is gone needs two concurrent transactions on a real SQL
/// Server. The change is a statement reorder inside one transaction with no
/// outcome difference; the reasoning is written at the statement.
/// </summary>
public class ReconcileTickTests
{
    // Closed port, 1s timeout: any path that reaches SQL fails loudly rather than
    // hanging, so "it answered" is proof it never got there.
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

    private static SquareFunction Build(IHttpClientFactory http, string? cronKey, bool squareConfigured = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = squareConfigured ? "test-token" : null,
            ["SQUARE_SANDBOX_LOCATION_ID"] = squareConfigured ? "LOC1" : null,
            ["SqlConnectionString"] = DeadSql,
        };
        if (cronKey != null) settings["RECONCILE_CRON_KEY"] = cronKey;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var square = new SquareService(http, cfg, NullLogger<SquareService>.Instance);
        return new SquareFunction(new SqlService(cfg, NullLogger<SqlService>.Instance), square,
            new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance),
            cfg, NullLogger<SquareFunction>.Instance);
    }

    private static HttpRequest Req(string? key)
    {
        var req = new DefaultHttpContext().Request;
        if (key != null) req.Headers["x-nsl-cron-key"] = key;
        return req;
    }

    private const string GoodKey = "2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6c";

    // ---- CronKeyMatches: the comparison itself -----------------------------

    /// <summary>
    /// THE DEFECT THIS EXISTS FOR. An app setting that was never created reads as
    /// null, and a caller who sends no header presents "" — compared naively those
    /// two are equal and the route is wide open to anyone who finds the URL. Every
    /// shape of "not configured" must refuse, including the caller who sends
    /// exactly the same nothing back.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("", null)]
    [InlineData("   ", "   ")]
    [InlineData("\t", "\t")]
    [InlineData(null, GoodKey)]
    [InlineData("", GoodKey)]
    public void An_unconfigured_key_never_matches_anything(string? expected, string? given)
        => Assert.False(SquareFunction.CronKeyMatches(expected, given));

    [Fact]
    public void The_configured_key_matches_itself()
        => Assert.True(SquareFunction.CronKeyMatches(GoodKey, GoodKey));

    /// <summary>
    /// Near misses. A prefix (length differs), one flipped character (same
    /// length), a case change, and the whitespace an editor or a copy-paste out of
    /// a portal adds — none of them is the key.
    /// </summary>
    [Theory]
    [InlineData("2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6")]    // one short
    [InlineData("2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6cc")]  // one long
    [InlineData("2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6d")]   // last character changed
    [InlineData("2F6A1C9E0B7D4A3F8C5E1D2B9A0F7E6C")]   // upper case
    [InlineData(" 2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6c")]
    [InlineData("2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6c ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_near_miss_is_not_the_key(string? given)
        => Assert.False(SquareFunction.CronKeyMatches(GoodKey, given));

    // ---- The route's door, before any connection ---------------------------

    /// <summary>
    /// RECONCILE_CRON_KEY never set on the SWA. The route cannot authenticate
    /// anyone, so it says so — 503, not 401, because "nobody can call this" and
    /// "you called it wrong" send a rollout in opposite directions. And it says so
    /// without opening a connection: the sweep is not run for an unauthenticated
    /// caller just because the deployment forgot a setting.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_cron_key_is_a_503_before_any_sql_or_square()
    {
        var http = new ExplodingHttpClientFactory();
        var r = Assert.IsType<ObjectResult>(
            await Build(http, cronKey: null).ReconcileTick(Req(GoodKey), CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>A key configured as blank is the same as no key at all.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_configured_key_is_also_a_503(string configured)
    {
        var http = new ExplodingHttpClientFactory();
        var r = Assert.IsType<ObjectResult>(
            await Build(http, cronKey: configured).ReconcileTick(Req(configured), CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>No header, wrong header, near-miss header: 401, and no sweep.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("2f6a1c9e0b7d4a3f8c5e1d2b9a0f7e6d")]
    public async Task A_missing_or_wrong_key_is_a_401_before_any_sql_or_square(string? given)
    {
        var http = new ExplodingHttpClientFactory();
        Assert.IsType<UnauthorizedResult>(
            await Build(http, cronKey: GoodKey).ReconcileTick(Req(given), CancellationToken.None));
        Assert.Equal(0, http.Calls);
    }

    /// <summary>
    /// ORDER OF THE TWO REFUSALS. An unauthenticated caller is turned away before
    /// the route says anything about the deployment behind it — so a wrong key
    /// cannot be used to probe whether Square is configured here, and, more
    /// practically, a rollout that sets neither setting reports the cron key
    /// (which is this route's problem) rather than Square (which is not).
    /// </summary>
    [Fact]
    public async Task Auth_is_decided_before_anything_is_said_about_square()
    {
        var http = new ExplodingHttpClientFactory();
        Assert.IsType<UnauthorizedResult>(await Build(http, cronKey: GoodKey, squareConfigured: false)
            .ReconcileTick(Req("wrong"), CancellationToken.None));

        // And with no cron key at all, the 503 is the cron key's, not Square's.
        var r = Assert.IsType<ObjectResult>(await Build(http, cronKey: null, squareConfigured: false)
            .ReconcileTick(Req(GoodKey), CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Contains("Cron key", r.Value!.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>
    /// A GOOD KEY IS NOT ENOUGH TO RUN A HALF-SWEEP. Square off means pass 1
    /// cannot ask anything, and the sweep would cancel links pass 3 could never
    /// delete — the same refusal the staff route gives, reached the same way.
    /// </summary>
    [Fact]
    public async Task A_good_key_with_square_off_is_a_503_before_any_sql_or_square()
    {
        var http = new ExplodingHttpClientFactory();
        var r = Assert.IsType<ObjectResult>(await Build(http, cronKey: GoodKey, squareConfigured: false)
            .ReconcileTick(Req(GoodKey), CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Contains("Square", r.Value!.ToString()!, StringComparison.Ordinal);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>
    /// THE POSITIVE CASE, which is the one that stops all of the above from being
    /// satisfied by a route that simply refuses everything. A correct key with
    /// Square configured is let through — and the proof is that it then fails on
    /// the closed SQL port, which only the sweep touches. Every other test in this
    /// file asserts something answered WITHOUT reaching SQL; this one asserts the
    /// opposite, so the two together bracket the door.
    /// </summary>
    [Fact]
    public async Task A_good_key_is_let_through_to_the_sweep()
    {
        var http = new ExplodingHttpClientFactory();
        var fn = Build(http, cronKey: GoodKey);
        await Assert.ThrowsAnyAsync<Exception>(
            () => fn.ReconcileTick(Req(GoodKey), CancellationToken.None));
    }

    // ---- The split ---------------------------------------------------------

    /// <summary>
    /// THE SPLIT'S LOAD-BEARING SIGNATURE, pinned the way SchemaContractTests pins
    /// a SQL text: as a contract, because the behaviour behind it needs a database.
    ///
    /// The sweep has two response shapes — the OK body of counters and a shorter
    /// body with an `aborted` marker carrying a 503 when the host shuts down
    /// mid-run. The status code is therefore part of the answer. ReconcileCoreAsync
    /// returns IActionResult so that both shapes reach both triggers intact; the
    /// tempting simplification (return the anonymous object, let each trigger wrap
    /// it in OkObjectResult) reports a stopped sweep as a completed one, which on
    /// an unattended schedule nobody would ever notice.
    ///
    /// This asserts the signature, not the pass-through. A trigger that discarded
    /// the result and built its own would still satisfy it.
    /// </summary>
    [Fact]
    public void The_shared_core_returns_an_IActionResult_so_the_status_code_survives()
    {
        var core = typeof(SquareFunction).GetMethod("ReconcileCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(core);
        Assert.Equal(typeof(Task<IActionResult>), core!.ReturnType);
    }

    /// <summary>
    /// Both triggers exist, on the routes the workflow and the staff page call.
    /// The tick route must be under /api/public/ — that prefix is what
    /// staticwebapp.config.json makes anonymous, and anywhere else the cron gets
    /// the AAD login page instead of a sweep.
    /// </summary>
    [Theory]
    [InlineData("Reconcile", "square-reconcile")]
    [InlineData("ReconcileTick", "public/reconcile-tick")]
    public void Each_trigger_is_on_the_route_its_caller_uses(string method, string route)
    {
        var p = typeof(SquareFunction).GetMethod(method)!.GetParameters()[0];
        var trigger = p.GetCustomAttributes()
            .Single(a => a.GetType().Name == "HttpTriggerAttribute");
        Assert.Equal(route, (string?)trigger.GetType().GetProperty("Route")!.GetValue(trigger));
    }
}
