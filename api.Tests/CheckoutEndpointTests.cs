using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// The public cart endpoint's refusals and its reference_id.
///
/// Every function here is built with a SQL connection string that points at a
/// closed port and an IHttpClientFactory that throws. That is deliberate: the
/// whole point of these cases is that the endpoint answers the shopper from
/// the request alone, so a test that passes proves no database connection was
/// taken and no call went out to Square. The happy path (which needs both)
/// lives in the manual/staging pass, not here.
/// </summary>
public class CheckoutEndpointTests
{
    private sealed class ExplodingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("no Square call was expected on this path");
    }

    private static SquareFunction Build(bool checkoutOn, bool configured = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = checkoutOn ? "true" : "false",
            // Closed port, 1s timeout: any path that reaches SQL fails loudly.
            ["SqlConnectionString"] =
                "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True",
        };
        if (configured)
        {
            settings["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token";
            settings["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1";
        }
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var square = new SquareService(new ExplodingHttpClientFactory(), cfg, NullLogger<SquareService>.Instance);
        var sql = new SqlService(cfg, NullLogger<SqlService>.Instance);
        var fulfill = new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance);
        return new SquareFunction(sql, square, fulfill, NullLogger<SquareFunction>.Instance);
    }

    private static HttpRequest Req(string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Request.ContentType = "application/json";
        return ctx.Request;
    }

    /// <summary>Null when the anonymous response object has no such member at all.</summary>
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    private static string Guids(int n) =>
        string.Join(",", Enumerable.Range(0, n).Select(_ => $"\"{Guid.NewGuid()}\""));

    // ---- GET /api/public/checkout-status ------------------------------------

    [Fact]
    public async Task Status_with_the_kill_switch_off_answers_without_touching_sql()
    {
        var r = Assert.IsType<OkObjectResult>(await Build(checkoutOn: false).Status(null!, CancellationToken.None));
        var v = r.Value!;
        Assert.Equal(false, Prop(v, "enabled"));
        Assert.Equal(SquareFunction.CartMax, Prop(v, "cartMax"));
        Assert.Equal(7.25m, Prop(v, "taxPercent"));
        Assert.Equal(SquarePayloads.DeliveryCents, Prop(v, "deliveryCents"));
        Assert.Empty((IEnumerable<string>)Prop(v, "deliveryZips")!);
        Assert.Equal(SquareFunction.FleaNote, Prop(v, "fleaNote"));
    }

    // ---- POST /api/public/checkout — refusals -------------------------------

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_with_no_field()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(Req("{not json"), CancellationToken.None));
        Assert.Equal("Invalid JSON", Prop(bad.Value!, "error"));
        Assert.Null(Prop(bad.Value!, "field"));
    }

    [Fact]
    public async Task An_unknown_delivery_option_is_a_400_on_the_delivery_field()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(
                Req($"{{\"ids\":[{Guids(1)}],\"delivery\":\"teleport\"}}"), CancellationToken.None));
        Assert.Equal("delivery", Prop(bad.Value!, "field"));
    }

    [Theory]
    [InlineData("\"pickup\"")]
    [InlineData("\"FLEA\"")]
    [InlineData("null")]
    public async Task An_empty_cart_is_a_400_whatever_the_handover(string delivery)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(
                Req($"{{\"ids\":[],\"delivery\":{delivery}}}"), CancellationToken.None));
        Assert.Equal("Add at least one box.", Prop(bad.Value!, "error"));
    }

    [Fact]
    public async Task A_cart_of_nothing_but_empty_guids_is_an_empty_cart_not_a_lookup()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(
                Req($"{{\"ids\":[\"{Guid.Empty}\",\"{Guid.Empty}\"],\"delivery\":\"pickup\"}}"),
                CancellationToken.None));
        Assert.Equal("Add at least one box.", Prop(bad.Value!, "error"));
    }

    [Fact]
    public async Task More_than_the_cart_max_is_a_400_before_any_lookup()
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(
                Req($"{{\"ids\":[{Guids(SquareFunction.CartMax + 1)}],\"delivery\":\"pickup\"}}"),
                CancellationToken.None));
        Assert.Equal($"A cart holds at most {SquareFunction.CartMax} boxes.", Prop(bad.Value!, "error"));
    }

    [Theory]
    [InlineData("\"275\"")]
    [InlineData("\"27587-1234\"")]
    [InlineData("\"abcde\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task A_delivery_order_without_a_five_digit_zip_is_a_400_on_zip_before_any_sql(string zip)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(
            await Build(checkoutOn: true).CreateCheckout(
                Req($"{{\"ids\":[{Guids(1)}],\"delivery\":\"delivery\",\"zip\":{zip},\"address\":\"1 Main St\"}}"),
                CancellationToken.None));
        Assert.Equal("zip", Prop(bad.Value!, "field"));
    }

    // ---- kill switch --------------------------------------------------------

    [Fact]
    public async Task Checkout_is_503_when_the_kill_switch_is_off()
    {
        var r = await Build(checkoutOn: false).CreateCheckout(
            Req($"{{\"ids\":[{Guids(1)}],\"delivery\":\"pickup\"}}"), CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(r);
        Assert.Equal(503, obj.StatusCode);
        Assert.Equal("Online checkout is not available right now.", Prop(obj.Value!, "error"));
    }

    [Fact]
    public async Task Checkout_is_503_when_square_credentials_are_missing()
    {
        var r = await Build(checkoutOn: true, configured: false).CreateCheckout(
            Req($"{{\"ids\":[{Guids(1)}],\"delivery\":\"pickup\"}}"), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    [Fact]
    public async Task The_legacy_single_box_route_is_a_cart_of_one_and_honours_the_kill_switch()
    {
        var r = await Build(checkoutOn: false).CreateCheckoutOne(null!, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    // ---- reference_id -------------------------------------------------------
    // Square caps order.reference_id at 40 characters. It is a label for Rob's
    // dashboard, not a correlation key (the line-item uid is), so past 40 a
    // count beats a list truncated mid-number.

    [Fact]
    public void Reference_id_is_the_box_list_while_it_fits()
    {
        Assert.Equal("NSL #12 #34", SquareFunction.ReferenceId(new[] { 12, 34 }));
    }

    [Fact]
    public void Reference_id_keeps_the_list_at_exactly_forty_characters()
    {
        var numbers = new[] { 10, 11, 12, 13, 14, 15, 16, 17, 100 };
        var reference = SquareFunction.ReferenceId(numbers);
        Assert.Equal(40, reference.Length);
        Assert.Equal("NSL #10 #11 #12 #13 #14 #15 #16 #17 #100", reference);
    }

    [Fact]
    public void Reference_id_falls_back_to_a_count_at_forty_one()
    {
        var numbers = new[] { 10, 11, 12, 13, 14, 15, 16, 100, 101 };
        Assert.Equal(41, ("NSL " + string.Join(" ", numbers.Select(n => "#" + n))).Length);
        Assert.Equal("NSL 9 boxes", SquareFunction.ReferenceId(numbers));
    }

    [Fact]
    public void Reference_id_for_a_full_cart_still_fits_squares_forty_chars()
    {
        var numbers = Enumerable.Range(1000, SquareFunction.CartMax).ToArray();
        var reference = SquareFunction.ReferenceId(numbers);
        Assert.Equal($"NSL {SquareFunction.CartMax} boxes", reference);
        Assert.True(reference.Length <= 40);
    }
}
