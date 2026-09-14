using System.Text.Json;
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

    private static MailService Build(params (string, string?)[] settings)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
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
    public async Task SendMemberWelcome_NeverThrows_WhenHttpFails()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MAIL_ENABLED"] = "true", ["MAIL_FROM"] = "hello@northstateliquidators.com",
            ["MAIL_TENANT_ID"] = "00000000-0000-0000-0000-000000000000",
            ["MAIL_CLIENT_ID"] = "00000000-0000-0000-0000-000000000001", ["MAIL_CLIENT_SECRET"] = "x"
        }).Build();
        var svc = new MailService(new NoHttp(), cfg, NullLogger<MailService>.Instance);
        // Token acquisition or HTTP will fail (bogus tenant / throwing factory); must return false, not throw.
        var sent = await svc.SendMemberWelcomeAsync(new MemberMail("2600001", "Sam", "sam@example.com"), CancellationToken.None);
        Assert.False(sent);
    }
}
