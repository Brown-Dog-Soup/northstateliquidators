using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// Task 9: SQUARE_{SANDBOX|PROD}_WEBHOOK_SIGNATURE_KEY / _WEBHOOK_URL, each
/// falling back to the unsuffixed name. Tested indirectly through
/// VerifyWebhookSignature, the only public surface that reads the private
/// _webhookSignatureKey/_webhookUrl fields the constructor sets — a wrong
/// selection shows up as a signature computed for one environment failing to
/// verify against the service built for the other.
/// </summary>
public class SquareServiceConfigTests
{
    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("no Square call expected in a config-only test");
    }

    private static SquareService Build(Dictionary<string, string?> settings)
    {
        var baseline = new Dictionary<string, string?>
        {
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "sandbox-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC-SANDBOX",
            ["SQUARE_PROD_ACCESS_TOKEN"] = "prod-token",
            ["SQUARE_PROD_LOCATION_ID"] = "LOC-PROD",
        };
        foreach (var kv in settings) baseline[kv.Key] = kv.Value;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(baseline).Build();
        return new SquareService(new NoOpHttpClientFactory(), cfg, NullLogger<SquareService>.Instance);
    }

    private static string Sign(string key, string url, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(url + body)));
    }

    [Fact]
    public void Sandbox_environment_with_no_suffixed_settings_falls_back_to_the_unsuffixed_names()
    {
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "shared-key",
            ["SQUARE_WEBHOOK_URL"] = "https://nsl.example/webhook",
        });
        var sig = Sign("shared-key", "https://nsl.example/webhook", "{}");
        Assert.True(square.VerifyWebhookSignature("{}", sig));
    }

    [Fact]
    public void Production_environment_with_no_suffixed_settings_falls_back_to_the_unsuffixed_names()
    {
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "production",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "shared-key",
            ["SQUARE_WEBHOOK_URL"] = "https://nsl.example/webhook",
        });
        var sig = Sign("shared-key", "https://nsl.example/webhook", "{}");
        Assert.True(square.VerifyWebhookSignature("{}", sig));
    }

    [Fact]
    public void Sandbox_environment_prefers_the_SANDBOX_suffixed_settings_over_the_unsuffixed_fallback()
    {
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_SANDBOX_WEBHOOK_SIGNATURE_KEY"] = "sandbox-key",
            ["SQUARE_SANDBOX_WEBHOOK_URL"] = "https://sandbox.example/webhook",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "prod-key",
            ["SQUARE_WEBHOOK_URL"] = "https://prod.example/webhook",
        });
        var sandboxSig = Sign("sandbox-key", "https://sandbox.example/webhook", "{}");
        var fallbackSig = Sign("prod-key", "https://prod.example/webhook", "{}");

        Assert.True(square.VerifyWebhookSignature("{}", sandboxSig));
        Assert.False(square.VerifyWebhookSignature("{}", fallbackSig));
    }

    [Fact]
    public void Production_environment_prefers_the_PROD_suffixed_settings_over_the_unsuffixed_fallback()
    {
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "production",
            ["SQUARE_PROD_WEBHOOK_SIGNATURE_KEY"] = "prod-key",
            ["SQUARE_PROD_WEBHOOK_URL"] = "https://prod.example/webhook",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "unsuffixed-key",
            ["SQUARE_WEBHOOK_URL"] = "https://unsuffixed.example/webhook",
        });
        var prodSig = Sign("prod-key", "https://prod.example/webhook", "{}");
        var fallbackSig = Sign("unsuffixed-key", "https://unsuffixed.example/webhook", "{}");

        Assert.True(square.VerifyWebhookSignature("{}", prodSig));
        Assert.False(square.VerifyWebhookSignature("{}", fallbackSig));
    }

    [Fact]
    public void Production_environment_does_not_pick_up_a_SANDBOX_suffixed_setting()
    {
        // Guards the {env} substitution itself: a PROD-environment service must
        // never resolve a SANDBOX-suffixed key, even when it is the only
        // suffixed setting present alongside the shared unsuffixed fallback.
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "production",
            ["SQUARE_SANDBOX_WEBHOOK_SIGNATURE_KEY"] = "sandbox-only-key",
            ["SQUARE_SANDBOX_WEBHOOK_URL"] = "https://sandbox-only.example/webhook",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "shared-key",
            ["SQUARE_WEBHOOK_URL"] = "https://shared.example/webhook",
        });
        var sandboxOnlySig = Sign("sandbox-only-key", "https://sandbox-only.example/webhook", "{}");
        var sharedSig = Sign("shared-key", "https://shared.example/webhook", "{}");

        Assert.False(square.VerifyWebhookSignature("{}", sandboxOnlySig));
        Assert.True(square.VerifyWebhookSignature("{}", sharedSig));
    }

    [Fact]
    public void The_signature_key_and_the_url_fall_back_independently()
    {
        // Only the URL is per-environment here; the signature key is only set
        // unsuffixed. Each setting must resolve on its own, not as a pair.
        var square = Build(new()
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_SANDBOX_WEBHOOK_URL"] = "https://sandbox.example/webhook",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = "shared-key",
        });
        var sig = Sign("shared-key", "https://sandbox.example/webhook", "{}");
        Assert.True(square.VerifyWebhookSignature("{}", sig));
    }
}
