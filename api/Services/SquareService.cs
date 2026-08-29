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
    /// Find a Square customer by exact email or create a minimal profile.
    /// EMAIL invoice delivery requires the recipient to exist in the Customer
    /// Directory with an email address.
    /// </summary>
    public async Task<string> FindOrCreateCustomerAsync(string email, string? name, CancellationToken ct)
    {
        using var client = Client();
        var search = new { query = new { filter = new { email_address = new { exact = email } } }, limit = 1 };
        var sResp = await client.PostAsync("/v2/customers/search",
            new StringContent(JsonSerializer.Serialize(search), Encoding.UTF8, "application/json"), ct);
        var sBody = await sResp.Content.ReadAsStringAsync(ct);
        if (sResp.IsSuccessStatusCode)
        {
            using var sDoc = JsonDocument.Parse(sBody);
            if (sDoc.RootElement.TryGetProperty("customers", out var cs) &&
                cs.ValueKind == JsonValueKind.Array && cs.GetArrayLength() > 0)
                return cs[0].GetProperty("id").GetString()!;
        }

        var create = new { email_address = email, company_name = string.IsNullOrWhiteSpace(name) ? null : name };
        var cResp = await client.PostAsync("/v2/customers",
            new StringContent(JsonSerializer.Serialize(create), Encoding.UTF8, "application/json"), ct);
        var cBody = await cResp.Content.ReadAsStringAsync(ct);
        if (!cResp.IsSuccessStatusCode)
        {
            _log.LogError("Square CreateCustomer failed {Status}: {Body}", (int)cResp.StatusCode, cBody);
            throw new InvalidOperationException($"Square CreateCustomer -> {(int)cResp.StatusCode}");
        }
        using var cDoc = JsonDocument.Parse(cBody);
        return cDoc.RootElement.GetProperty("customer").GetProperty("id").GetString()!;
    }

    public sealed record InvoiceResult(string InvoiceId, string OrderId, string? PublicUrl, string Status);

    /// <summary>
    /// Ad-hoc invoice for one box: create Order (line-item quantity is a
    /// STRING per Square) -> create DRAFT invoice (BALANCE payment request,
    /// due date required, card + ACH accepted) -> publish (version 0), which
    /// emails the buyer immediately with delivery_method EMAIL.
    /// </summary>
    public async Task<InvoiceResult> CreateInvoiceAsync(
        string itemName, long amountCents, string customerId, string title,
        string invoiceNumber, CancellationToken ct)
    {
        using var client = Client();

        var orderPayload = new
        {
            idempotency_key = Guid.NewGuid().ToString(),
            order = new
            {
                location_id = LocationId,
                line_items = new[] { new { name = itemName, quantity = "1",
                    base_price_money = new { amount = amountCents, currency = "USD" } } }
            }
        };
        var oResp = await client.PostAsync("/v2/orders",
            new StringContent(JsonSerializer.Serialize(orderPayload), Encoding.UTF8, "application/json"), ct);
        var oBody = await oResp.Content.ReadAsStringAsync(ct);
        if (!oResp.IsSuccessStatusCode)
        {
            _log.LogError("Square CreateOrder failed {Status}: {Body}", (int)oResp.StatusCode, oBody);
            throw new InvalidOperationException($"Square CreateOrder -> {(int)oResp.StatusCode}");
        }
        string orderId;
        using (var oDoc = JsonDocument.Parse(oBody))
            orderId = oDoc.RootElement.GetProperty("order").GetProperty("id").GetString()!;

        var invoicePayload = new
        {
            idempotency_key = Guid.NewGuid().ToString(),
            invoice = new
            {
                location_id = LocationId,
                order_id = orderId,
                primary_recipient = new { customer_id = customerId },
                payment_requests = new[] { new { request_type = "BALANCE",
                    due_date = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd") } },
                delivery_method = "EMAIL",
                accepted_payment_methods = new { card = true, bank_account = true,
                    square_gift_card = false, buy_now_pay_later = false, cash_app_pay = true },
                title,
                invoice_number = invoiceNumber
            }
        };
        var iResp = await client.PostAsync("/v2/invoices",
            new StringContent(JsonSerializer.Serialize(invoicePayload), Encoding.UTF8, "application/json"), ct);
        var iBody = await iResp.Content.ReadAsStringAsync(ct);
        if (!iResp.IsSuccessStatusCode)
        {
            _log.LogError("Square CreateInvoice failed {Status}: {Body}", (int)iResp.StatusCode, iBody);
            throw new InvalidOperationException($"Square CreateInvoice -> {(int)iResp.StatusCode}");
        }
        string invoiceId; int version;
        using (var iDoc = JsonDocument.Parse(iBody))
        {
            var inv = iDoc.RootElement.GetProperty("invoice");
            invoiceId = inv.GetProperty("id").GetString()!;
            version = inv.GetProperty("version").GetInt32();
        }

        var pubPayload = new { version, idempotency_key = Guid.NewGuid().ToString() };
        var pResp = await client.PostAsync($"/v2/invoices/{Uri.EscapeDataString(invoiceId)}/publish",
            new StringContent(JsonSerializer.Serialize(pubPayload), Encoding.UTF8, "application/json"), ct);
        var pBody = await pResp.Content.ReadAsStringAsync(ct);
        if (!pResp.IsSuccessStatusCode)
        {
            _log.LogError("Square PublishInvoice {InvoiceId} failed {Status}: {Body}", invoiceId, (int)pResp.StatusCode, pBody);
            throw new InvalidOperationException($"Square PublishInvoice -> {(int)pResp.StatusCode}");
        }
        using var pDoc = JsonDocument.Parse(pBody);
        var pubInv = pDoc.RootElement.GetProperty("invoice");
        return new InvoiceResult(invoiceId, orderId,
            pubInv.TryGetProperty("public_url", out var pu) ? pu.GetString() : null,
            pubInv.TryGetProperty("status", out var st) ? st.GetString() ?? "UNPAID" : "UNPAID");
    }

    /// <summary>Cancel an unpaid invoice (fetches current version first).</summary>
    public async Task CancelInvoiceAsync(string invoiceId, CancellationToken ct)
    {
        using var client = Client();
        var gResp = await client.GetAsync($"/v2/invoices/{Uri.EscapeDataString(invoiceId)}", ct);
        var gBody = await gResp.Content.ReadAsStringAsync(ct);
        if (!gResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Square GetInvoice -> {(int)gResp.StatusCode}");
        int version;
        using (var gDoc = JsonDocument.Parse(gBody))
            version = gDoc.RootElement.GetProperty("invoice").GetProperty("version").GetInt32();

        var cResp = await client.PostAsync($"/v2/invoices/{Uri.EscapeDataString(invoiceId)}/cancel",
            new StringContent(JsonSerializer.Serialize(new { version }), Encoding.UTF8, "application/json"), ct);
        if (!cResp.IsSuccessStatusCode)
        {
            var cBody = await cResp.Content.ReadAsStringAsync(ct);
            _log.LogError("Square CancelInvoice {InvoiceId} failed {Status}: {Body}", invoiceId, (int)cResp.StatusCode, cBody);
            throw new InvalidOperationException($"Square CancelInvoice -> {(int)cResp.StatusCode}");
        }
    }

    /// <summary>
    /// Full refund of a payment. Idempotency key derived from the payment id,
    /// so a double-clicked Refund button can't refund twice.
    /// </summary>
    public async Task<JsonDocument> RefundPaymentAsync(string paymentId, long amountCents, string? reason, CancellationToken ct)
    {
        var payload = new
        {
            idempotency_key = $"nsl-refund-{paymentId}",
            payment_id = paymentId,
            amount_money = new { amount = amountCents, currency = "USD" },
            reason = reason ?? "NSL admin refund"
        };
        using var client = Client();
        var resp = await client.PostAsync("/v2/refunds",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square RefundPayment {PaymentId} failed {Status}: {Body}", paymentId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square RefundPayment -> {(int)resp.StatusCode}");
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// All payments since <paramref name="begin"/> (floor + web — one account),
    /// newest first, paginated up to a sane cap. Feeds the sales dashboard.
    /// </summary>
    public async Task<List<JsonElement>> ListPaymentsAsync(DateTime beginUtc, CancellationToken ct)
        => await ListPagedAsync("/v2/payments", "payments", beginUtc, ct);

    /// <summary>Deposits to the bank (payouts), newest first.</summary>
    public async Task<List<JsonElement>> ListPayoutsAsync(DateTime beginUtc, CancellationToken ct)
        => await ListPagedAsync("/v2/payouts", "payouts", beginUtc, ct);

    private async Task<List<JsonElement>> ListPagedAsync(string path, string arrayField, DateTime beginUtc, CancellationToken ct)
    {
        var results = new List<JsonElement>();
        string? cursor = null;
        using var client = Client();
        for (int page = 0; page < 10; page++)   // cap: 10 x 100 rows
        {
            var url = $"{path}?begin_time={Uri.EscapeDataString(beginUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"))}&sort_order=DESC&limit=100&location_id={Uri.EscapeDataString(LocationId)}";
            if (cursor != null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("Square {Path} failed {Status}: {Body}", path, (int)resp.StatusCode, body);
                throw new InvalidOperationException($"Square {path} -> {(int)resp.StatusCode}");
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(arrayField, out var arr))
                foreach (var el in arr.EnumerateArray()) results.Add(el.Clone());
            cursor = doc.RootElement.TryGetProperty("cursor", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return results;
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
