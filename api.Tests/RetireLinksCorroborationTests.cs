using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// CheckoutFulfillment.RetireLinksAsync: what is allowed to stamp link_deleted_at.
///
/// WHY THIS FILE EXISTS. The stamp used to be housekeeping and is now the thing
/// that decides whether a canceled row stays inside SquareFunction.ReconcileBacklogSql
/// — the only pass that still asks Square about the order and heals it if it comes
/// back paid. Two ways of stamping it without evidence were left in the method:
/// a 404 from Square read as confirmation (which is what EVERY call looks like on
/// a credential pointed at the wrong merchant), and a row with no link id stamped
/// with no Square call at all.
///
/// THE SEAM, and why these are not tests that would pass against a broken
/// implementation. There is no database here — the connection string points at a
/// closed port with a 1s timeout — so the stamp cannot be observed directly. What
/// CAN be observed is whether it was ATTEMPTED: a stamp against that connection
/// throws, RetireLinksAsync swallows the exception and logs "could not delete
/// link", and a captured logger sees that line. So:
///   * a test that requires the stamp to be refused FAILS against the old code,
///     which attempted it and logged;
///   * and the positive control below (probe answers, therefore stamp) FAILS
///     against an implementation that simply never stamps anything, which is the
///     way a gate like this rots.
/// Both directions are pinned, and the Square traffic itself is asserted from the
/// wire so "asked Square nothing" is a fact rather than an inference.
///
/// WHAT IS NOT HERE. That the UPDATE writes the right row, and that an unstamped
/// row is still drawn by the backlog window, are SQL Server behaviour — staging
/// pass, like the rest of this suite's database half.
/// </summary>
public class RetireLinksCorroborationTests
{
    // Closed port, 1s timeout: an attempted stamp fails loudly (and is swallowed
    // and logged), a stamp that is never attempted leaves no trace at all.
    private const string DeadSql =
        "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True";

    private const string StampAttempted = "could not delete link";

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
        public readonly List<string> Seen = new();

