using System.Text.Json;
using NSL.Api.Services;
using Xunit;

public class SquareEventsTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string RetailCash = """
    {"type":"payment.updated","event_id":"e1","data":{"type":"payment","id":"p1","object":{"payment":{
      "id":"PAY_RETAIL","status":"COMPLETED","order_id":"ORD_RETAIL","source_type":"CASH",
      "amount_money":{"amount":25000,"currency":"USD"},
      "application_details":{"square_product":"RETAIL"}}}}}
    """;

    private const string WebLink = """
    {"type":"payment.updated","event_id":"e2","data":{"type":"payment","id":"p2","object":{"payment":{
      "id":"PAY_WEB","status":"COMPLETED","order_id":"ORD_WEB",
      "amount_money":{"amount":100,"currency":"USD"},
      "application_details":{"square_product":"ECOMMERCE_API"}}}}}
    """;

    [Theory]
    [InlineData("ECOMMERCE_API", true)]
    [InlineData("INVOICES", true)]
    [InlineData("ecommerce_api", true)]
    [InlineData("RETAIL", false)]
    [InlineData("SQUARE_POS", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsOurProduct_matches_only_web_and_invoice_products(string? product, bool expected)
        => Assert.Equal(expected, SquareEvents.IsOurProduct(product));

    [Fact]
    public void EventType_reads_type_or_null()
    {
        Assert.Equal("payment.updated", SquareEvents.EventType(Root(WebLink)));
        Assert.Null(SquareEvents.EventType(Root("{}")));
        Assert.Null(SquareEvents.EventType(Root("{\"type\":123}")));
    }

    [Fact]
    public void TryParsePayment_reads_all_fields()
    {
        Assert.True(SquareEvents.TryParsePayment(Root(RetailCash), out var ev));
        Assert.Equal("PAY_RETAIL", ev.PaymentId);
        Assert.Equal("COMPLETED", ev.Status);
        Assert.Equal("ORD_RETAIL", ev.OrderId);
        Assert.Equal(25000, ev.AmountCents);
        Assert.Equal("RETAIL", ev.Product);
    }

    [Fact]
    public void TryParsePayment_tolerates_missing_optional_fields()
    {
        var json = """{"type":"payment.updated","data":{"object":{"payment":{"id":"PAY_MIN"}}}}""";
        Assert.True(SquareEvents.TryParsePayment(Root(json), out var ev));
        Assert.Equal("PAY_MIN", ev.PaymentId);
        Assert.Null(ev.Status);
        Assert.Null(ev.OrderId);
        Assert.Null(ev.AmountCents);
        Assert.Null(ev.Product);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type":"payment.updated","data":{}}""")]
    [InlineData("""{"type":"payment.updated","data":{"object":{"payment":{}}}}""")]
    [InlineData("""{"type":"payment.updated","data":{"object":{"payment":{"id":""}}}}""")]
    public void TryParsePayment_returns_false_without_a_payment_id(string json)
        => Assert.False(SquareEvents.TryParsePayment(Root(json), out _));

    [Fact]
    public void TryParseRefund_reads_fields()
    {
        var json = """{"type":"refund.updated","data":{"object":{"refund":{"id":"R1","status":"COMPLETED","payment_id":"PAY_WEB","amount_money":{"amount":100,"currency":"USD"}}}}}""";
        Assert.True(SquareEvents.TryParseRefund(Root(json), out var r));
        Assert.Equal("R1", r.RefundId);
        Assert.Equal("COMPLETED", r.Status);
        Assert.Equal("PAY_WEB", r.PaymentId);
        Assert.Equal(100, r.AmountCents);
    }

    [Fact]
    public void TryParseRefund_returns_false_without_refund_object()
        => Assert.False(SquareEvents.TryParseRefund(Root("""{"type":"refund.updated","data":{}}"""), out _));
}
