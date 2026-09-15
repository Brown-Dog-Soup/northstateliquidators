using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSL.Api.Functions;
using NSL.Api.Services;
using Xunit;

/// <summary>
/// What the shopper gets when the Square call itself fails.
///
/// The case that matters: HttpClient reports its OWN timeout as
/// TaskCanceledException, which derives from OperationCanceledException. A
/// filter of `ex is not OperationCanceledException` therefore let the one
/// failure the 502 "call us" copy exists for — Square hung or slow — escape as
/// a bare 500. These build a Square client whose transport throws and assert on
/// the answer, with a CancellationTokenSource that is deliberately NOT
/// cancelled: only a real shopper disconnect may propagate.
/// </summary>
public class CheckoutSquareFailureTests
{
    private sealed class ThrowingHandler(Func<Exception> ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw ex();
    }

    private sealed class ThrowingTransportFactory(Func<Exception> ex) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler(ex));
    }

    private static SquareFunction Build(Func<Exception> transportThrows)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SQUARE_ENVIRONMENT"] = "sandbox",
            ["SQUARE_CHECKOUT_ENABLED"] = "true",
            ["SQUARE_SANDBOX_ACCESS_TOKEN"] = "test-token",
            ["SQUARE_SANDBOX_LOCATION_ID"] = "LOC1",
            // Closed port, 1s timeout: nothing here is allowed to reach SQL.
            ["SqlConnectionString"] =
                "Server=tcp:127.0.0.1,1;Database=nsl;User ID=u;Password=p;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True",
        }).Build();
        var square = new SquareService(new ThrowingTransportFactory(transportThrows), cfg, NullLogger<SquareService>.Instance);
        var sql = new SqlService(cfg, NullLogger<SqlService>.Instance);
        var fulfill = new CheckoutFulfillment(square, NullLogger<CheckoutFulfillment>.Instance);
        return new SquareFunction(sql, square, fulfill, cfg, NullLogger<SquareFunction>.Instance);
    }

    private static Task<(SquareService.CartLink? Link, IActionResult? Error)> Attempt(
        SquareFunction fn, CancellationToken ct) =>
        fn.TryCreateCartLinkAsync(
            new[] { new CartLine(Guid.NewGuid(), "BOX #7 — Mega Box", 5000L) },
            new[] { 7 },
            "https://northstateliquidators.com/thanks.html?boxes=7",
            DeliveryMethod.Pickup, "pickup", deliveryFeeCents: 0, ct);

    private static string ErrorText(IActionResult? r) =>
        (string)Assert.IsType<ObjectResult>(r).Value!.GetType().GetProperty("error")!
            .GetValue(Assert.IsType<ObjectResult>(r).Value!)!;

    [Fact]
    public async Task A_square_timeout_is_the_502_call_us_answer_not_an_unhandled_500()
    {
        using var cts = new CancellationTokenSource();   // never cancelled: the shopper is still there

        var (link, error) = await Attempt(Build(() => new TaskCanceledException()), cts.Token);

        Assert.Null(link);
        Assert.Equal(502, Assert.IsType<ObjectResult>(error).StatusCode);
        Assert.Contains("(919) 526-0112", ErrorText(error));
        Assert.False(cts.IsCancellationRequested);
    }

    /// <summary>The same shape one layer up: a bare OperationCanceledException with our token clear.</summary>
    [Fact]
    public async Task An_operation_cancelled_with_our_token_clear_is_also_the_502()
    {
        using var cts = new CancellationTokenSource();

        var (link, error) = await Attempt(Build(() => new OperationCanceledException()), cts.Token);

        Assert.Null(link);
        Assert.Equal(502, Assert.IsType<ObjectResult>(error).StatusCode);
    }

    [Fact]
    public async Task Any_other_square_failure_is_still_the_502()
    {
        var (link, error) = await Attempt(
            Build(() => new InvalidOperationException("Square CreatePaymentLink -> 400")), CancellationToken.None);

        Assert.Null(link);
        Assert.Equal(502, Assert.IsType<ObjectResult>(error).StatusCode);
        Assert.Contains("(919) 526-0112", ErrorText(error));
    }

    /// <summary>
    /// The half the filter must keep: when OUR token really was signalled the
    /// shopper has gone, and the cancellation propagates rather than being
    /// dressed up as a Square outage.
    /// </summary>
    [Fact]
    public async Task A_genuine_shopper_disconnect_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Attempt(Build(() => new TaskCanceledException()), cts.Token));
    }
}