        public RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(_route(request));
        }

        public int Deletes => Seen.Count(s => s.StartsWith("DELETE ", StringComparison.Ordinal));
        public int OrderReads => Seen.Count(s => s.Contains("/v2/orders/", StringComparison.Ordinal));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));

        public bool Saw(string fragment) => Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string? body)
    {
        var resp = new HttpResponseMessage(status);
        if (body != null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return resp;
    }

    /// <summary>The wrong merchant, exactly: every call 404s, nothing ever reads back.</summary>
    private static HttpResponseMessage Everything404(HttpRequestMessage _)
        => Json(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"NOT_FOUND\"}]}");

    /// <summary>Our merchant with a link already gone: the delete 404s, the ORDER reads back.</summary>
    private static HttpResponseMessage DeleteGoneOrderReadable(HttpRequestMessage r)
        => r.RequestUri!.AbsolutePath.Contains("/v2/orders/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"order\":{\"id\":\"ORD1\",\"state\":\"CANCELED\",\"tenders\":[]}}")
            : Json(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"NOT_FOUND\"}]}");

    private static (CheckoutFulfillment Fulfill, RouteHandler Http, CapturingLogger<CheckoutFulfillment> Log) Build(
        Func<HttpRequestMessage, HttpResponseMessage> route, bool squareConfigured = true)
    {
        var http = new RouteHandler(route);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = squareConfigured ? "test-token" : null,
            ["SQUARE_SANDBOX_LOCATION_ID"] = squareConfigured ? "LOC1" : null,
        }).Build();
        var log = new CapturingLogger<CheckoutFulfillment>();
        var square = new SquareService(new StubHttpClientFactory(http), cfg, NullLogger<SquareService>.Instance);
        return (new CheckoutFulfillment(square, log), http, log);
    }

    private static CanceledLink[] Links(int n) =>
        Enumerable.Range(1, n).Select(i => new CanceledLink($"ORD{i}", $"LINK{i}")).ToArray();

    // ---- 1. A 404 is not confirmation unless something readable came back ----

    /// <summary>
    /// THE ONE THAT MUST NEVER COME BACK. Square 404s the delete AND 404s the
    /// order, which is what a token on the wrong merchant looks like from here —
    /// the link is still payable at ours and we cannot see it. Nothing may be
    /// stamped on that, because a canceled row that is stamped is out of the
    /// backlog window for good.
    ///
    /// The old code stamped, hit the dead connection and logged; that is what
    /// this asserts is absent.
    /// </summary>
    [Fact]
    public async Task A_404_delete_with_no_readable_order_stamps_nothing()
    {
        var (fulfill, http, log) = Build(Everything404);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(1), CancellationToken.None);

        Assert.Equal(1, http.Deletes);
        Assert.Equal(1, http.OrderReads);                 // it ASKED — it did not just refuse
        Assert.False(log.Saw(StampAttempted));            // and it did not reach SQL
        Assert.True(log.Saw("NOT ONE link_deleted_at was stamped"));
    }

    /// <summary>
    /// THE POSITIVE CONTROL, and the half that stops this gate rotting into "never
    /// stamp anything". Same 404 on the delete, but Square hands back one of our
    /// orders — so the credential is demonstrably on our merchant and the 404 is
    /// real evidence that the link is gone. The stamp is attempted, fails against
    /// the closed port, and is swallowed and logged.
    /// </summary>
    [Fact]
    public async Task A_404_delete_stamps_once_an_order_of_ours_reads_back()
    {
        var (fulfill, http, log) = Build(DeleteGoneOrderReadable);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(1), CancellationToken.None);

        Assert.Equal(1, http.OrderReads);
        Assert.True(log.Saw(StampAttempted));             // the UPDATE was reached
        Assert.False(log.Saw("NOT ONE link_deleted_at was stamped"));
    }

    /// <summary>
    /// The proof is about the CREDENTIAL, not about one row, so it is bought once
    /// per call: two links, one order read. A probe per link would double this
    /// method's Square traffic on the sweep's 40-link budget for no extra
    /// information.
    /// </summary>
    [Fact]
    public async Task One_readable_order_proves_the_merchant_for_the_whole_call()
    {
        var (fulfill, http, log) = Build(DeleteGoneOrderReadable);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(2), CancellationToken.None);

        Assert.Equal(2, http.Deletes);
        Assert.Equal(1, http.OrderReads);
        Assert.True(log.Saw(StampAttempted));
    }

    /// <summary>
    /// And when nothing reads back, the asking stops: a 404 on the order is itself
    /// evidence of the wrong merchant, so a fourth question learns nothing and
    /// costs a call. Five links, five deletes, three probes — and still no stamp.
    /// </summary>
    [Fact]
    public async Task The_merchant_proof_is_not_retried_link_after_link()
    {
        var (fulfill, http, log) = Build(Everything404);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(5), CancellationToken.None);

        Assert.Equal(5, http.Deletes);
        Assert.Equal(3, http.OrderReads);
        Assert.False(log.Saw(StampAttempted));
    }

    /// <summary>
    /// A 200 with no cancelled_order_id is Square's "still open" (SquareService
    /// pins that from the wire). There is nothing to corroborate, so no order is
    /// read and nothing is stamped — the corroboration must not turn an
    /// unconfirmed delete into a confirmed one.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_delete_is_not_corroborated_into_a_stamp()
    {
        var (fulfill, http, log) = Build(_ => Json(HttpStatusCode.OK, "{}"));
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(1), CancellationToken.None);

        Assert.Equal(1, http.Deletes);
        Assert.Equal(0, http.OrderReads);
        Assert.False(log.Saw(StampAttempted));
    }

    /// <summary>
    /// Square unconfigured: still no calls and still no stamp. The gate must not
    /// have introduced a path that asks about an order without a token.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_square_asks_nothing_and_stamps_nothing()
    {
        var (fulfill, http, log) = Build(Everything404, squareConfigured: false);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, Links(1), CancellationToken.None);

        Assert.Empty(http.Seen);
        Assert.False(log.Saw(StampAttempted));
    }

    // ---- 2. A row with no link id ------------------------------------------

    /// <summary>
    /// THE DECISION, pinned. A canceled order whose link id we never recorded used
    /// to be stamped on the reasoning that there is nothing to delete. There is
    /// not — but the stamp no longer says only "the link is gone", it says "stop
    /// looking at this row", and this is the one case where we cannot have
    /// cancelled anything at Square at all. So: no Square call (there is nothing
    /// to call about), NO stamp, and a warning that names the row.
    ///
    /// The old behaviour would have reached the dead connection here. The absence
    /// of that log line is the whole test.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_link_id_is_never_stamped()
    {
        var (fulfill, http, log) = Build(Everything404);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn, new[] { new CanceledLink("ORD1", null) }, CancellationToken.None);

        Assert.Empty(http.Seen);
        Assert.False(log.Saw(StampAttempted));
        Assert.True(log.Saw("carry no square_link_id"));
        Assert.True(log.Saw("ORD1"));
    }

    /// <summary>
    /// And it does not poison the rest of the batch: the link-bearing row beside
    /// it is still deleted, still corroborated, and still stamped.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_link_id_does_not_stop_the_rows_that_have_one()
    {
        var (fulfill, http, log) = Build(DeleteGoneOrderReadable);
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn,
            new[] { new CanceledLink("ORD0", null), new CanceledLink("ORD1", "LINK1") }, CancellationToken.None);

        Assert.Equal(1, http.Deletes);
        Assert.Equal(1, http.OrderReads);
        Assert.True(log.Saw(StampAttempted));            // ORD1's stamp was reached
        Assert.True(log.Saw("carry no square_link_id")); // ORD0 was reported, not retired
    }

    /// <summary>
    /// Still never throws, whichever branch runs — the callers are past their
    /// commit and have nothing left to roll back.
    /// </summary>
    [Fact]
    public async Task Nothing_here_throws()
    {
        var (fulfill, _, _) = Build(_ => throw new HttpRequestException("Square is down"));
        await using var conn = new SqlConnection(DeadSql);

        await fulfill.RetireLinksAsync(conn,
            new[] { new CanceledLink("ORD0", null), new CanceledLink("ORD1", "LINK1") }, CancellationToken.None);
    }
}
