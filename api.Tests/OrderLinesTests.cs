using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// OrderLinesAsync is what a recovered order is rebuilt from, so the money it
/// returns becomes the money we later refund (spec §8.6 / §8.8). The trap it has
/// to avoid: a Square line's total_money is tax-INCLUSIVE, while our
/// checkout_order_boxes.amount_cents is ex-tax. Reading the wrong field
/// overstates every box by its own tax and then refunds that too.
/// </summary>
public class OrderLinesTests
{
    private sealed class OneResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        public OneResponseHandler(HttpStatusCode status, string? body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status);
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

    private static SquareService Build(HttpStatusCode status, string? body)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
        }).Build();
        return new SquareService(new StubHttpClientFactory(new OneResponseHandler(status, body)),
            cfg, NullLogger<SquareService>.Instance);
    }

    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    private static Task<SquareService.RecoveredOrder> Lines(string body)
        => Build(HttpStatusCode.OK, body).OrderLinesAsync("ORDER1", CancellationToken.None);

    /// <summary>Two boxes at $100 and $50, 7.25% tax, $10 delivery — as Square returns it.</summary>
    private const string TwoBoxOrder = @"{""order"":{
        ""total_money"":{""amount"":17088,""currency"":""USD""},
        ""total_tax_money"":{""amount"":1088,""currency"":""USD""},
        ""total_service_charge_money"":{""amount"":1000,""currency"":""USD""},
        ""line_items"":[
          {""uid"":""11111111-1111-1111-1111-111111111111"",
           ""gross_sales_money"":{""amount"":10000},
           ""total_tax_money"":{""amount"":725},
           ""total_money"":{""amount"":10725}},
          {""uid"":""22222222-2222-2222-2222-222222222222"",
           ""gross_sales_money"":{""amount"":5000},
           ""total_tax_money"":{""amount"":363},
           ""total_money"":{""amount"":5363}}
        ]}}";

    [Fact]
    public async Task Reads_the_real_per_line_money_not_the_tax_inclusive_total()
    {
        var o = await Lines(TwoBoxOrder);
        Assert.Equal(2, o.Lines.Count);
        Assert.Equal(Guid.Parse(A), o.Lines[0].ManifestId);
        Assert.Equal(10000, o.Lines[0].AmountCents);   // NOT 10725
        Assert.Equal(725, o.Lines[0].TaxCents);
        Assert.Equal(5000, o.Lines[1].AmountCents);
        Assert.Equal(363, o.Lines[1].TaxCents);
    }

    [Fact]
    public async Task Reads_the_order_level_totals()
    {
        var o = await Lines(TwoBoxOrder);
        Assert.Equal(17088, o.TotalCents);
        Assert.Equal(1088, o.TaxCents);
        Assert.Equal(1000, o.DeliveryCents);
    }

    /// <summary>The per-box tax must still add up to the order tax, or a partial refund is wrong.</summary>
    [Fact]
    public async Task Per_line_tax_sums_to_the_order_tax()
    {
        var o = await Lines(TwoBoxOrder);
        Assert.Equal(o.TaxCents, o.Lines.Sum(l => l.TaxCents ?? 0));
    }

    [Fact]
    public async Task Falls_back_to_total_money_minus_its_own_tax_when_gross_sales_is_absent()
    {
        var o = await Lines(@"{""order"":{""line_items"":[
            {""uid"":""11111111-1111-1111-1111-111111111111"",
             ""total_tax_money"":{""amount"":725},
             ""total_money"":{""amount"":10725}}]}}");
        Assert.Equal(10000, o.Lines[0].AmountCents);
    }

    [Fact]
    public async Task Uses_the_variation_total_when_that_is_all_square_sent()
    {
        var o = await Lines(@"{""order"":{""line_items"":[
            {""uid"":""11111111-1111-1111-1111-111111111111"",
             ""variation_total_price_money"":{""amount"":10000}}]}}");
        Assert.Equal(10000, o.Lines[0].AmountCents);
        Assert.Null(o.Lines[0].TaxCents);
    }

    /// <summary>Nulls, not zeros: the caller's SQL falls back to the box price only when it sees null.</summary>
    [Fact]
    public async Task A_line_with_no_money_at_all_reports_null_rather_than_zero()
    {
        var o = await Lines(@"{""order"":{""line_items"":[
            {""uid"":""11111111-1111-1111-1111-111111111111""}]}}");
        Assert.Single(o.Lines);
        Assert.Null(o.Lines[0].AmountCents);
        Assert.Null(o.Lines[0].TaxCents);
    }

    [Fact]
    public async Task Skips_line_uids_that_are_not_manifest_ids()
    {
        var o = await Lines(@"{""order"":{""line_items"":[
            {""uid"":""delivery-service-charge"",""gross_sales_money"":{""amount"":1000}},
            {""uid"":""22222222-2222-2222-2222-222222222222"",""gross_sales_money"":{""amount"":5000}}]}}");
        Assert.Single(o.Lines);
        Assert.Equal(Guid.Parse(B), o.Lines[0].ManifestId);
    }

    [Fact]
    public async Task A_missing_order_yields_nothing_rather_than_throwing()
    {
        var o = await Build(HttpStatusCode.NotFound, null).OrderLinesAsync("GONE", CancellationToken.None);
        Assert.Empty(o.Lines);
        Assert.Null(o.TotalCents);
    }

    /// <summary>The Task 2 signature still works — it is just the ids of the same lines now.</summary>
    [Fact]
    public async Task OrderLineUids_still_returns_the_manifest_ids()
    {
        var ids = await Build(HttpStatusCode.OK, TwoBoxOrder).OrderLineUidsAsync("ORDER1", CancellationToken.None);
        Assert.Equal(new[] { Guid.Parse(A), Guid.Parse(B) }, ids);
    }
}
