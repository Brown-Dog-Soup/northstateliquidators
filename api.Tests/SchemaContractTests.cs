using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// The one contract in this feature that CAN be checked without a database,
/// because both sides of it are string literals in this repository: the column
/// lists of our INSERTs against dbo.checkout_orders / dbo.checkout_order_boxes
/// versus the columns db/cart-checkout.sql declares NOT NULL with no default.
///
/// WHY it earns a test when the other SQL paths correctly did not. The rest of
/// this feature is behavioural — what SQL Server does with an UPDLOCK, whether a
/// transaction rolls back — and there is no seam that can be faked without lying
/// about it, so those are proved in the staging pass. This is not behavioural. A
/// later edit that adds a NOT NULL column with no default to checkout_orders, or
/// tightens CK_checkout_orders_kind, breaks these INSERTs at runtime, and the
/// worst of them throws INSIDE the invoice route AFTER a real Square invoice has
/// already been emailed to a real customer: the invoice is out, nothing records
/// it, and the webhook will later see the payment as UNMATCHED and flag it for a
/// refund the buyer is not owed. Nothing else in the repo would catch that.
///
/// WHAT THIS DOES NOT PROVE, and it is worth being plain about it. It compares
/// two texts. It does not execute a single statement, so it cannot tell you that
/// the values bound to those columns are the right TYPE, that the parameter
/// names line up with the anonymous objects Dapper is given, or that the
/// statement runs at all. A column list that satisfies this test can still fail
/// against the live database. What it does catch is the specific, silent drift
/// the review named: the schema growing a mandatory column that an INSERT here
/// does not fill.
/// </summary>
public class SchemaContractTests
{
    private const string SchemaFile = "db/cart-checkout.sql";

