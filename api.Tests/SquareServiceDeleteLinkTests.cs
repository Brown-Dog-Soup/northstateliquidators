using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// DeletePaymentLinkAsync is the one call whose entire premise is that Square
/// misbehaves on success responses, so these tests pin the contract from the
/// wire up: a 2xx must never throw, whatever the body is, because callers read
/// the bool as "confirmed dead / still open" and two of them
/// (SquareFunction.cs:262, :604) do not catch.
/// </summary>
public class SquareServiceDeleteLinkTests
{
    private sealed class OneResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        public int Calls;

        public OneResponseHandler(HttpStatusCode status, string? body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var resp = new HttpResponseMessage(_status);
            // null body => no content at all, which is not the same as "".
            if (_body != null) resp.Content = new StringContent(_body, Encoding.UTF8, "application/json");
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

    private static async Task<bool> Delete(HttpStatusCode status, string? body)
        => await Build(new OneResponseHandler(status, body)).DeletePaymentLinkAsync("LINK1", CancellationToken.None);

    [Fact]
    public async Task Confirmed_when_square_returns_a_cancelled_order_id()
        => Assert.True(await Delete(HttpStatusCode.OK, "{\"cancelled_order_id\":\"ORDER123\"}"));

    [Fact]
    public async Task Confirmed_when_the_link_is_already_gone()
        => Assert.True(await Delete(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"NOT_FOUND\"}]}"));

    [Fact]
    public async Task Confirmed_when_a_404_carries_no_body_at_all()
        => Assert.True(await Delete(HttpStatusCode.NotFound, null));

    [Theory]
    // Every one of these is a 2xx. None may throw; all mean "still open".
    [InlineData("")]                                   // empty body
    [InlineData("   ")]                                // whitespace body
    [InlineData("<html>Bad Gateway</html>")]           // not JSON at all
    [InlineData("{\"cancelled_order_id\":")]           // truncated JSON
    [InlineData("{}")]                                 // 200, no cancelled_order_id
    [InlineData("{\"cancelled_order_id\":null}")]      // present but null
    [InlineData("{\"cancelled_order_id\":\"\"}")]      // present but empty
    [InlineData("{\"cancelled_order_id\":12345}")]     // present but not a string
    [InlineData("[]")]                                 // valid JSON, not an object
    [InlineData("123")]                                // valid JSON scalar, not an object
    public async Task A_2xx_never_throws_and_is_only_confirmed_with_a_real_id(string body)
    {
        // TryGetProperty on a non-object JsonElement throws InvalidOperationException,
        // not JsonException — "[]" and "123" are here to keep that guard honest.
        var result = await Delete(HttpStatusCode.OK, body);
        Assert.False(result);
    }

    [Fact]
    public async Task A_2xx_with_no_content_never_throws()
        => Assert.False(await Delete(HttpStatusCode.NoContent, null));

    [Fact]
    public async Task A_real_failure_still_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Delete(HttpStatusCode.InternalServerError, "{\"errors\":[]}"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Delete(HttpStatusCode.Unauthorized, ""));
    }
}
