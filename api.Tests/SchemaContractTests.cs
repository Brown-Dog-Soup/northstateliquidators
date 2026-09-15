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
    /// The text BETWEEN the parentheses of CREATE TABLE dbo.{table} — columns
    /// and table-level constraints, and nothing from any other table.
    ///
    /// Everything that reads the schema goes through this, and that is the
    /// point. The constraint reader used to scan the whole file for the first
    /// CHECK (col IN (...)) it could find, so a table declared EARLIER with a
    /// column of the same name — 'status' is not exactly rare — would silently
    /// re-point the pin at somebody else's constraint and go on passing while
    /// the one it exists to guard was narrowed underneath it.
    /// </summary>
    private static string CreateTableBody(string sql, string table)
    {
        var create = Regex.Match(sql, @"CREATE\s+TABLE\s+dbo\." + Regex.Escape(table) + @"\s*\((.*?)\n\s*\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(create.Success, $"No CREATE TABLE dbo.{table} found in {SchemaFile}.");
        return create.Groups[1].Value;
    }

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

        foreach (var raw in CreateTableBody(sql, table).Split('\n'))
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

    /// <summary>
    /// The literals dbo.{table}'s own CHECK (col IN ('a','b')) constraint
    /// permits. Scoped to that table's body — see <see cref="CreateTableBody"/>
    /// for the re-pointing this prevents. A constraint moved out to an ALTER
    /// TABLE fails here rather than falling back to a file-wide search: a pin
    /// that quietly finds something else is worse than one that stops.
    /// </summary>
    internal static List<string> AllowedValues(string sql, string table, string column)
    {
        var body = CreateTableBody(sql, table);
        var m = Regex.Match(body, @"CHECK\s*\(\s*" + Regex.Escape(column) + @"\s+IN\s*\(([^)]*)\)\s*\)", RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"No CHECK ({column} IN (...)) constraint inside CREATE TABLE dbo.{table} in {SchemaFile}.");
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
        Assert.Equal(new[] { "link", "invoice" }, AllowedValues(sql, "checkout_orders", "kind"));
        Assert.Equal(new[] { "open", "paid", "canceled" }, AllowedValues(sql, "checkout_orders", "status"));
    }

    /// <summary>
    /// The constraint reader, against a schema written to trip it: two tables,
    /// each with a 'status' check, the DECOY DECLARED FIRST. Fed as a string
    /// because the real db/cart-checkout.sql does not currently contain a second
    /// such table — and a scoping guard that is only exercised by a file that
    /// cannot exercise it is not a guard at all. The day someone adds one is
    /// exactly the day the old file-wide search would have started quietly
    /// pinning the wrong constraint.
    /// </summary>
    [Fact]
    public void A_check_constraint_on_another_table_cannot_be_mistaken_for_ours()
    {
        const string twoTables = @"
IF OBJECT_ID('dbo.decoy', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.decoy (
        id     INT          NOT NULL,
        status VARCHAR(16)  NOT NULL,
        CONSTRAINT CK_decoy_status CHECK (status IN ('wrong','answer'))
    );
END;
GO

IF OBJECT_ID('dbo.checkout_orders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_orders (
        square_order_id VARCHAR(64) NOT NULL,
        status          VARCHAR(16) NOT NULL CONSTRAINT DF_s DEFAULT 'open',
        CONSTRAINT CK_checkout_orders_status CHECK (status IN ('open','paid','canceled'))
    );
END;
GO
";
        Assert.Equal(new[] { "open", "paid", "canceled" }, AllowedValues(twoTables, "checkout_orders", "status"));
        Assert.Equal(new[] { "wrong", "answer" }, AllowedValues(twoTables, "decoy", "status"));
    }

    /// <summary>
    /// The INSERTs this file exists to guard, pinned BY FILE AND COUNT — the
    /// other half of the anti-vacuity the column parse already has.
    ///
    /// Assert.NotEmpty(inserts) above is not that. It says some INSERT was found
    /// somewhere under api/, which two of the three satisfy on their own. The
    /// finder's pattern — INSERT INTO dbo.{table} ( up to the first close paren —
    /// would miss a bracketed table name ([checkout_orders]), a column list
    /// containing a parenthesis, or an INSERT with no column list at all. Reformat
    /// the invoice route's statement into any of those and it drops silently out
    /// of the scan while this file goes on reporting green off the other two.
    /// That statement is the one whose failure mode is a real Square invoice
    /// already emailed to a real customer with nothing on our side recording it,
    /// so "we are still looking at it" has to be asserted, not assumed.
    ///
    /// Adding a genuinely new INSERT is expected to fail this test. Read the new
    /// statement, satisfy yourself it names every mandatory column, and move the
    /// number.
    /// </summary>
    [Theory]
    [InlineData("checkout_orders")]
    [InlineData("checkout_order_boxes")]
    public void The_inserts_this_test_guards_are_all_still_being_found(string table)
    {
        var byFile = InsertsInto(RepoRoot(), table)
            .GroupBy(i => i.File.Replace(Path.DirectorySeparatorChar, '/'))
            .ToDictionary(g => g.Key, g => g.Count());

        // SquareFunction.cs holds two: the cart route's and the invoice route's.
        // CheckoutFulfillment.cs holds the recovery path's one.
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["api/Functions/SquareFunction.cs"] = 2,
                ["api/Services/CheckoutFulfillment.cs"] = 1,
            },
            byFile);
    }

    // ------------------------------------------------- the acknowledge columns

    /// <summary>
    /// The second contract with both halves in this repository: the
    /// acknowledged_* columns the refund guard now rules on exist ONLY because
    /// db/cart-checkout.sql adds them to dbo.payments, and four statements in
    /// SquareFunction.cs read or write them by name.
    ///
    /// WHY THIS ONE EARNS A TEST. Those columns are not bookkeeping. They are the
    /// durable record that fulfilment declined to price a debt, and they are what
    /// stops /api/square-refund offering the rest of a payment on a row nobody
    /// could price — money back for boxes the buyer kept. If a later edit renames
    /// or drops them here, or a typo creeps into one of the statements, the
    /// endpoints throw at the moment a staff member is standing in front of a
    /// customer, and the acknowledge route — the only exit from a permanently
    /// flagged row — stops working entirely.
    ///
    /// WHAT IT DOES NOT PROVE. It compares two texts and nothing else: not the
    /// types, not that any statement parses, not that the migration has been
    /// APPLIED to the database (it has not — the script is still unapplied, and
    /// running against a database without these columns fails at runtime with
    /// this test green). Applying db/cart-checkout.sql before deploying is a
    /// staging step, not something any test here can stand in for.
    /// </summary>
    [Theory]
    [InlineData("acknowledged_at")]
    [InlineData("acknowledged_by")]
    public void The_acknowledge_columns_the_refund_guard_rests_on_are_declared(string column)
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), SchemaFile));
        Assert.Contains($"ALTER TABLE dbo.payments ADD {column} ", sql);
        Assert.Contains($"COL_LENGTH('dbo.payments', '{column}')", sql);
    }

    /// <summary>
    /// And the other direction, which is the one that catches a typo: every
    /// acknowledged_* name the function app puts in a SQL string is a column the
    /// migration actually declares. Anti-vacuity is the count — this must be
    /// finding references, or it is asserting nothing at all.
    /// </summary>
    [Fact]
    public void Every_acknowledge_column_the_code_names_is_one_the_migration_adds()
    {
        var root = RepoRoot();
        var code = File.ReadAllText(Path.Combine(root, "api/Functions/SquareFunction.cs"));
        var sql = File.ReadAllText(Path.Combine(root, SchemaFile));

        var named = Regex.Matches(code, @"\backnowledged_[a-z_]+\b")
                         .Select(m => m.Value).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "acknowledged_at", "acknowledged_by" }, named);
        foreach (var col in named)
            Assert.Contains($"ALTER TABLE dbo.payments ADD {col} ", sql);
    }
}
