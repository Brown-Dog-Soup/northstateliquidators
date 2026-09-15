using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// One pin, on the single statement that closed the deleted-box money hole.
///
/// BE CLEAR ABOUT WHAT THIS IS. It reads a source file and matches text. It
/// executes nothing, proves nothing about what SQL Server does with that
/// statement, and would not notice a typo in a column name. It is here for
/// exactly one failure mode, which is not hypothetical — it is the shape the hole
/// had for months: someone reads "hard-delete a pallet and all its line items",
/// sees an UPDATE sitting in the middle of a delete route, decides it is a leftover
/// and tidies it back into a DELETE. The rows it would delete are what a buyer paid
/// for, and losing them puts the order's money beyond the reach of every check in
/// CheckoutFulfillment, because they all start from a line that exists.
///
/// The behaviour itself — that a payment arriving after a delete is flagged and the
/// right amount owed — needs a database and is a staging-pass question.
/// </summary>
public class DeleteRetainsOrderLinesTests
{
    private const string RouteFile = "api/Functions/PalletsFunction.cs";

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, RouteFile))) dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException(
                $"Could not find {RouteFile} above {AppContext.BaseDirectory} — the repo layout moved and this test is no longer checking anything.");
        return File.ReadAllText(Path.Combine(dir.FullName, RouteFile));
    }

    /// <summary>
    /// Anti-vacuity first: if the route stopped touching these rows at all, both
    /// assertions below would pass on an empty file.
    /// </summary>
    [Fact]
    public void The_delete_route_still_settles_the_boxs_order_lines()
        => Assert.Matches(
            new Regex(@"UPDATE\s+dbo\.checkout_order_boxes\s+SET\s+outcome\s*=\s*'unavailable'", RegexOptions.IgnoreCase),
            Source());

    /// <summary>
    /// And only the ones no fulfilment has already settled — a row already 'sold'
    /// keeps its outcome and its fulfilled_at, which is what makes this safe
    /// against a sale that committed after the route's unlocked guard read.
    /// </summary>
    [Fact]
    public void It_leaves_rows_a_fulfilment_has_already_settled_alone()
        => Assert.Matches(new Regex(@"WHERE\s+manifest_id\s*=\s*@id\s+AND\s+outcome\s+IS\s+NULL", RegexOptions.IgnoreCase), Source());

    /// <summary>
    /// The regression itself. Nothing in this route may delete an order line: they
    /// are the record of what a buyer was charged, and the manifest going away is
    /// not a reason to forget it.
    /// </summary>
    [Fact]
    public void Nothing_in_the_route_deletes_an_order_line()
        => Assert.DoesNotMatch(
            new Regex(@"DELETE\s+FROM\s+dbo\.checkout_order_boxes", RegexOptions.IgnoreCase),
            Source());
}
