using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

public class MailServiceTests
{
    private sealed class NoHttp : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Calls++; throw new InvalidOperationException("HTTP must not be used"); }
    }

    /// <summary>Always hands back a live-looking token — lets tests reach the HTTP send path without a real credential.</summary>
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Simulates a bad/expired secret — every token request fails, no network involved.</summary>
    private sealed class FakeThrowingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("bad secret");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new AuthenticationFailedException("bad secret");
    }

    /// <summary>Records every request it receives and hands back a scripted sequence of responses, one per request.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public ScriptedHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0) throw new InvalidOperationException("ScriptedHandler: no more scripted responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private static MailService Build(params (string, string?)[] settings)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
    }

    /// <summary>MAIL_ENABLED + MAIL_FROM only — no MAIL_CLIENT_*; the credential-seam ctor supplies the credential directly.</summary>
    private static MailService BuildScripted(ScriptedHandler handler, TokenCredential? credential = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MAIL_ENABLED"] = "true",
            ["MAIL_FROM"] = "hello@northstateliquidators.com",
        }).Build();
        return new MailService(new StubHttpClientFactory(handler), cfg, NullLogger<MailService>.Instance, credential ?? new FakeCredential());
    }

    [Fact]
    public void Payload_MatchesGraphSendMailShape()
    {
        var payload = MailService.BuildSendMailPayload("to@example.com", "Sam", "Subj", "<p>hi</p>", "hello@northstateliquidators.com");
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"toRecipients\":[", json);   // an ARRAY, never a collapsed object
        Assert.Contains("\"replyTo\":[", json);
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("message");
        Assert.Equal("Subj", msg.GetProperty("subject").GetString());
        Assert.Equal("HTML", msg.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("<p>hi</p>", msg.GetProperty("body").GetProperty("content").GetString());
        var to = msg.GetProperty("toRecipients")[0].GetProperty("emailAddress");
        Assert.Equal("to@example.com", to.GetProperty("address").GetString());
        Assert.Equal("Sam", to.GetProperty("name").GetString());
        Assert.Equal("hello@northstateliquidators.com", msg.GetProperty("replyTo")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public void Payload_OmitsSaveToSentItems()
    {
        var json = JsonSerializer.Serialize(MailService.BuildSendMailPayload("a@b.c", "A", "s", "h", "r@b.c"));
        Assert.DoesNotContain("saveToSentItems", json);
    }

    [Fact]
    public void DisabledByDefault_NoHttp()
    {
        var svc = Build(("MAIL_FROM", "hello@northstateliquidators.com"));
        Assert.False(svc.Enabled);
        Assert.False(svc.Configured);
    }

    [Fact]
    public async Task SendMemberWelcome_WhenDisabled_ReturnsFalseWithoutHttp()
    {
        var http = new NoHttp();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MAIL_ENABLED"] = "false", ["MAIL_FROM"] = "hello@northstateliquidators.com" }).Build();
        var svc = new MailService(http, cfg, NullLogger<MailService>.Instance);
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);
        Assert.False(sent);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public void Configured_RequiresEnabledAndFrom()
    {
        Assert.True(Build(("MAIL_ENABLED", "true"), ("MAIL_FROM", "hello@northstateliquidators.com")).Configured);
        Assert.False(Build(("MAIL_ENABLED", "true")).Configured);
    }

    [Fact]
    public async Task SendMemberWelcome_TokenFailure_ReturnsFalse()
    {
        var http = new NoHttp();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MAIL_ENABLED"] = "true", ["MAIL_FROM"] = "hello@northstateliquidators.com" }).Build();
        var svc = new MailService(http, cfg, NullLogger<MailService>.Instance, new FakeThrowingCredential());
        // Token acquisition fails before any HTTP is touched; auth failures never retry.
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);
        Assert.False(sent);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task SendMemberWelcome_WithCancelledToken_ReturnsFalse()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MAIL_ENABLED"] = "true", ["MAIL_FROM"] = "hello@northstateliquidators.com",
            ["MAIL_TENANT_ID"] = "00000000-0000-0000-0000-000000000000",
            ["MAIL_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001", ["MAIL_CLIENT_SECRET"] = "x"
        }).Build();
        var svc = new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Real cancellation must still come back as false, not propagate — the
        // "never throws" contract on SendMemberWelcomeAsync is unconditional.
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), cts.Token);
        Assert.False(sent);
    }

    [Fact]
    public async Task Send_202_ReturnsTrue_PostsToEncodedFromUrl()
    {
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        var svc = BuildScripted(handler);

        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);

        Assert.True(sent);
        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal("https://graph.microsoft.com/v1.0/users/hello%40northstateliquidators.com/sendMail", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("t", request.Headers.Authorization!.Parameter);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"toRecipients\":[", body);
    }

    [Fact]
    public async Task Send_403_NoRetry()
    {
        var body = "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access to OData is disabled.\"}}";
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
        var svc = BuildScripted(handler);

        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);

        Assert.False(sent);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_500_ThenRetriesOnce()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") },
            new HttpResponseMessage(HttpStatusCode.Accepted));
        var svc = BuildScripted(handler);

        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Send_429_CapturesRetryAfter()
    {
        static HttpResponseMessage TooManyRequests()
        {
            var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        }

        var handler = new ScriptedHandler(TooManyRequests(), TooManyRequests());
        var svc = BuildScripted(handler);

        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);

        Assert.False(sent);
        Assert.Equal(2, handler.Requests.Count);   // the second is the single retry

        var directHandler = new ScriptedHandler(TooManyRequests());
        var directSvc = BuildScripted(directHandler);
        var result = await directSvc.SendAsync("sam@example.com", "Sam", "s", "h", CancellationToken.None);
        Assert.Contains("retry-after 7", result.Error);
    }

    [Fact]
    public void RedactError_KeepsGraphCodeButHidesRecipientAddress()
    {
        var body = "{\"error\":{\"code\":\"ErrorInvalidRecipients\",\"message\":\"Recipient sam@example.com is invalid\"}}";
        var result = MailService.RedactError(body, "sam@example.com");
        Assert.Contains("ErrorInvalidRecipients", result);
        Assert.Contains("[recipient]", result);
        Assert.DoesNotContain("sam@example.com", result);
    }

    [Fact]
    public void RedactError_NonJsonBody_UsesFallbackAndRedacts()
    {
        var result = MailService.RedactError("Bad Gateway from proxy for sam@example.com", "sam@example.com", "502 Bad Gateway");
        Assert.True(result.Contains("502") || result.Contains("[recipient]"));
        Assert.DoesNotContain("sam@example.com", result);
    }
}
