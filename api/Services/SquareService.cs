using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NSL.Api.Services;

/// <summary>
/// Thin REST client for the Square Checkout/Orders APIs (SQUARE-INTEGRATION.md).
/// Deliberately plain HttpClient rather than the Square SDK — the surface is
/// three endpoints plus one HMAC, and this repo compiles only in CI.
///
/// Config (SWA application settings):
///   SQUARE_ENVIRONMENT            sandbox | production   (default sandbox)
///   SQUARE_SANDBOX_ACCESS_TOKEN / SQUARE_PROD_ACCESS_TOKEN
///   SQUARE_SANDBOX_LOCATION_ID  / SQUARE_PROD_LOCATION_ID
///   SQUARE_CHECKOUT_ENABLED       "true" to allow link creation (kill switch)
///   SQUARE_WEBHOOK_SIGNATURE_KEY  from the webhook subscription
///   SQUARE_WEBHOOK_URL            the exact notification URL registered with
///                                 Square — the HMAC signs url+body, so this
///                                 must match character-for-character
/// </summary>
public sealed class SquareService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SquareService> _log;

    private const string ApiVersion = "2025-01-23";

    public bool   IsProduction { get; }
    public bool   CheckoutEnabled { get; }
    public string BaseUrl { get; }
    public string LocationId { get; }
    private readonly string _token;
    private readonly string _webhookSignatureKey;
    private readonly string _webhookUrl;

    public bool Configured => !string.IsNullOrEmpty(_token) && !string.IsNullOrEmpty(LocationId);

    public SquareService(IHttpClientFactory http, IConfiguration cfg, ILogger<SquareService> log)
    {
        _http = http;
        _log = log;
        IsProduction = string.Equals(cfg["SQUARE_ENVIRONMENT"], "production", StringComparison.OrdinalIgnoreCase);
        BaseUrl = IsProduction ? "https://connect.squareup.com" : "https://connect.squareupsandbox.com";
        _token     = (IsProduction ? cfg["SQUARE_PROD_ACCESS_TOKEN"] : cfg["SQUARE_SANDBOX_ACCESS_TOKEN"]) ?? "";
        LocationId = (IsProduction ? cfg["SQUARE_PROD_LOCATION_ID"]  : cfg["SQUARE_SANDBOX_LOCATION_ID"])  ?? "";
        CheckoutEnabled = string.Equals(cfg["SQUARE_CHECKOUT_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
        _webhookSignatureKey = cfg["SQUARE_WEBHOOK_SIGNATURE_KEY"] ?? "";
        _webhookUrl          = cfg["SQUARE_WEBHOOK_URL"] ?? "";
    }

    private HttpClient Client()
    {
        var c = _http.CreateClient();
        c.BaseAddress = new Uri(BaseUrl);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        c.DefaultRequestHeaders.Add("Square-Version", ApiVersion);
        return c;
    }

    public sealed record PaymentLink(string Id, string OrderId, string Url);

    /// <summary>
    /// Quick Pay payment link for one box. Deterministic idempotency key means
    /// a double-clicked Buy (or a retried request) replays the original link
    /// instead of minting a second one.
    /// </summary>
    public async Task<PaymentLink> CreatePaymentLinkAsync(
        string name, long amountCents, string redirectUrl, string idempotencyKey,
        string? note, CancellationToken ct)
    {
        var payload = new
        {
            idempotency_key = idempotencyKey,
            quick_pay = new
            {
                name,
                price_money = new { amount = amountCents, currency = "USD" },
                location_id = LocationId
            },
            checkout_options = new { redirect_url = redirectUrl },
            payment_note = note
        };
        using var client = Client();
        var resp = await client.PostAsync("/v2/online-checkout/payment-links",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square CreatePaymentLink failed {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square CreatePaymentLink -> {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        var link = doc.RootElement.GetProperty("payment_link");
        return new PaymentLink(
            link.GetProperty("id").GetString()!,
            link.GetProperty("order_id").GetString()!,
            link.GetProperty("url").GetString()!);
    }

    /// <summary>Returns the raw order JSON, or null on 404.</summary>
    public async Task<JsonDocument?> RetrieveOrderAsync(string orderId, CancellationToken ct)
    {
        using var client = Client();
        var resp = await client.GetAsync($"/v2/orders/{Uri.EscapeDataString(orderId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square RetrieveOrder {OrderId} failed {Status}: {Body}", orderId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square RetrieveOrder -> {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// "Paid" for a payment-link order. GOTCHA (Square docs): paid link orders
    /// go DRAFT -> OPEN and stay OPEN forever — never test state=="COMPLETED".
    /// Paid = tenders exist, or net_amount_due_money is zero.
    /// </summary>
    public static bool IsOrderPaid(JsonElement order)
    {
        if (order.TryGetProperty("tenders", out var tenders) &&
            tenders.ValueKind == JsonValueKind.Array && tenders.GetArrayLength() > 0)
            return true;
        if (order.TryGetProperty("net_amount_due_money", out var due) &&
            due.TryGetProperty("amount", out var amt) && amt.GetInt64() == 0)
            return true;
        return false;
    }

    /// <summary>Delete (deactivate) a payment link; cancels its unpaid order. 404 is fine.</summary>
    public async Task DeletePaymentLinkAsync(string linkId, CancellationToken ct)
    {
        using var client = Client();
        var resp = await client.DeleteAsync($"/v2/online-checkout/payment-links/{Uri.EscapeDataString(linkId)}", ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("Square DeletePaymentLink {LinkId} failed {Status}: {Body}", linkId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square DeletePaymentLink -> {(int)resp.StatusCode}");
        }
    }

    /// <summary>
    /// Verify Square's webhook HMAC: base64(HMACSHA256(signatureKey,
    /// notificationUrl + rawBody)) must equal the x-square-hmacsha256-signature
    /// header. Constant-time comparison. Returns false when the subscription
    /// isn't configured yet — callers must treat that as reject.
    /// </summary>
    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(_webhookSignatureKey) || string.IsNullOrEmpty(_webhookUrl) ||
            string.IsNullOrEmpty(signatureHeader))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSignatureKey));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(_webhookUrl + rawBody)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(signatureHeader));
    }
}
