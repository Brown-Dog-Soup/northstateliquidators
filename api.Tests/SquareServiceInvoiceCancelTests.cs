using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// CancelInvoiceAsync, over the wire, because its contract is entirely about which
/// Square answers count as success.
///
/// The failure it exists to prevent: CancelBoxInvoice calls Square first and only
/// then, in one transaction, closes the order row and clears manifests.invoice_id.
/// If that transaction fails, Square has cancelled and our box still carries the id.
/// The operator retries. While this method threw on an invoice Square had already
/// cancelled, that retry died before reaching any SQL and the box stayed blocked
/// behind a stale invoice_id with no route able to clear it.
///
/// The opposite mistake is worse and is pinned here too: a PAID invoice's refusal
/// must still throw, because the caller's next move is to clear invoice_id, and
/// doing that on a paid invoice un-links a box from real money.
/// </summary>
public class SquareServiceInvoiceCancelTests
{
    /// <summary>
    /// Answers a scripted reply per request and records the method+path of each,
    /// so a test can assert that the POST was never sent at all.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string? Body)> _replies;
        public readonly List<string> Requests = new();

        public ScriptedHandler(params (HttpStatusCode, string?)[] replies) => _replies = new(replies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            Assert.True(_replies.Count > 0, $"Unscripted request: {Requests[^1]}");
            var (status, body) = _replies.Dequeue();
            var resp = new HttpResponseMessage(status);
            if (body != null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private static SquareService Build(HttpMessageHandler handler)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
        }).Build();
        return new SquareService(new StubHttpClientFactory(handler), cfg, NullLogger<SquareService>.Instance);
    }

    private static string Invoice(string status, int version = 3)
        => $"{{\"invoice\":{{\"id\":\"INV1\",\"version\":{version},\"status\":\"{status}\"}}}}";

    private static Task Cancel(ScriptedHandler h) => Build(h).CancelInvoiceAsync("INV1", CancellationToken.None);

    [Fact]
    public async Task An_unpaid_invoice_is_fetched_then_cancelled()
    {
        var h = new ScriptedHandler(
            (HttpStatusCode.OK, Invoice("UNPAID")),
            (HttpStatusCode.OK, "{\"invoice\":{\"id\":\"INV1\",\"status\":\"CANCELED\"}}"));
        await Cancel(h);
        Assert.Equal(new[] { "GET /v2/invoices/INV1", "POST /v2/invoices/INV1/cancel" }, h.Requests);
    }

    /// <summary>
    /// THE DEAD END, closed. Square already holds it as CANCELED — this is the
    /// retry of a cancel whose database half failed. It must not throw, and it must
    /// not even send the POST, because that POST is what used to throw.
    /// </summary>
    [Fact]
    public async Task An_invoice_square_already_cancelled_is_success_and_sends_no_cancel()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, Invoice("CANCELED")));
        await Cancel(h);
        Assert.Equal(new[] { "GET /v2/invoices/INV1" }, h.Requests);
    }

    [Fact]
    public async Task The_status_check_is_case_insensitive()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, Invoice("canceled")));
        await Cancel(h);
        Assert.Single(h.Requests);
    }

    /// <summary>
    /// Square holds no such invoice, so nothing is payable against it and our row
    /// pointing at it is exactly the stuck state the caller needs to clear.
    /// </summary>
    [Fact]
    public async Task An_invoice_square_has_never_heard_of_is_success()
    {
        var h = new ScriptedHandler((HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"NOT_FOUND\"}]}"));
        await Cancel(h);
        Assert.Equal(new[] { "GET /v2/invoices/INV1" }, h.Requests);
    }

    /// <summary>
    /// The refusal that must NEVER be swallowed. Square's message for it contains
    /// the word "canceled", which is why the verdict is read off the status field
    /// and never off the error body.
    /// </summary>
    [Fact]
    public async Task A_paid_invoice_still_throws()
    {
        var h = new ScriptedHandler(
            (HttpStatusCode.OK, Invoice("PAID")),
            (HttpStatusCode.BadRequest, "{\"errors\":[{\"code\":\"BAD_REQUEST\",\"detail\":\"Invoice with status PAID cannot be canceled.\"}]}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cancel(h));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task A_cancel_that_square_refuses_for_any_other_reason_still_throws()
    {
        var h = new ScriptedHandler(
            (HttpStatusCode.OK, Invoice("UNPAID")),
            (HttpStatusCode.Conflict, "{\"errors\":[{\"code\":\"VERSION_MISMATCH\"}]}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cancel(h));
    }

    /// <summary>A Square outage is not "already cancelled" and must not read as done.</summary>
    [Fact]
    public async Task A_failed_fetch_still_throws()
    {
        var h = new ScriptedHandler((HttpStatusCode.InternalServerError, "{\"errors\":[]}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cancel(h));
        Assert.Single(h.Requests);
    }

    /// <summary>
    /// No status field at all: unknown is not cancelled, so the cancel is still
    /// attempted rather than being reported as already done.
    /// </summary>
    [Fact]
    public async Task An_invoice_with_no_status_field_is_still_cancelled()
    {
        var h = new ScriptedHandler(
            (HttpStatusCode.OK, "{\"invoice\":{\"id\":\"INV1\",\"version\":7}}"),
            (HttpStatusCode.OK, "{}"));
        await Cancel(h);
        Assert.Equal(2, h.Requests.Count);
    }

    /// <summary>
    /// The reordered read: status is checked with the never-throwing accessor
    /// BEFORE version is touched with the throwing one. An already-CANCELED
    /// invoice that (hypothetically) omits version must still succeed and must
    /// still send no POST, because the early return happens before version is
    /// ever read.
    /// </summary>
    [Fact]
    public async Task An_already_cancelled_invoice_with_no_version_field_still_succeeds()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, "{\"invoice\":{\"id\":\"INV1\",\"status\":\"CANCELED\"}}"));
        await Cancel(h);
        Assert.Equal(new[] { "GET /v2/invoices/INV1" }, h.Requests);
    }

    /// <summary>
    /// An invoice that is NOT already cancelled and (hypothetically) omits
    /// version still needs it to build the cancel request, so this is the one
    /// path where the throwing accessor is still reached — deliberately not
    /// swallowed into a softer failure, since every real fixture supplies
    /// version and a caller seeing this is looking at a malformed Square
    /// response worth surfacing loudly.
    /// </summary>
    [Fact]
    public async Task An_unpaid_invoice_with_no_version_field_throws_before_sending_a_cancel()
    {
        var h = new ScriptedHandler((HttpStatusCode.OK, "{\"invoice\":{\"id\":\"INV1\",\"status\":\"UNPAID\"}}"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Cancel(h));
        Assert.Equal(new[] { "GET /v2/invoices/INV1" }, h.Requests);
    }

    [Theory]
    [InlineData("UNPAID", false)]
    [InlineData("DRAFT", false)]
    [InlineData("PAID", false)]
    [InlineData("PARTIALLY_PAID", false)]
    [InlineData("REFUNDED", false)]
    [InlineData("FAILED", false)]
    [InlineData(null, false)]
    [InlineData("CANCELED", true)]
    public void Only_CANCELED_counts_as_already_done(string? status, bool expected)
        => Assert.Equal(expected, SquareService.InvoiceAlreadyCanceled(status));
}
