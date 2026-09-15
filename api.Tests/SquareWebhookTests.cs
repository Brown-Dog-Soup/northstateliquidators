using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// The Square webhook's refusals, its 200 bodies, and the floor/POS filter.
///
/// Same trick as CheckoutEndpointTests: the function is built with a SQL
/// connection string pointing at a closed port on a 1s timeout, so every case
/// here proves the handler answered Square from the event alone. That is the
/// property under test, not an accident — Square retries a 500 about eleven
/// times over 24 hours, so a malformed or irrelevant event has to be a cheap,
/// SQL-free 200. The paths that DO need a database (fulfilment, refund
/// recording) are exercised in the staging pass.
/// </summary>
public class SquareWebhookTests
{
    private const string SigKey = "test-signature-key";
    private const string SigUrl = "https://nsl.example/api/square/webhook";

    private sealed class ExplodingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("no Square call was expected on this path");
    }

    private static SquareFunction Build()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
            ["SQUARE_WEBHOOK_SIGNATURE_KEY"] = SigKey,
            ["SQUARE_WEBHOOK_URL"] = SigUrl,
            // Closed port, 1s timeout: any path that reaches SQL fails loudly.
            ["SqlConnectionString"] =
                "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True",
        }).Build();
        var square = new SquareService(new ExplodingHttpClientFactory(), cfg, NullLogger<SquareService>.Instance);
        var sql = new SqlService(cfg, NullLogger<SqlService>.Instance);
        var fulfill = new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance);
        return new SquareFunction(sql, square, fulfill, cfg, NullLogger<SquareFunction>.Instance);
    }

    private static string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(SigUrl + body)));
    }

    private static HttpRequest Req(string body, string? signature)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "application/json";
        if (signature != null) ctx.Request.Headers["x-square-hmacsha256-signature"] = signature;
        return ctx.Request;
    }

    /// <summary>Post a correctly signed body and get the 200 payload back.</summary>
    private static async Task<object> Post(string body)
    {
        var r = await Build().Webhook(Req(body, Sign(body)), CancellationToken.None);
        return Assert.IsType<OkObjectResult>(r).Value!;
    }

    /// <summary>Null when the anonymous response object has no such member at all.</summary>
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    private static string Payment(string status, string? orderId, string? product, long? amount = 12345,
        string paymentId = "PAY1", string type = "payment.updated")
    {
        var order = orderId == null ? "" : $@"""order_id"":""{orderId}"",";
        var app = product == null ? "" : $@"""application_details"":{{""square_product"":""{product}""}},";
        var money = amount == null ? "" : $@"""amount_money"":{{""amount"":{amount},""currency"":""USD""}},";
        return $@"{{""type"":""{type}"",""event_id"":""ev1"",""data"":{{""type"":""payment"",""object"":{{""payment"":{{
            ""id"":""{paymentId}"",""status"":""{status}"",{order}{app}{money}""location_id"":""LOC1""}}}}}}}}";
    }

    private static string Refund(string status, string? paymentId, long? amount = 100,
        string refundId = "REF1", string type = "refund.updated")
    {
        var pid = paymentId == null ? "" : $@"""payment_id"":""{paymentId}"",";
        var money = amount == null ? "" : $@"""amount_money"":{{""amount"":{amount},""currency"":""USD""}},";
        return $@"{{""type"":""{type}"",""event_id"":""ev1"",""data"":{{""type"":""refund"",""object"":{{""refund"":{{
            ""id"":""{refundId}"",""status"":""{status}"",{pid}{money}""location_id"":""LOC1""}}}}}}}}";
    }

    // ---- authentication -----------------------------------------------------

    [Fact]
    public async Task Webhook_drops_a_body_whose_signature_does_not_match()
    {
        var body = Payment("COMPLETED", "ORD1", "ECOMMERCE_API");
        var r = await Build().Webhook(Req(body, "not-the-right-signature"), CancellationToken.None);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    [Fact]
    public async Task Webhook_drops_a_body_with_no_signature_header()
    {
        var body = Payment("COMPLETED", "ORD1", "ECOMMERCE_API");
        var r = await Build().Webhook(Req(body, null), CancellationToken.None);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(r).StatusCode);
    }

    // ---- malformed never 500s ----------------------------------------------

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{\"type\":")]
    [InlineData("")]
    public async Task Webhook_answers_200_ignored_malformed_for_a_body_that_is_not_json(string body)
        => Assert.Equal("malformed", Prop(await Post(body), "ignored"));

    [Theory]
    [InlineData("[]")]                          // valid JSON, not an event object
    [InlineData("\"hello\"")]
    [InlineData("{\"type\":123}")]              // type is not a string
    [InlineData("{}")]                          // no type at all
    public async Task Webhook_answers_200_ignored_malformed_for_json_with_no_event_type(string body)
        => Assert.Equal("malformed", Prop(await Post(body), "ignored"));

    [Fact]
    public async Task Webhook_ignores_event_types_it_does_not_handle()
    {
        var v = await Post(@"{""type"":""invoice.published"",""data"":{""object"":{}}}");
        Assert.Equal("invoice.published", Prop(v, "ignored"));
    }

    [Fact]
    public async Task Webhook_ignores_a_payment_event_with_no_payment_id()
    {
        var v = await Post(@"{""type"":""payment.updated"",""data"":{""object"":{""payment"":{""status"":""COMPLETED""}}}}");
        Assert.Equal("malformed", Prop(v, "ignored"));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("APPROVED")]
    [InlineData("FAILED")]
    [InlineData("CANCELED")]
    public async Task Webhook_acts_only_on_COMPLETED_payments(string status)
        => Assert.Equal(status, Prop(await Post(Payment(status, "ORD1", "ECOMMERCE_API")), "ignored"));

    [Fact]
    public async Task Webhook_ignores_a_payment_event_with_no_status()
    {
        // Not COMPLETED, so nothing happens either way — but the `ignored`
        // reason is always a string, never a null the log cannot explain.
        var v = await Post(@"{""type"":""payment.updated"",""data"":{""object"":{""payment"":{""id"":""PAY1""}}}}");
        Assert.Equal("malformed", Prop(v, "ignored"));
    }

    // ---- the floor / POS filter --------------------------------------------
    //
    // The merchant account is shared with the counter. db/hotfix-floor-payments.sql
    // exists because two RETAIL cash sales were once recorded and flagged as owing
    // a refund they did not owe.

    [Theory]
    [InlineData("RETAIL")]
    [InlineData("SQUARE_POS")]
    [InlineData("VIRTUAL_TERMINAL")]
    [InlineData(null)]
    public async Task Webhook_ignores_a_counter_sale_without_touching_sql(string? product)
    {
        // No order_id: there is nothing to look up, so the answer comes from the
        // event alone — reaching SQL here would blow up on the closed port.
        var v = await Post(Payment("COMPLETED", orderId: null, product: product));
        Assert.Equal("floor", Prop(v, "ignored"));
    }

    [Theory]
    // (order we know, square_product)                      -> is it a counter sale?
    [InlineData(false, "RETAIL", true)]
    [InlineData(false, "SQUARE_POS", true)]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "ECOMMERCE_API", false)]     // our payment link
    [InlineData(false, "INVOICES", false)]          // our wholesale invoice
    [InlineData(true, "RETAIL", false)]             // an order we minted, taken at the counter
    [InlineData(true, null, false)]
    [InlineData(true, "ECOMMERCE_API", false)]
    public void IsFloorPayment_needs_both_an_unknown_order_and_a_foreign_product(
        bool knownOrder, string? product, bool expected)
        => Assert.Equal(expected, SquareFunction.IsFloorPayment(knownOrder, product));

    // ---- refunds ------------------------------------------------------------

    [Fact]
    public async Task Webhook_ignores_a_refund_event_with_no_refund_object()
        => Assert.Equal("malformed", Prop(await Post(@"{""type"":""refund.updated"",""data"":{}}"), "ignored"));

    [Theory]
    [InlineData("PENDING")]
    [InlineData("APPROVED")]
    public async Task Webhook_does_not_record_a_refund_that_has_not_settled(string status)
    {
        // An accepted-but-unsettled refund must not clear the attention flag, and
        // there is nothing to write, so it never opens a connection.
        var v = await Post(Refund(status, "PAY1"));
        Assert.Equal(status, Prop(v, "refund"));
        Assert.Equal(false, Prop(v, "recorded"));
    }

    [Fact]
    public async Task Webhook_does_not_record_a_refund_with_no_payment_id()
    {
        var v = await Post(Refund("COMPLETED", paymentId: null));
        Assert.Equal("COMPLETED", Prop(v, "refund"));
        Assert.Equal(false, Prop(v, "recorded"));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    public async Task Webhook_does_not_record_a_refund_of_nothing(long? amount)
    {
        var v = await Post(Refund("COMPLETED", "PAY1", amount));
        Assert.Equal("COMPLETED", Prop(v, "refund"));
        Assert.Equal(false, Prop(v, "recorded"));
    }

    // ---- the fulfilment 200 body -------------------------------------------

    private static FulfillResult R(string outcome, int sold = 0, int newlySold = 0, int unavailable = 0,
        long refundDue = 0, List<int>? boxes = null, List<int>? unavailableBoxes = null)
        => new(outcome, sold, newlySold, unavailable, refundDue, boxes ?? new List<int>(), unavailableBoxes ?? new List<int>());

    [Fact]
    public void FulfillmentBody_reports_a_duplicate_and_nothing_else()
    {
        var v = SquareFunction.FulfillmentBody(R("duplicate"));
        Assert.Equal(true, Prop(v, "duplicate"));
        Assert.Null(Prop(v, "fulfilled"));
    }

    [Fact]
    public void FulfillmentBody_reports_unmatched_and_nothing_else()
    {
        var v = SquareFunction.FulfillmentBody(R("unmatched"));
        Assert.Equal(true, Prop(v, "unmatched"));
        Assert.Null(Prop(v, "fulfilled"));
    }

    [Fact]
    public void FulfillmentBody_reports_the_boxes_and_the_tax_inclusive_refund()
    {
        var v = SquareFunction.FulfillmentBody(R("fulfilled", sold: 2, newlySold: 2, unavailable: 1,
            refundDue: 10725, boxes: new List<int> { 41, 42 }, unavailableBoxes: new List<int> { 43 }));
        Assert.Equal(true, Prop(v, "fulfilled"));
        Assert.Equal(2, Prop(v, "sold"));
        Assert.Equal(1, Prop(v, "unavailable"));
        Assert.Equal(10725L, Prop(v, "refundDue"));
        Assert.Equal(new[] { 41, 42 }, (IEnumerable<int>)Prop(v, "boxes")!);
        Assert.Equal(new[] { 43 }, (IEnumerable<int>)Prop(v, "unavailableBoxes")!);
    }

    /// <summary>
    /// The one that matters on a replay: a second tender against an order we
    /// already fulfilled re-reads every box as 'sold' — sold by US, earlier.
    /// Reporting FulfillResult.Sold there tells the caller N boxes just sold and
    /// fires the buyer's confirmation a second time. Only NewlySold is news.
    /// </summary>
    [Fact]
    public void FulfillmentBody_reports_newly_sold_boxes_not_the_orders_whole_tally()
    {
        var v = SquareFunction.FulfillmentBody(R("fulfilled", sold: 3, newlySold: 0, refundDue: 30000,
            boxes: new List<int> { 1, 2, 3 }));
        Assert.Equal(0, Prop(v, "sold"));
    }
}
