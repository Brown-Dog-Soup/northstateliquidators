using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Source pins on the delete route, for two regressions whose failure is SILENT:
/// the route goes on returning 200, the build stays green, and the damage shows up
/// as a customer's box missing from the floor or a payment nobody was told about.
///
/// BE CLEAR ABOUT WHAT THIS IS. It reads a source file and matches text. It
/// executes nothing, proves nothing about what SQL Server does with an UPDLOCK,
/// and would not notice a typo in a column name. The concurrency property it pins
/// the SHAPE of — that a sale committing mid-delete is seen — needs two
/// simultaneous transactions against a real database and is a staging-pass
/// question. What text CAN settle is that the statements are still there, still
/// whole, and still in the order that makes them mean anything.
///
/// Both regressions are edits a reasonable person makes:
///   * "hard-delete a pallet and all its line items" — someone sees an UPDATE
///     sitting in the middle of a delete route, decides it is a leftover and tidies
///     it back into a DELETE. Those rows are what a buyer paid for, and losing them
///     puts the order's money beyond the reach of every check in CheckoutFulfillment,
///     because they all start from a line that exists.
///   * "we already checked that at the top" — someone removes the second sold
///     check, or the lock claim above the first. Either one on its own is a race:
///     the first check is an unlocked COUNT under READ_COMMITTED_SNAPSHOT and
///     cannot see a fulfilment that has sold the box and not yet committed.
///
/// EVERYTHING BELOW READS THE ROUTE WITH COMMENTS STRIPPED. A pin satisfied by
/// prose is not a pin — and this route is heavily commented, including with the
/// very words the patterns look for.
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

    /// <summary>Line and block comments out; code in. No SQL here contains "//" or "/*".</summary>
    private static string StripComments(string s)
        => Regex.Replace(Regex.Replace(s, @"/\*[\s\S]*?\*/", " "), @"//[^\n]*", " ");

    /// <summary>
    /// JUST the delete route, comments stripped. Whole-file searches were the old
    /// weakness here: two of them could each be satisfied by a different statement
    /// somewhere else in an 850-line file while the one statement they were meant
    /// to describe had been taken apart. Scoping also makes "before" and "after"
    /// mean something, which is the whole of the second regression above.
    /// </summary>
    private static string Route()
    {
        var src = StripComments(Source());
        int start = src.IndexOf("[Function(\"DeletePallet\")]", StringComparison.Ordinal);
        int end = src.IndexOf("[Function(\"SoldToInventory\")]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No [Function(\"DeletePallet\")] in {RouteFile} — this test is no longer reading the delete route.");
        Assert.True(end > start, $"No [Function(\"SoldToInventory\")] after the delete route in {RouteFile} — the slice below would be the rest of the file.");
        return src[start..end];
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase);

    /// <summary>The statement that settles the box's order lines, pinned WHOLE.</summary>
    private static readonly Regex SettleStatement = Rx(
        @"UPDATE\s+dbo\.checkout_order_boxes\s+SET\s+outcome\s*=\s*'unavailable'\s*,\s*fulfilled_at\s*=\s*SYSUTCDATETIME\(\)\s+WHERE\s+manifest_id\s*=\s*@id\s+AND\s+outcome\s+IS\s+NULL");

    /// <summary>A read of "has this box been sold through the website".</summary>
    private static readonly Regex SoldCheck = Rx(
        @"COUNT\(\*\)\s+FROM\s+dbo\.checkout_order_boxes\s+WHERE\s+manifest_id\s*=\s*@id\s+AND\s+outcome\s*=\s*'sold'");

    private static readonly Regex ManifestDelete = Rx(@"DELETE\s+FROM\s+dbo\.manifests\s+WHERE\s+id\s*=\s*@id");

    // ---- the order lines survive -----------------------------------------------

    /// <summary>
    /// ONE match on ONE statement, not a column list here and a WHERE clause there.
    /// The old pair asserted the UPDATE's head and its predicate as two separate
    /// whole-file searches, so an edit that stripped "AND outcome IS NULL" off this
    /// statement passed both as long as that text survived anywhere else in the
    /// file — and it does survive elsewhere, in prose, which is why comments are
    /// stripped above as well.
    /// </summary>
    [Fact]
    public void The_route_settles_the_boxs_order_lines_in_one_whole_statement()
        => Assert.Matches(SettleStatement, Route());

    /// <summary>
    /// The regression itself. Nothing in this route may delete an order line: it is
    /// the record of what a buyer was charged, and the manifest going away is not a
    /// reason to forget it.
    /// </summary>
    [Fact]
    public void Nothing_in_the_route_deletes_an_order_line()
        => Assert.DoesNotMatch(
            Rx(@"DELETE\b(?:\s+\w+)?\s+FROM\s+(?:\[?dbo\]?\s*\.\s*)?\[?checkout_order_boxes\]?"),
            Route());

    /// <summary>
    /// And the blunt version of the same thing, because the pattern above still
    /// spells the statement out and a spelling is exactly what went wrong with the
    /// insert-side pins: an aliased delete (DELETE b FROM ... JOIN), a bracketed
    /// table name, a DELETE TOP (n), a DELETE with the FROM on the next line all
    /// read as "no delete found". This one asks a cruder question that none of
    /// those dodge — is the word DELETE anywhere near this table — and it can only
    /// be asked because comments are stripped first.
    /// </summary>
    [Fact]
    public void No_delete_of_any_spelling_comes_anywhere_near_the_order_lines()
        => Assert.DoesNotMatch(Rx(@"DELETE\b[\s\S]{0,160}?checkout_order_boxes"), Route());

    /// <summary>
    /// Anti-vacuity for the three above: if the route stopped naming these rows at
    /// all, every DoesNotMatch would pass on an empty file and the Matches would be
    /// the only thing holding. Assert the route is still in this business.
    /// </summary>
    [Fact]
    public void The_route_is_still_touching_order_lines_at_all()
        => Assert.True(Regex.Matches(Route(), "checkout_order_boxes", RegexOptions.IgnoreCase).Count >= 3,
            "The delete route no longer mentions checkout_order_boxes three times (one settle + two sold checks) — the pins above are checking nothing.");

    // ---- and the box itself is not destroyed under a sale ----------------------

    /// <summary>
    /// The lock claim. dbo.manifests is where a fulfilment decides — it re-reads
    /// that row WITH (UPDLOCK, ROWLOCK) before selling the box — so taking the same
    /// lock here first is what makes the COUNT below capable of seeing a sale at
    /// all. Without it the COUNT is a snapshot read that a not-yet-committed
    /// fulfilment is invisible to.
    /// </summary>
    [Fact]
    public void The_route_claims_the_manifest_row_under_a_lock_before_it_looks()
    {
        var route = Route();
        var claim = Rx(@"FROM\s+dbo\.manifests\s+WITH\s*\(\s*UPDLOCK").Match(route);
        Assert.True(claim.Success, "The delete route no longer takes a locking read on dbo.manifests — the sold check below is back to an unlocked snapshot COUNT.");
        var firstCheck = SoldCheck.Match(route);
        Assert.True(firstCheck.Success, "The delete route no longer checks whether the box was sold through the website.");
        Assert.True(claim.Index < firstCheck.Index,
            "The lock on dbo.manifests is taken AFTER the sold check, which is the same as not taking it: the check still reads a snapshot.");
    }

    /// <summary>
    /// TWO checks, and the second one is the point. Retaining the order line keeps
    /// the SALE record when a fulfilment commits mid-delete — that half always
    /// worked — but the manifest delete used to run unconditionally afterwards, so
    /// the box a customer had just bought was hard-deleted with its items and its
    /// history. A check that sits between the settling UPDATE (which holds a write
    /// lock on every one of this box's order lines by then) and the manifest DELETE
    /// is what refuses that.
    /// </summary>
    [Fact]
    public void A_sale_is_checked_for_again_after_the_update_and_before_the_box_is_destroyed()
    {
        var route = Route();
        var settle = SettleStatement.Match(route);
        var delete = ManifestDelete.Match(route);
        Assert.True(settle.Success, "No settling UPDATE found — see the whole-statement pin above.");
        Assert.True(delete.Success, "The route no longer deletes dbo.manifests; this test no longer describes it.");
        Assert.True(settle.Index < delete.Index, "The settling UPDATE now runs after the manifest is deleted.");

        var checks = SoldCheck.Matches(route);
        Assert.True(checks.Count >= 2,
            $"Expected a sold check before the work and another after it; found {checks.Count}. One check alone cannot refuse a delete for a sale that commits while it is running.");
        Assert.Contains(checks, m => m.Index > settle.Index && m.Index < delete.Index);
    }

    /// <summary>
    /// And that the second check can actually stop the delete. A check whose result
    /// is read and then not acted on is the exact failure this round is fixing, so
    /// the route has to still be capable of abandoning: a rollback and a 409 between
    /// the settling UPDATE and the manifest DELETE.
    ///
    /// Honest about its reach — this says the statements are in that span, not that
    /// they are the consequent of the right if. That much needs the database.
    /// </summary>
    [Fact]
    public void The_second_check_can_still_abandon_the_delete()
    {
        var route = Route();
        var between = route[SettleStatement.Match(route).Index..ManifestDelete.Match(route).Index];
        Assert.Matches(Rx(@"tx\.Rollback\(\)"), between);
        Assert.Matches(Rx(@"ConflictObjectResult"), between);
    }
}