    /// <summary>
    /// Walk up from the test binary until the schema file is underfoot. Throws
    /// rather than returning null: a test that cannot find its inputs must fail
    /// loudly, never pass by having nothing to check.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, SchemaFile)))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException(
                $"Could not find {SchemaFile} above {AppContext.BaseDirectory} — the repo layout moved and this test is no longer checking anything.");
        return dir.FullName;
    }

    /// <summary>Strip a line comment, trailing comma and surrounding space.</summary>
    private static string Clean(string line)
    {
        var cut = line.IndexOf("--", StringComparison.Ordinal);
        if (cut >= 0) line = line[..cut];
        return line.Trim().TrimEnd(',').Trim();
    }

    private static bool IsNotNull(string s) => Regex.IsMatch(s, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);
    private static bool HasDefault(string s) => Regex.IsMatch(s, @"\bDEFAULT\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Every column of <paramref name="table"/> that an INSERT is obliged to
    /// supply: NOT NULL and no DEFAULT. Reads the CREATE TABLE body and the
    /// re-runnable ALTER TABLE ... ADD lines — the latter because adding a column
    /// to a table that already exists in production is the likelier shape of the
    /// edit this test exists to catch.
    ///
    /// The parse is deliberately dumb and line-oriented: one column per line,
    /// skip anything that starts with CONSTRAINT / PRIMARY KEY / CHECK / INDEX.
    /// That is exactly the shape db/cart-checkout.sql is written in. If this file
    /// ever grows a column definition that does not fit on its own line, this
    /// stops being worth fixing and starts being a parser under test — delete it
    /// and say so rather than making it cleverer.
    /// </summary>
    internal static List<string> RequiredColumns(string sql, string table)
    {
        var required = new List<string>();

        var create = Regex.Match(sql, @"CREATE\s+TABLE\s+dbo\." + Regex.Escape(table) + @"\s*\((.*?)\n\s*\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(create.Success, $"No CREATE TABLE dbo.{table} found in {SchemaFile}.");

        foreach (var raw in create.Groups[1].Value.Split('\n'))
        {
            var line = Clean(raw);
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^(CONSTRAINT|PRIMARY\s+KEY|UNIQUE|CHECK|FOREIGN\s+KEY|CREATE\s+INDEX)\b", RegexOptions.IgnoreCase))
                continue;
            // A column definition: name, then a type. Anything else is not one.
            var col = Regex.Match(line, @"^(\w+)\s+\w");
            if (!col.Success) continue;
            if (IsNotNull(line) && !HasDefault(line)) required.Add(col.Groups[1].Value);
        }

        foreach (Match add in Regex.Matches(sql,
                     @"ALTER\s+TABLE\s+dbo\." + Regex.Escape(table) + @"\s+ADD\s+(\w+)\s+[^;]*?;",
                     RegexOptions.IgnoreCase))
        {
            var text = Clean(add.Value);
            if (IsNotNull(text) && !HasDefault(text)) required.Add(add.Groups[1].Value);
        }

        return required.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Every INSERT against <paramref name="table"/> in our C# — file, and the
    /// column list it names. The column lists contain no nested parentheses, so
    /// "up to the first close paren" is the whole of it, newlines included.
    /// </summary>
    private static List<(string File, List<string> Columns)> InsertsInto(string repoRoot, string table)
    {
        var found = new List<(string, List<string>)>();
        var rx = new Regex(@"INSERT\s+INTO\s+dbo\." + Regex.Escape(table) + @"\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "api"), "*.cs", SearchOption.AllDirectories))
        {
            // Generated build output is not source we control.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in rx.Matches(text))
                found.Add((Path.GetRelativePath(repoRoot, file),
                           m.Groups[1].Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList()));
        }
        return found;
    }

    /// <summary>The literals a CHECK (col IN ('a','b')) constraint permits.</summary>
    private static List<string> AllowedValues(string sql, string column)
    {
        var m = Regex.Match(sql, @"CHECK\s*\(\s*" + Regex.Escape(column) + @"\s+IN\s*\(([^)]*)\)\s*\)", RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"No CHECK ({column} IN (...)) constraint found in {SchemaFile}.");
        return Regex.Matches(m.Groups[1].Value, @"'([^']*)'").Select(x => x.Groups[1].Value).ToList();
    }

    [Theory]
    [InlineData("checkout_orders")]
    [InlineData("checkout_order_boxes")]
    public void Every_insert_supplies_every_mandatory_column(string table)
    {
        var root = RepoRoot();
        var required = RequiredColumns(File.ReadAllText(Path.Combine(root, SchemaFile)), table);
        var inserts = InsertsInto(root, table);

        // Both guards are anti-vacuity: a parser that silently matched nothing
        // would otherwise turn this into a test that always passes.
        Assert.NotEmpty(required);
        Assert.NotEmpty(inserts);

        foreach (var (file, columns) in inserts)
            foreach (var col in required)
                Assert.True(columns.Contains(col, StringComparer.OrdinalIgnoreCase),
                    $"INSERT INTO dbo.{table} in {file} omits '{col}', which {SchemaFile} declares NOT NULL with no default. " +
                    $"Insert names: {string.Join(", ", columns)}");
    }

    /// <summary>
    /// The exact columns the schema currently makes mandatory, pinned. The test
    /// above proves the INSERTs keep up with the schema; this one proves the
    /// SCHEMA PARSE itself still sees what a human sees, so a regex that quietly
    /// stopped matching column definitions cannot make the check above vacuous.
    /// </summary>
    [Fact]
    public void The_mandatory_columns_are_the_ones_a_human_reads_off_the_schema()
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), SchemaFile));
        Assert.Equal(new[] { "square_order_id", "kind", "total_cents" },
            RequiredColumns(sql, "checkout_orders"));
        Assert.Equal(new[] { "square_order_id", "manifest_id", "amount_cents" },
            RequiredColumns(sql, "checkout_order_boxes"));
    }

    /// <summary>
    /// The other half of the review's worry: a TIGHTENED check constraint. Both
    /// kinds and all three statuses are written as bare literals by the routes
    /// (the invoice route writes 'invoice' and 'open'; the cart route and
    /// fulfilment's recovery path write 'link', 'paid' and 'canceled'), so
    /// narrowing either constraint breaks a live route with no other warning.
    ///
    /// Honest about what this is: a pin on the constraint, not a cross-check of
    /// which literal each INSERT writes. Matching a literal to its column would
    /// mean parsing VALUES positionally, at which point the test is mostly
    /// testing the parser.
    /// </summary>
    [Fact]
    public void The_kind_and_status_constraints_still_allow_what_the_routes_write()
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), SchemaFile));
        Assert.Equal(new[] { "link", "invoice" }, AllowedValues(sql, "kind"));
        Assert.Equal(new[] { "open", "paid", "canceled" }, AllowedValues(sql, "status"));
    }
}
