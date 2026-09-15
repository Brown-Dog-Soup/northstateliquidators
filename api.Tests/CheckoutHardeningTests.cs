using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// Hardening of the anonymous cart route: the per-IP rate limit, the zip
/// cache's two very different TTLs, and the nullable pallet number.
///
/// As in CheckoutEndpointTests the SQL connection string points at a closed
/// port, so anything that reaches the database fails loudly rather than
/// quietly passing.
/// </summary>
public class CheckoutHardeningTests
{
    private sealed class ExplodingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("no Square call was expected on this path");
    }

    private static SquareFunction Build(bool checkoutOn = true)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = checkoutOn ? "true" : "false",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
            ["SqlConnectionString"] =
                "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True",
        }).Build();
        var square = new SquareService(new ExplodingHttpClientFactory(), cfg, NullLogger<SquareService>.Instance);
        var sql = new SqlService(cfg, NullLogger<SqlService>.Instance);
        var fulfill = new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance);
        return new SquareFunction(sql, square, fulfill, cfg, NullLogger<SquareFunction>.Instance);
    }

    /// <summary>An empty cart: refused from the request alone, so it never reaches SQL or Square.</summary>
    private static HttpRequest EmptyCartFrom(string ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ids\":[],\"delivery\":\"pickup\"}"));
        ctx.Request.ContentType = "application/json";
        return ctx.Request;
    }

    // ---- item 2: per-IP rate limit on POST /api/public/checkout -------------

    /// <summary>
    /// Ten attempts a minute get through; the eleventh is a 429. The route is
    /// anonymous and every call past the reuse query mints a real Square order,
    /// so the ceiling the old deterministic idempotency key used to provide has
    /// to come from somewhere.
    /// </summary>
    [Fact]
    public async Task An_eleventh_checkout_in_one_window_from_one_address_is_a_429()
    {
        SquareFunction.ResetStaticStateForTests();
        var fn = Build();
        const string ip = "198.51.100.7";

        for (var i = 1; i <= 10; i++)
        {
            var allowed = await fn.CreateCheckout(EmptyCartFrom(ip), CancellationToken.None);
            // A 400 means it got past the limiter and was refused on its merits.
            Assert.IsType<BadRequestObjectResult>(allowed);
        }

        var refused = Assert.IsType<ObjectResult>(await fn.CreateCheckout(EmptyCartFrom(ip), CancellationToken.None));
        Assert.Equal(429, refused.StatusCode);
        Assert.Contains("Too many checkout attempts",
            (string)refused.Value!.GetType().GetProperty("error")!.GetValue(refused.Value)!);
    }

    /// <summary>
    /// The bucket is per address: one hammering shopper must not shut the cart
    /// for everyone else on the instance.
    /// </summary>
    [Fact]
    public async Task One_address_over_the_limit_does_not_refuse_a_different_address()
    {
        SquareFunction.ResetStaticStateForTests();
        var fn = Build();

        for (var i = 1; i <= 11; i++)
            await fn.CreateCheckout(EmptyCartFrom("198.51.100.8"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(
            await fn.CreateCheckout(EmptyCartFrom("198.51.100.9"), CancellationToken.None));
    }

    /// <summary>
    /// checkout-status is deliberately NOT limited — the homepage reads it on
    /// every load, and a shopper who reloads is not an attacker.
    /// </summary>
    [Fact]
    public async Task Checkout_status_is_not_rate_limited()
    {
        SquareFunction.ResetStaticStateForTests();
        // Kill switch off so the loop answers from the request alone.
        var fn = Build(checkoutOn: false);
        for (var i = 0; i < 25; i++)
            Assert.IsType<OkObjectResult>(await fn.Status(EmptyCartFrom("198.51.100.10"), CancellationToken.None));
    }

    // ---- items 3 + 4: the zip cache and its asymmetric TTLs -----------------

    private static SquareFunction.ZipCache Cache(TimeSpan readAgo, TimeSpan? failedAgo = null) =>
        new(new List<SquareFunction.DeliveryZip> { new("27587", 1000) },
            DateTime.UtcNow - readAgo,
            failedAgo is null ? DateTime.MinValue : DateTime.UtcNow - failedAgo.Value);

    [Fact]
    public void Nothing_cached_means_read_the_table()
    {
        Assert.False(SquareFunction.ServeCachedZips(null, DateTime.UtcNow));
    }

    [Fact]
    public void A_recent_good_read_is_served_from_cache()
    {
        Assert.True(SquareFunction.ServeCachedZips(Cache(readAgo: TimeSpan.FromMinutes(1)), DateTime.UtcNow));
    }

    [Fact]
    public void A_stale_good_read_goes_back_to_the_table()
    {
        Assert.False(SquareFunction.ServeCachedZips(Cache(readAgo: TimeSpan.FromMinutes(6)), DateTime.UtcNow));
    }

    /// <summary>
    /// The negative cache: after a failed read, the next few seconds of public
    /// page loads are answered from the last good list instead of each opening
    /// its own doomed connection.
    /// </summary>
    [Fact]
    public void A_just_failed_read_is_held_off_even_though_the_good_list_is_stale()
    {
        var cached = Cache(readAgo: TimeSpan.FromHours(2), failedAgo: TimeSpan.FromSeconds(2));
        Assert.True(SquareFunction.ServeCachedZips(cached, DateTime.UtcNow));
        // and what it serves is still the last good list
        Assert.Equal("27587", Assert.Single(cached.Zips).Zip);
    }

    /// <summary>
    /// The asymmetry, stated as a test: a failure is held for SECONDS, not the
    /// five minutes a good list is worth. Holding it longer would leave delivery
    /// switched off after the database is already back.
    /// </summary>
    [Fact]
    public void A_failure_window_expires_in_seconds_not_minutes()
    {
        Assert.False(SquareFunction.ServeCachedZips(
            Cache(readAgo: TimeSpan.FromHours(2), failedAgo: TimeSpan.FromSeconds(30)), DateTime.UtcNow));
        // the same age would still be well inside the SUCCESS ttl
        Assert.True(SquareFunction.ServeCachedZips(
            Cache(readAgo: TimeSpan.FromSeconds(30)), DateTime.UtcNow));
    }

    // ---- item 5: manifests.pallet_number is INT NULL ------------------------

    [Fact]
    public void A_live_priced_numbered_box_is_sellable()
    {
        Assert.True(SquareFunction.Sellable("live", null, false, null, 49.99m, 7));
    }

    /// <summary>
    /// No pallet number, no guess: the number names the line on the buyer's
    /// receipt, and the cast that used to read it would have thrown mid-request.
    /// </summary>
    [Fact]
    public void A_box_with_no_pallet_number_is_unavailable_rather_than_a_500()
    {
        Assert.False(SquareFunction.Sellable("live", null, false, null, 49.99m, null));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("sold")]
    public void A_box_that_is_not_live_is_unavailable(string state)
    {
        Assert.False(SquareFunction.Sellable(state, null, false, null, 49.99m, 7));
    }

    [Fact]
    public void A_box_with_no_price_is_unavailable()
    {
        Assert.False(SquareFunction.Sellable("live", null, false, null, null, 7));
        Assert.False(SquareFunction.Sellable("live", null, false, null, 0m, 7));
    }
}
