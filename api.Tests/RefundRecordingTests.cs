using NSL.Api.Services;
using Xunit;

/// <summary>
/// Refund recording — CheckoutFulfillment.RecordRefundAsync and the webhook's
/// FAILED/REJECTED re-flag.
///
/// Most of that code is SQL issued against dbo.payment_refunds and dbo.payments
/// over a live SqlConnection, with no seam to stand in for a database: the
/// transaction that ties the dedupe INSERT to the money UPDATE, the NULL
/// amount_cents handling, and the owed-based re-flag are all statements the
/// server evaluates against current row values. A C# re-implementation of those
/// CASE expressions would only test the re-implementation, so they are left to
/// the staging pass rather than covered by a test that cannot fail for the right
/// reason.
///
/// What IS reachable without a database is the pure decision the concurrency fix
/// rests on: which SQL error numbers mean "this refund is already recorded" and
/// therefore answer like a replay.
/// </summary>
public class RefundRecordingTests
{
    /// <summary>
    /// 2627 is a PRIMARY KEY / UNIQUE constraint violation, 2601 a unique-index
    /// one. WHERE NOT EXISTS does not serialise under read-committed snapshot
    /// (the Azure SQL default), so two simultaneous deliveries of one refund can
    /// both pass it and the loser hits the key. The loser is a replay — the
    /// winner is committing the identical row — and must answer like one, not
    /// escape as a 500 that buys the customer about eleven Square retries over
    /// 24 hours on the one path that gives them their money back.
    /// </summary>
    [Theory]
    [InlineData(2627)]
    [InlineData(2601)]
    public void A_duplicate_key_error_is_a_replay(int sqlErrorNumber)
        => Assert.True(CheckoutFulfillment.IsDuplicateKey(sqlErrorNumber));

    /// <summary>
    /// The catch must stay narrow. Every number below is a refund that genuinely
    /// did NOT record; calling one a replay would return false to the webhook,
    /// report the refund to Square as handled, and leave money that really went
    /// back to a customer absent from our books with nothing left to retry it.
    /// </summary>
    [Theory]
    [InlineData(1205)]      // deadlock victim
    [InlineData(-2)]        // command timeout
    [InlineData(547)]       // FK / CHECK constraint
    [InlineData(4060)]      // cannot open database
    [InlineData(18456)]     // login failed
    [InlineData(40613)]     // Azure SQL: database currently unavailable (failover)
    [InlineData(40197)]     // Azure SQL: service error, reconfiguration in progress
    [InlineData(0)]
    public void Any_other_sql_error_is_not_a_replay(int sqlErrorNumber)
        => Assert.False(CheckoutFulfillment.IsDuplicateKey(sqlErrorNumber));
}
