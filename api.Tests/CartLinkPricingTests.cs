using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// What a cart link records as money, and what happens when Square will not say.
///
/// THE BUG THESE EXIST FOR. Square normally embeds the order it just created in
/// the payment-link response, and every money column we write comes out of that
/// block: the total the buyer is charged, the tax that belongs to NCDOR, the
/// delivery service charge, and the per-box tax a partial refund returns with the
/// one box the buyer did not get. When the block was absent the service used to
/// INVENT those figures — total = the sum of our own line asks, tax 0, delivery 0,
/// every per-box tax 0 — while Square went on charging goods + 7.25% + delivery.
/// Nothing downstream could tell: the goods-versus-subtotal backstop shrinks on
/// both sides at once, and needs_refund clears at whatever refund_due_cents says,
/// so the under-refunded row went green on its own.
///
/// So: read the order Square actually holds, and if it will not price the order,
/// refuse the sale. A refused checkout is a phone call; a mispriced order is a
/// refund nobody knows is owed.
/// </summary>
public class CartLinkPricingTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The two boxes we send: $100 and $50, ex tax.</summary>
    private static readonly CartLine[] Lines =
    {
        new(A, "BOX #1 — Tools", 10_000),
        new(B, "BOX #2 — Toys",   5_000),
    };

    /// <summary>
    /// What Square holds for that cart, delivered: $150 goods + $10 delivery,
    /// taxed per line (725 + 363 + 73 = 1161) = $171.61. NOT 15000, which is what
    /// the old fallback would have recorded, and not tax 0.
    /// </summary>
    private const string RealOrder = @"{
        ""total_money"":{""amount"":17161,""currency"":""USD""},
        ""total_tax_money"":{""amount"":1161},
        ""total_service_charge_money"":{""amount"":1000},
        ""line_items"":[
          {""uid"":""11111111-1111-1111-1111-111111111111"",
           ""gross_sales_money"":{""amount"":10000},""total_tax_money"":{""amount"":725}},
          {""uid"":""22222222-2222-2222-2222-222222222222"",
           ""gross_sales_money"":{""amount"":5000},""total_tax_money"":{""amount"":363}}
        ]}";

    private static string LinkResponse(string? embeddedOrder) => @"{""payment_link"":{
        ""id"":""LINK1"",""order_id"":""ORDER1"",""url"":""https://square.link/u/abc""}" +
        (embeddedOrder == null ? "" : @",""related_resources"":{""orders"":[" + embeddedOrder + "]}") + "}";

    private static string Retrieved(string order) => @"{""order"":" + order + "}";

    /// <summary>
    /// Answers the POST that mints the link with one body and the GET that
    /// retrieves the order with another, and COUNTS the retrievals — several tests
    /// below turn on whether the second call happened at all.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _create;
        private readonly HttpStatusCode _getStatus;
        private readonly string? _getBody;
        public int Retrievals { get; private set; }

        public RoutingHandler(string create, HttpStatusCode getStatus = HttpStatusCode.OK, string? getBody = null)
        { _create = create; _getStatus = getStatus; _getBody = getBody; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool isCreate = request.Method == HttpMethod.Post;
            if (!isCreate) Retrievals++;
            var resp = new HttpResponseMessage(isCreate ? HttpStatusCode.OK : _getStatus);
            var body = isCreate ? _create : _getBody;
            if (body != null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
            // Closed port, 1s timeout: nothing in this file may reach SQL.
            ["SqlConnectionString"] =
                "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True",
        }).Build();

    private static SquareService Square(RoutingHandler h) =>
        new(new StubFactory(h), Config(), NullLogger<SquareService>.Instance);

    private static Task<SquareService.CartLink> Create(RoutingHandler h) =>
        Square(h).CreateCartPaymentLinkAsync(Lines, "https://northstateliquidators.com/thanks.html",
            idempotencyKey: "nsl-cart-test", referenceId: "BOX 1,2", paymentNote: "BOX #1, #2",
            DeliveryMethod.Delivery, deliveryFeeCents: 1000, CancellationToken.None);

    // ---- the ordinary path: Square embeds the order ----------------------------

    [Fact]
    public async Task The_embedded_order_is_what_we_record()
    {
        var h = new RoutingHandler(LinkResponse(RealOrder));

        var link = await Create(h);

        Assert.Equal("LINK1", link.Id);
        Assert.Equal("ORDER1", link.OrderId);
        Assert.Equal(17161, link.TotalCents);
        Assert.Equal(1161, link.TaxCents);
        Assert.Equal(1000, link.DeliveryCents);
        Assert.Equal(725, link.LineTaxCents[A]);
        Assert.Equal(363, link.LineTaxCents[B]);
    }

    /// <summary>
    /// And it costs nothing extra. Checkout is a shopper waiting on a button; a
    /// second Square round trip on the path that already has the answer would be
    /// latency for nothing.
    /// </summary>
    [Fact]
    public async Task An_embedded_order_is_not_re_fetched()
    {
        var h = new RoutingHandler(LinkResponse(RealOrder));

        await Create(h);

        Assert.Equal(0, h.Retrievals);
    }

    // ---- the branch this round fixed -------------------------------------------

    /// <summary>
    /// THE SCENARIO. No related_resources at all. The old code recorded
    /// total = 15000 (our own asks), tax 0, delivery 0 and per-box tax 0 — under
    /// every real figure, and silently. Now the order is retrieved and what we
    /// record is what Square charges.
    /// </summary>
    [Fact]
    public async Task A_create_response_with_no_order_is_priced_from_the_retrieved_order()
    {
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.OK, Retrieved(RealOrder));

        var link = await Create(h);

        Assert.Equal(1, h.Retrievals);
        Assert.Equal(17161, link.TotalCents);            // NOT 15000, the sum of our asks
        Assert.Equal(1161, link.TaxCents);               // NOT 0
        Assert.Equal(1000, link.DeliveryCents);          // NOT 0
        Assert.Equal(725, link.LineTaxCents[A]);         // NOT 0 — a partial refund returns this
        Assert.Equal(363, link.LineTaxCents[B]);
    }

    /// <summary>
    /// The boundary of the same claim. An order block that IS there but carries no
    /// total is no more priced than an absent one, and reading it as "Square said
    /// so" would write our own line sum with tax 0 all over again.
    /// </summary>
    [Fact]
    public async Task An_embedded_order_with_no_total_is_retrieved_rather_than_believed()
    {
        var unpriced = @"{""line_items"":[
            {""uid"":""11111111-1111-1111-1111-111111111111"",""gross_sales_money"":{""amount"":10000}},
            {""uid"":""22222222-2222-2222-2222-222222222222"",""gross_sales_money"":{""amount"":5000}}]}";
        var h = new RoutingHandler(LinkResponse(unpriced), HttpStatusCode.OK, Retrieved(RealOrder));

        var link = await Create(h);

        Assert.Equal(1, h.Retrievals);
        Assert.Equal(17161, link.TotalCents);
        Assert.Equal(1161, link.TaxCents);
    }

    /// <summary>
    /// The per-box tax is not a nice-to-have: it is exactly what a partial refund
    /// returns with the box the buyer did not get (spec §8.8). An order-level total
    /// with a line whose tax is missing would record that box at tax 0 and
    /// under-refund it, so it is not "priced" either.
    /// </summary>
    [Fact]
    public async Task An_embedded_order_missing_one_lines_tax_is_retrieved_rather_than_believed()
    {
        var halfPriced = @"{
            ""total_money"":{""amount"":17161},""total_tax_money"":{""amount"":1161},
            ""total_service_charge_money"":{""amount"":1000},
            ""line_items"":[
              {""uid"":""11111111-1111-1111-1111-111111111111"",
               ""gross_sales_money"":{""amount"":10000},""total_tax_money"":{""amount"":725}},
              {""uid"":""22222222-2222-2222-2222-222222222222"",
               ""gross_sales_money"":{""amount"":5000}}]}";
        var h = new RoutingHandler(LinkResponse(halfPriced), HttpStatusCode.OK, Retrieved(RealOrder));

        var link = await Create(h);

        Assert.Equal(1, h.Retrievals);
        Assert.Equal(363, link.LineTaxCents[B]);
    }

    // ---- and when Square will not price it at all -------------------------------

    /// <summary>
    /// Square answered the retrieval with 404. There is no figure left that is not
    /// a guess, so the sale is refused rather than recorded at a price we made up.
    /// </summary>
    [Fact]
    public async Task An_order_square_cannot_show_us_refuses_the_checkout()
    {
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(h));
    }

    /// <summary>Square down, not merely unhelpful: same answer.</summary>
    [Fact]
    public async Task A_retrieval_that_errors_refuses_the_checkout()
    {
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.InternalServerError, @"{""errors"":[]}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(h));
    }

    /// <summary>
    /// A retrieved order with a total but no tax. This is the shape that most
    /// invites a shrug — "the total is right, call the tax zero" — and it is the
    /// one that under-remits to NCDOR and under-refunds every unavailable box.
    /// </summary>
    [Fact]
    public async Task A_retrieved_order_with_no_tax_refuses_the_checkout()
    {
        var noTax = @"{""total_money"":{""amount"":17161},
            ""line_items"":[
              {""uid"":""11111111-1111-1111-1111-111111111111"",""gross_sales_money"":{""amount"":10000}},
              {""uid"":""22222222-2222-2222-2222-222222222222"",""gross_sales_money"":{""amount"":5000}}]}";
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.OK, Retrieved(noTax));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(h));
    }

    /// <summary>
    /// A retrieved order that prices the order but not every box. One box would go
    /// on the row with tax 0 and be short its own tax on any partial refund, and
    /// the attention flag would clear at that short figure.
    /// </summary>
    [Fact]
    public async Task A_retrieved_order_missing_one_lines_tax_refuses_the_checkout()
    {
        var halfPriced = @"{""total_money"":{""amount"":17161},""total_tax_money"":{""amount"":1161},
            ""line_items"":[
              {""uid"":""11111111-1111-1111-1111-111111111111"",
               ""gross_sales_money"":{""amount"":10000},""total_tax_money"":{""amount"":725}},
              {""uid"":""22222222-2222-2222-2222-222222222222"",
               ""gross_sales_money"":{""amount"":5000}}]}";
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.OK, Retrieved(halfPriced));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(h));
    }

    /// <summary>
    /// WHAT THE SHOPPER ACTUALLY GETS, which is the half a throw does not prove.
    /// The refusal has to arrive as the existing 502 "call us" answer — the same
    /// one a Square timeout gets — and not as an unhandled 500 or, worse, a link
    /// the buyer can pay against an order we never priced.
    /// </summary>
    [Fact]
    public async Task An_unpriceable_order_reaches_the_shopper_as_the_502_call_us_answer()
    {
        var h = new RoutingHandler(LinkResponse(null), HttpStatusCode.NotFound);
        var cfg = Config();
        var square = Square(h);
        var fn = new SquareFunction(new SqlService(cfg, NullLogger<SqlService>.Instance), square,
            new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance),
            cfg, NullLogger<SquareFunction>.Instance);

        var (link, error) = await fn.TryCreateCartLinkAsync(Lines, new[] { 1, 2 },
            "https://northstateliquidators.com/thanks.html", DeliveryMethod.Delivery, "delivery",
            deliveryFeeCents: 1000, CancellationToken.None);

        Assert.Null(link);
        var result = Assert.IsType<ObjectResult>(error);
        Assert.Equal(502, result.StatusCode);
        Assert.Contains("call us", (string)result.Value!.GetType().GetProperty("error")!.GetValue(result.Value)!);
    }
}
