using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// Cancel-on-change (spec §4): the admin paths that retire an open cart link.
///
/// WHAT IS NOT HERE, deliberately. The five paths themselves — UpdatePallet on a
/// price/state change, SoldToInventory, DeletePallet, InvoiceBox and
/// CancelBoxInvoice — are SQL from end to end: an UPDATE … OUTPUT over
/// checkout_orders inside a transaction, then INSERTs against it. There is no
/// seam that can be faked without lying about what SQL Server does with them, so
/// their behaviour is proved against a real database in the staging pass, not
/// here.
///
/// What IS testable without a database is the property all five lean on: every
/// one of them calls RetireLinksAsync AFTER its own transaction has already
/// committed, outside any try. If that method can throw, a committed sale or a
/// created invoice turns into a 500 and staff retry something that already
/// happened. So the contract is pinned from every failure direction here. Plus
/// the InvoiceBox refusals that must still answer before any of the new
/// transaction work starts — a connection string pointing at a closed port is
/// what proves no connection was taken.
/// </summary>
public class CancelOnChangeTests
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

    private sealed class ThrowingHandler(Func<Exception> ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw ex();
    }

    private sealed class ThrowingTransportFactory(Func<Exception> ex) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler(ex));
    }

    private static IConfiguration Cfg(bool squareConfigured) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = squareConfigured ? "test-token" : null,
            ["SQUARE_SANDBOX_LOCATION_ID"] = squareConfigured ? "LOC1" : null,
            ["SqlConnectionString"] = DeadSql,
        }).Build();

    private static CheckoutFulfillment Fulfill(IHttpClientFactory http, bool squareConfigured = true)
        => new(new SquareService(http, Cfg(squareConfigured), NullLogger<SquareService>.Instance),
               NullLogger<CheckoutFulfillment>.Instance);

    private static SquareFunction Square(IHttpClientFactory http, bool squareConfigured = true)
    {
        var cfg = Cfg(squareConfigured);
        var square = new SquareService(http, cfg, NullLogger<SquareService>.Instance);
        return new SquareFunction(new SqlService(cfg, NullLogger<SqlService>.Instance), square,
            new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance),
            NullLogger<SquareFunction>.Instance);
    }

    private static HttpRequest Req(string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Request.ContentType = "application/json";
        return ctx.Request;
    }

    // ---- RetireLinksAsync never throws --------------------------------------

    /// <summary>
    /// Square's delete blew up (its transport threw). The sale is already
    /// committed, so this is a log line and a job for Reconcile — never an
    /// exception back into a caller with nothing left to roll back.
    /// </summary>
    [Fact]
    public async Task A_square_delete_that_throws_is_swallowed()
    {
        var http = new ThrowingTransportFactory(() => new HttpRequestException("Square is down"));
        await using var conn = new SqlConnection(DeadSql);
        await Fulfill(http).RetireLinksAsync(conn, new[] { new CanceledLink("ORD1", "LINK1") }, CancellationToken.None);
    }

    /// <summary>
    /// The other direction: nothing to delete at Square (a row with no link id is
    /// confirmed dead on sight), so the only work left is the link_deleted_at
    /// stamp — and the database is unreachable. Still not an exception.
    /// </summary>
    [Fact]
    public async Task A_stamp_that_cannot_reach_sql_is_swallowed()
    {
        await using var conn = new SqlConnection(DeadSql);
        await Fulfill(new ExplodingHttpClientFactory()).RetireLinksAsync(
            conn, new[] { new CanceledLink("ORD1", null) }, CancellationToken.None);
    }

    /// <summary>
    /// Square unconfigured: the link is left open for Reconcile to sweep. Neither
    /// Square nor the database is touched — the exploding factory and the closed
    /// port both stay quiet — and link_deleted_at is (correctly) never stamped.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_square_touches_neither_square_nor_sql()
    {
        var http = new ExplodingHttpClientFactory();
        await using var conn = new SqlConnection(DeadSql);
        await Fulfill(http, squareConfigured: false).RetireLinksAsync(
            conn, new[] { new CanceledLink("ORD1", "LINK1") }, CancellationToken.None);
        Assert.Equal(0, http.Calls);
    }

    /// <summary>
    /// A cancelled token must not escape either: a host shutdown mid-retire is
    /// exactly when the sale has already committed.
    /// </summary>
    [Fact]
    public async Task A_cancelled_token_does_not_escape()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var conn = new SqlConnection(DeadSql);
        await Fulfill(new ThrowingTransportFactory(() => new TaskCanceledException())).RetireLinksAsync(
            conn, new[] { new CanceledLink("ORD1", "LINK1") }, cts.Token);
    }

    /// <summary>No links, no work: not even a connection attempt.</summary>
    [Fact]
    public async Task An_empty_list_is_a_no_op()
    {
        var http = new ExplodingHttpClientFactory();
        await using var conn = new SqlConnection(DeadSql);
        await Fulfill(http).RetireLinksAsync(conn, Array.Empty<CanceledLink>(), CancellationToken.None);
        Assert.Equal(0, http.Calls);
    }

    // ---- InvoiceBox refuses before it opens a transaction -------------------

    [Fact]
    public async Task Invoicing_with_square_off_is_a_503_before_any_sql()
    {
        var http = new ExplodingHttpClientFactory();
        var r = Assert.IsType<ObjectResult>(await Square(http, squareConfigured: false)
            .InvoiceBox(Req("{\"email\":\"buyer@example.com\"}"), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(503, r.StatusCode);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"email\":\"  \"}")]
    [InlineData("{\"email\":\"nobody-at-example.com\"}")]
    public async Task A_junk_invoice_request_is_a_400_before_any_sql(string body)
    {
        var http = new ExplodingHttpClientFactory();
        Assert.IsType<BadRequestObjectResult>(
            await Square(http).InvoiceBox(Req(body), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, http.Calls);
    }
}
