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
///   SQUARE_{SANDBOX|PROD}_WEBHOOK_SIGNATURE_KEY / _WEBHOOK_URL  per-environment
///                                 (fall back to the unsuffixed names)
///   SQUARE_PUBLIC_BASE_URL        site origin used in redirect URLs
///   SQUARE_SUPPORT_EMAIL          merchant_support_email on the hosted page
///   SQUARE_TAX_CATALOG_ID         the account's own NC/Wake sales-tax catalog
///                                 object; ABSENT falls back to the ad-hoc tax,
///                                 a WRONG value just fails the link create
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
    public string SupportEmail { get; }
    public string? TaxCatalogId { get; }
    /// <summary>Origin the buyer comes back to after paying. Preview slots and
    /// staging set this so the redirect doesn't bounce them to production.</summary>
    public string PublicBaseUrl { get; }
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
        string env = IsProduction ? "PROD" : "SANDBOX";
        _webhookSignatureKey = cfg[$"SQUARE_{env}_WEBHOOK_SIGNATURE_KEY"] ?? cfg["SQUARE_WEBHOOK_SIGNATURE_KEY"] ?? "";
        _webhookUrl          = cfg[$"SQUARE_{env}_WEBHOOK_URL"]           ?? cfg["SQUARE_WEBHOOK_URL"]           ?? "";
        SupportEmail = cfg["SQUARE_SUPPORT_EMAIL"] ?? "hello@northstateliquidators.com";
        TaxCatalogId = cfg["SQUARE_TAX_CATALOG_ID"];
        PublicBaseUrl = (cfg["SQUARE_PUBLIC_BASE_URL"] ?? "https://northstateliquidators.com").TrimEnd('/');
    }

    private HttpClient Client()
    {
        var c = _http.CreateClient();
        c.BaseAddress = new Uri(BaseUrl);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        c.DefaultRequestHeaders.Add("Square-Version", ApiVersion);
        return c;
    }

    /// <summary>Per-box tax as Square computed it, keyed by the line uid (= manifest id).</summary>
    public sealed record CartLink(string Id, string OrderId, string Url,
        long TotalCents, long TaxCents, long DeliveryCents, IReadOnlyDictionary<Guid, long> LineTaxCents);

    /// <summary>
    /// One payment link for N boxes: a full `order` with one ad-hoc line item
    /// per box (uid = manifest_id) instead of quick_pay, plus the 7.25% NC tax
    /// and — for delivery orders — the caller's per-zip delivery service charge
    /// (dbo.delivery_zips.fee_cents, never a constant). Idempotency key is
    /// per attempt (nsl-cart-{guid}); reuse is decided by our DB, not Square.
    /// Every money figure comes back out of Square's own order totals so our
    /// arithmetic can never disagree with what the buyer is charged, and the
    /// per-line tax means a partial refund can return that box's tax exactly.
    /// That is now a guarantee rather than an aspiration: there is no local
    /// fallback left. If Square neither embeds nor returns an order carrying a
    /// total, a tax and a tax for every line we sent, this THROWS and the sale
    /// is refused — see the long note at the retrieval below.
    /// </summary>
    public async Task<CartLink> CreateCartPaymentLinkAsync(IReadOnlyList<CartLine> lines, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, DeliveryMethod delivery,
        long deliveryFeeCents, CancellationToken ct)
    {
        // deliveryFeeCents is what we ASK Square to charge; the delivery figure
        // read off Square's order below is what it says it DID charge. Distinct
        // names on purpose — confusing the two here is a money bug.
        var payload = SquarePayloads.CartLink(lines, LocationId, redirectUrl, idempotencyKey, referenceId, paymentNote, SupportEmail, delivery, TaxCatalogId, deliveryFeeCents);
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
        string linkId  = link.GetProperty("id").GetString()!;
        string orderId = link.GetProperty("order_id").GetString()!;
        string url     = link.GetProperty("url").GetString()!;

        // Square usually embeds the order it just created. When it does not — or
        // embeds one it has not priced — ASK for it, and refuse the sale if that
        // fails too.
        //
        // THE FALLBACK THIS REPLACED, because it read as harmless and was not.
        // It summed OUR line asks into the total, recorded tax and delivery as 0
        // and every per-box tax as 0, while Square went on charging the buyer
        // goods + 7.25% + any delivery fee. Every later rule reads those columns
        // as fact: one box unavailable refunded its price and no tax; nothing
        // sold refunded goods only, keeping the buyer's tax AND a delivery fee
        // for a delivery that never happens; the sales-tax figure the owner
        // reports to NCDOR under-stated what was collected. And it was silent —
        // the goods-versus-subtotal backstop cannot see it (both sides shrink
        // together) and needs_refund CLEARS once refunded_cents reaches the
        // under-paid refund_due_cents (CheckoutFulfillment.ApplyRefundSql), so
        // the row went green and nobody ever learned.
        //
        // An order we cannot price is an order we cannot refund correctly, so
        // the honest answer is not to take the money: the caller turns this
        // throw into the same 502 "call us at (919) 526-0112" the shopper gets
        // for any other Square failure (SquareFunction.TryCreateCartLinkAsync).
        // A phone call costs one sale; a mispriced order costs a refund nobody
        // knows is owed.
        var order = doc.RootElement.TryGetProperty("related_resources", out var rr) &&
                    rr.TryGetProperty("orders", out var orders) && orders.GetArrayLength() > 0
            ? ReadOrder(orders[0])
            : null;
        if (!IsPriced(order, lines))
        {
            _log.LogWarning("Square CreatePaymentLink {LinkId}: the create response did not price order {OrderId} — retrieving the order for its real totals",
                linkId, orderId);
            // The sibling parser, not a second reading of the same JSON: this is
            // exactly what the recovery path in CheckoutFulfillment does with an
            // order whose create response we never stored. A non-2xx throws from
            // in here; a 404 comes back unpriced and is refused just below.
            order = await OrderLinesAsync(orderId, ct);
        }
        if (!IsPriced(order, lines))
        {
            _log.LogError("Square CreatePaymentLink {LinkId}: order {OrderId} carries no usable totals even on retrieval — refusing the checkout rather than recording an order we cannot price",
                linkId, orderId);
            throw new InvalidOperationException($"Square CreatePaymentLink {linkId}: order {orderId} came back unpriced");
        }

        var lineTax = new Dictionary<Guid, long>();
        foreach (var l in order!.Lines)
            if (l.TaxCents.HasValue) lineTax[l.ManifestId] = l.TaxCents.Value;

        return new CartLink(linkId, orderId, url,
            order.TotalCents!.Value, order.TaxCents!.Value, order.DeliveryCents ?? 0, lineTax);
    }

    /// <summary>
    /// Is this enough of an order to bill, refund and remit against? Three
    /// figures have to be Square's own, because nothing downstream can
    /// reconstruct them:
    ///
    ///   * the ORDER TOTAL — what the buyer is charged, and what goes back when
    ///     an order sells nothing at all;
    ///   * the ORDER TAX — what is owed to NCDOR rather than to NSL, and what
    ///     checkout_orders.subtotal_cents is derived by subtracting;
    ///   * a PER-LINE TAX for every box we sent — what a partial refund returns
    ///     with the one box the buyer did not get (spec §8.8). A box recorded
    ///     with tax 0 under-refunds by its own tax and, because the attention
    ///     flag clears at whatever figure we recorded, does it silently.
    ///
    /// Delivery is deliberately NOT required: when Square returns no service
    /// charge it also did not charge one, so a recorded 0 agrees with the total
    /// and nothing is under-refunded. Missing money elsewhere is the opposite —
    /// the buyer was charged and we would be writing down less than they paid.
    /// </summary>
    private static bool IsPriced(RecoveredOrder? order, IReadOnlyList<CartLine> lines)
    {
        if (order?.TotalCents == null || order.TaxCents == null) return false;
        var taxed = order.Lines.Where(l => l.TaxCents.HasValue).Select(l => l.ManifestId).ToHashSet();
        return lines.All(l => taxed.Contains(l.ManifestId));
    }

    /// <summary>Manifest ids we stamped as line-item uids on a cart order (webhook fallback correlation).</summary>
    public async Task<List<Guid>> OrderLineUidsAsync(string orderId, CancellationToken ct)
        => (await OrderLinesAsync(orderId, ct)).Lines.Select(l => l.ManifestId).ToList();

    /// <summary>
    /// One line of a Square order we can map back to a box: the manifest id we
    /// stamped as its uid, what the buyer actually paid for it EX tax, and the
    /// tax Square charged on it. Either amount is null when Square's response
    /// didn't carry the corresponding money field.
    /// </summary>
    public sealed record OrderLine(Guid ManifestId, long? AmountCents, long? TaxCents);

    /// <summary>
    /// A whole order as Square has it. Used to rebuild an order row whose create
    /// response we never stored: spec §8.6 says we never compute the authoritative
    /// tax, so a recovery reads the real per-line and order-level money here
    /// rather than re-pricing the boxes from today's ask price.
    /// </summary>
    public sealed record RecoveredOrder(List<OrderLine> Lines, long? TotalCents, long? TaxCents, long? DeliveryCents);

    /// <summary>
    /// Retrieve an order and pull out every line whose uid parses as a GUID, with
    /// its money, plus the order totals. Returns an empty result on 404 / unconfigured.
    ///
    /// A PARSEABLE GUID IS NOT PROOF THE LINE IS ONE OF OUR MANIFEST IDS — this
    /// method has no database and cannot check that. It used to be documented as
    /// skipping anything that isn't one of our manifest ids, which was never what
    /// the code did; that went unnoticed while a non-matching line was simply
    /// dropped, harmlessly. It is not harmless now: the caller
    /// (CheckoutFulfillment, recovery path) resolves every surviving line against
    /// dbo.v_pallets itself, and a line that matches no box there raises a refund
    /// flag for a human instead of being dropped. So the comment is load-bearing
    /// and was wrong; this fixes the comment rather than the code, because the
    /// manifest check belongs — and already lives — in the caller that owns the
    /// database connection, not in this REST client. Only a missing uid or one
    /// that isn't a GUID at all is skipped here.
    /// </summary>
    public async Task<RecoveredOrder> OrderLinesAsync(string orderId, CancellationToken ct)
    {
        using var doc = await RetrieveOrderAsync(orderId, ct);
        return doc != null && doc.RootElement.TryGetProperty("order", out var o)
            ? ReadOrder(o)
            : new RecoveredOrder(new List<OrderLine>(), null, null, null);
    }

    /// <summary>
    /// One Square `order` object, read into our shape. ONE parser, two callers:
    /// the copy Square embeds in a payment-link create response and the copy it
    /// returns from RetrieveOrder are the same object, and reading them two
    /// different ways is how the two recorded different money for the same order.
    /// Every figure is Square's own or null — null means Square did not say, which
    /// is never the same as zero (<see cref="IsPriced"/> is where that is judged).
    /// </summary>
    private static RecoveredOrder ReadOrder(JsonElement o)
    {
        var lines = new List<OrderLine>();
        if (o.TryGetProperty("line_items", out var items))
            foreach (var li in items.EnumerateArray())
            {
                if (!li.TryGetProperty("uid", out var uid) || !Guid.TryParse(uid.GetString(), out var g)) continue;
                // total_money on a Square line is tax-INCLUSIVE; amount_cents is the
                // ex-tax price, so prefer the gross-sales figures and only fall back
                // to total_money minus the line's tax.
                long? tax = Money(li, "total_tax_money");
                long? amount = Money(li, "gross_sales_money")
                            ?? Money(li, "variation_total_price_money")
                            ?? Money(li, "base_price_money");
                if (amount == null)
                {
                    var totalMoney = Money(li, "total_money");
                    if (totalMoney != null) amount = totalMoney - (tax ?? 0);
                }
                lines.Add(new OrderLine(g, amount, tax));
            }

        return new RecoveredOrder(lines,
            Money(o, "total_money"), Money(o, "total_tax_money"), Money(o, "total_service_charge_money"));

        static long? Money(JsonElement el, string name)
            => el.TryGetProperty(name, out var m) && m.TryGetProperty("amount", out var a) ? a.GetInt64() : null;
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
    /// What one order object says about its own money.
    /// <c>Paid</c> / <c>Unpaid</c> are positive readings. <c>Unknown</c> means the
    /// object carried neither signal and we learned nothing — see
    /// <see cref="PaymentOf"/> for why that is not the same as unpaid.
    /// </summary>
    public enum PaymentSignal { Unknown, Paid, Unpaid }

    /// <summary>
    /// "Paid" for a payment-link order. GOTCHA (Square docs): paid link orders
    /// go DRAFT -> OPEN and stay OPEN forever — never test state=="COMPLETED".
    /// Paid = tenders exist, or net_amount_due_money is zero.
    ///
    /// AND UNPAID IS NOT THE ABSENCE OF THOSE TWO KEYS. This used to return a
    /// bare false when an order object carried neither, which made a truncated
    /// body, a proxy-mangled response or an empty object read as positive proof
    /// that nobody had paid — and a caller that closes orders on that reading
    /// (SquareFunction.Reconcile) would bury the charge. A body with no order key
    /// at all was already treated as unreachable; this is the same rule applied
    /// one level down. Non-payment must be EVIDENCED: an empty tenders array, or
    /// a net_amount_due_money above zero. Anything else is Unknown, and a caller
    /// must treat Unknown exactly as it treats a 5xx.
    /// </summary>
    public static PaymentSignal PaymentOf(JsonElement order)
    {
        if (order.ValueKind != JsonValueKind.Object) return PaymentSignal.Unknown;

        bool hasTenders = order.TryGetProperty("tenders", out var tenders) &&
                          tenders.ValueKind == JsonValueKind.Array;
        if (hasTenders && tenders.GetArrayLength() > 0) return PaymentSignal.Paid;

        if (order.TryGetProperty("net_amount_due_money", out var due) &&
            due.ValueKind == JsonValueKind.Object &&
            due.TryGetProperty("amount", out var amt) &&
            amt.ValueKind == JsonValueKind.Number)
            return amt.GetInt64() == 0 ? PaymentSignal.Paid : PaymentSignal.Unpaid;

        // An empty tenders array is Square saying, positively, that nothing has
        // been tendered against this order. That is the ordinary unpaid link.
        return hasTenders ? PaymentSignal.Unpaid : PaymentSignal.Unknown;
    }

    /// <summary>
    /// Delete (deactivate) a payment link. Returns true only when Square
    /// confirms the order was cancelled (response carries cancelled_order_id)
    /// or the link is already gone (404). Square has been seen returning 200
    /// without cancelled_order_id and leaving the link payable — callers
    /// must treat false as "still open, re-check later", never as deleted.
    ///
    /// A 2xx NEVER throws, whatever the body is. The whole premise here is that
    /// Square misbehaves on success responses, so an empty or non-JSON body is
    /// just another flavour of "not confirmed" — throwing a JsonException out of
    /// a method callers read as a bool would turn a soft "re-check later" into a
    /// hard failure at call sites that don't catch (SquareFunction.cs:262, :604).
    /// Only a non-2xx that isn't 404 still throws.
    /// </summary>
    public async Task<bool> DeletePaymentLinkAsync(string linkId, CancellationToken ct)
    {
        using var client = Client();
        var resp = await client.DeleteAsync($"/v2/online-checkout/payment-links/{Uri.EscapeDataString(linkId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square DeletePaymentLink {LinkId} failed {Status}: {Body}", linkId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square DeletePaymentLink -> {(int)resp.StatusCode}");
        }

        var confirmed = false;
        if (string.IsNullOrWhiteSpace(body))
        {
            // no-op: falls through to the "still open" warning below
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                confirmed = doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("cancelled_order_id", out var c) &&
                            c.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(c.GetString());
            }
            catch (JsonException)
            {
                confirmed = false;
            }
        }

        if (!confirmed)
            _log.LogWarning("Square DeletePaymentLink {LinkId}: 200 without cancelled_order_id — treating as still open", linkId);
        return confirmed;
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

    /// <summary>
    /// Is this the Square answer that means "there is nothing here left to
    /// cancel"? Only two shapes qualify: the invoice is already CANCELED, or
    /// Square holds no such invoice at all.
    ///
    /// Deliberately narrow, and PAID is deliberately NOT in it. An invoice
    /// Square has taken money on must still fail loudly here, because the only
    /// caller goes on to clear manifests.invoice_id — on a paid invoice that
    /// quietly un-links a box from a real payment. "Cancelled" and "we never
    /// had it" are the two states where clearing our row is the correct and
    /// complete outcome; everything else is an error as before.
    /// </summary>
    internal static bool InvoiceAlreadyCanceled(string? invoiceStatus)
        => string.Equals(invoiceStatus, "CANCELED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cancel an unpaid invoice (fetches current version first).
    ///
    /// TOLERATES AN INVOICE SQUARE HAS ALREADY CANCELLED, and that is a
    /// correctness requirement of the caller, not politeness. CancelBoxInvoice
    /// calls Square FIRST and only then, in one transaction, closes the order
    /// row and clears manifests.invoice_id. If that transaction fails — an Azure
    /// SQL transient failover is routine — Square has cancelled and our box
    /// still carries the invoice id. The retry re-enters here. While this threw
    /// on an already-cancelled invoice the retry died before reaching any SQL,
    /// the box stayed blocked behind its stale invoice_id and NO route could
    /// clear it: the operator's only move was to edit the row by hand. Answering
    /// "already cancelled = done" is what makes that retry terminate.
    ///
    /// A 404 counts the same way. If Square holds no such invoice then nothing
    /// is payable against it, and our row pointing at it is precisely the stuck
    /// state this exists to let the caller clear.
    ///
    /// THAT 404 RULE IS NOT PROVABLY SCOPED TO THIS MERCHANT. A 404 also covers
    /// the case where invoiceId is real but belongs to a different Square
    /// environment or a different merchant account — sandbox credentials asked
    /// about a production invoice id, say — and this method cannot tell that
    /// apart from "we genuinely never had it," so it clears our row on it just
    /// the same, on an invoice that may still be holding money elsewhere. This
    /// is deliberately left as a documentation gap rather than a code change:
    /// the realistic trigger is a sandbox-versus-production credential
    /// mix-up, which breaks every other call this service makes just as
    /// badly, so a caller in that state has far larger problems than one
    /// invoice_id clearing early.
    ///
    /// The verdict is taken from the GET's status field and NOWHERE ELSE. The
    /// obvious-looking alternative — sniff the failed cancel's error body for
    /// the word "canceled" — was written and deleted: Square refuses a PAID
    /// invoice with "Invoice with status PAID cannot be canceled", which
    /// contains the word, so that sniff would swallow the one refusal that must
    /// never be swallowed and let the caller clear invoice_id off a box with a
    /// real payment behind it. The narrow cost of reading only the status is the
    /// race where the invoice is cancelled BETWEEN our GET and our POST: that
    /// POST still fails and still throws, but the operator's next retry re-reads
    /// the status, sees CANCELED and completes. The dead end is still closed —
    /// it just takes one more click in a case that needed one anyway.
    /// </summary>
    public async Task CancelInvoiceAsync(string invoiceId, CancellationToken ct)
    {
        using var client = Client();
        var gResp = await client.GetAsync($"/v2/invoices/{Uri.EscapeDataString(invoiceId)}", ct);
        if (gResp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _log.LogWarning("Square CancelInvoice {InvoiceId}: Square has no such invoice (404) — nothing to cancel, treating as done so the caller can clear our row", invoiceId);
            return;
        }
        var gBody = await gResp.Content.ReadAsStringAsync(ct);
        if (!gResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Square GetInvoice -> {(int)gResp.StatusCode}");

        // Status first, via the accessor that never throws, and version only
        // once we know we still need it: GetProperty("version") throws
        // KeyNotFoundException — not the InvalidOperationException the rest of
        // this method raises — if Square ever omits it. Every fixture supplies
        // version today so this is defensive only, but reading status first
        // costs nothing and means an already-CANCELED invoice never has to
        // touch that accessor at all.
        using var gDoc = JsonDocument.Parse(gBody);
        var inv = gDoc.RootElement.GetProperty("invoice");
        string? invoiceStatus = inv.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (InvoiceAlreadyCanceled(invoiceStatus))
        {
            _log.LogInformation("Square CancelInvoice {InvoiceId}: already CANCELED at Square — treating as done (this is the retry of a cancel whose database half failed)", invoiceId);
            return;
        }
        int version = inv.GetProperty("version").GetInt32();

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
    /// Refund (full or partial). Key = hash(payment, amount), ≤45 chars, so the
    /// same amount can't be refunded twice by a double click but a later
    /// different-amount refund is allowed.
    /// </summary>
    public async Task<JsonDocument> RefundPaymentAsync(string paymentId, long amountCents, string? reason, CancellationToken ct)
    {
        var payload = new
        {
            idempotency_key = SquarePayloads.RefundKey(paymentId, amountCents),
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
    /// One payment, straight from Square — the raw payment JSON, or null on 404.
    ///
    /// Exists for exactly one caller: the staff "look up the amount" action on a
    /// payment we recorded with amount_cents NULL. Square can omit amount_money
    /// from a payment webhook, and an unknown total concludes nothing — no refund
    /// is allowed to clear the attention flag on it — so without a way to ASK,
    /// such a row is flagged forever even after a human has refunded it by hand
    /// in the Square Dashboard. This is that way to ask.
    /// </summary>
    public async Task<JsonDocument?> RetrievePaymentAsync(string paymentId, CancellationToken ct)
    {
        using var client = Client();
        var resp = await client.GetAsync($"/v2/payments/{Uri.EscapeDataString(paymentId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square RetrievePayment {PaymentId} failed {Status}: {Body}", paymentId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square RetrievePayment -> {(int)resp.StatusCode}");
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
