using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// A source-text pin on the anonymous read path, in the same spirit as
/// <see cref="SchemaContractTests"/>: both halves of the contract are string
/// literals in this repository, so it can be checked without a database.
///
/// WHY IT EXISTS. /api/public/pallets used to read dbo.v_public_pallets and
/// nothing else, and that view is a wall — it had already dropped total_cost,
/// wholesale, notes and sold_to_inventory_at before the C# ever saw a row, so
/// even a careless SELECT * there could only surface customer-safe columns. To
/// reach the two Hot Deals columns (db/hot-deal-toggle.sql adds them to
/// dbo.manifests, deliberately without re-declaring the views) the query now
/// JOINs the base table. The column list it names is correct — but the wall is
/// gone, and what replaced it is discipline. One `m.*`, or one cost column
/// pasted in while debugging a margin question and left behind, publishes our
/// buy price to an anonymous GET behind every page of the site.
///
/// WHAT IT ACTUALLY CHECKS, and it is worth being plain: it reads the C# file
/// as TEXT and the db/*.sql files as TEXT. It executes nothing, opens no
/// connection, and proves nothing about what SQL Server returns. A query that
/// satisfies every assertion here can still be wrong in ways only the database
/// can tell you. What it does catch is exactly the drift the review named — the
/// join to dbo.manifests widening, by wildcard or by one more column, past the
/// two it was opened for.
///
/// WHAT IT DOES NOT COVER. Only PalletsFunction.cs. Other files may serve
/// anonymous routes; this pin does not know about them.
/// </summary>
public class PublicFeedColumnsTests
{
    private const string SourceFile = "api/Functions/PalletsFunction.cs";
    private const string SchemaFile = "db/schema.sql";

    /// <summary>
    /// Walk up from the test binary until the source file is underfoot. Throws
    /// rather than returning null: a test that cannot find its inputs must fail
    /// loudly, never pass by having nothing to check.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, SourceFile)))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException(
                $"Could not find {SourceFile} above {AppContext.BaseDirectory} — the repo layout moved and this test is no longer checking anything.");
        return dir.FullName;
    }

    /// <summary>A C# verbatim string literal: @"..." with "" for a quote.</summary>
    private static readonly Regex VerbatimString =
        new(@"@""((?:[^""]|"""")*)""", RegexOptions.Singleline);

    /// <summary>
    /// Every SQL statement an ANONYMOUS "public/..." route in PalletsFunction.cs
    /// hands to Dapper, keyed by the route's [Function] name.
    ///
    /// Routes are discovered from the source, not listed here, so a third public
    /// route added later is covered the day it is written. The set found is
    /// pinned separately (see
    /// <see cref="The_public_routes_this_pin_guards_are_all_still_being_found"/>)
    /// because a finder that silently matched nothing would turn every
    /// assertion below into a test that always passes.
    /// </summary>
    private static List<(string Route, string Sql)> PublicRouteSql()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), SourceFile));
        var marks = Regex.Matches(src, @"\[Function\(""(\w+)""\)\]").ToList();
        var found = new List<(string, string)>();

        for (var i = 0; i < marks.Count; i++)
        {
            var start = marks[i].Index;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : src.Length;
            var body = src[start..end];

            // The route prefix is the real signal, not AuthorizationLevel: the
            // staff routes are Anonymous too (Static Web Apps gates them at the
            // edge), so only "public/..." says "no login stands between this
            // SELECT and the internet".
            if (!Regex.IsMatch(body, @"Route\s*=\s*""public/")) continue;

            foreach (Match lit in VerbatimString.Matches(body))
            {
                var sql = lit.Groups[1].Value;
                if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                    found.Add((marks[i].Groups[1].Value, sql));
            }
        }
        return found;
    }

    // --------------------------------------------------------------- the wall

    /// <summary>
    /// No wildcard anywhere in a public statement. This is the one that stops
    /// `SELECT v.*, m.*` — the single edit that would turn the join into a full
    /// dump of dbo.manifests, cost and notes and invoice URLs included.
    ///
    /// Deliberately absolute: it bans `*` outright rather than trying to tell a
    /// column wildcard from a COUNT(*). If a public route ever genuinely needs
    /// COUNT(*), narrow this on purpose and say why — do not soften it to "any *
    /// not preceded by COUNT", which is how a ban stops meaning anything.
    /// </summary>
    [Fact]
    public void No_public_statement_selects_a_wildcard()
    {
        var statements = PublicRouteSql();
        Assert.NotEmpty(statements);

        foreach (var (route, sql) in statements)
            Assert.False(sql.Contains('*'),
                $"{route} in {SourceFile} contains a '*'. Public statements name every column explicitly — " +
                $"a wildcard over the joined dbo.manifests publishes cost, wholesale, notes and invoice data " +
                $"to an anonymous request.\n{sql}");
    }

    /// <summary>
    /// The columns the join to dbo.manifests may reach: the join key and the two
    /// Hot Deals columns. Nothing else.
    ///
    /// This is the pin that actually replaces the view's wall, and it needs no
    /// list of forbidden names to do it — it reads the alias out of the JOIN and
    /// asserts the whole set of columns reached through it. Anything new — a
    /// cost column, a note, an invoice URL, a column that does not exist yet —
    /// fails by default rather than by having been thought of in advance.
    ///
    /// Adding a third manifests column to the public feed is expected to fail
    /// here. That is the review step: satisfy yourself the column is safe for an
    /// anonymous customer to read, then add it to the list below.
    /// </summary>
    [Fact]
    public void The_public_feed_reaches_dbo_manifests_for_the_hot_deal_columns_and_nothing_else()
    {
        var allowed = new[] { "id", "is_hot_deal", "hot_deal_at" };
        var joins = 0;

        foreach (var (route, sql) in PublicRouteSql())
        {
            foreach (Match join in Regex.Matches(sql, @"JOIN\s+dbo\.manifests\s+(?:AS\s+)?(\w+)\b", RegexOptions.IgnoreCase))
            {
                joins++;
                var alias = join.Groups[1].Value;

                Assert.False(Regex.IsMatch(sql, $@"\b{Regex.Escape(alias)}\s*\.\s*\*"),
                    $"{route} in {SourceFile} selects {alias}.* off dbo.manifests.\n{sql}");

                var reached = Regex.Matches(sql, $@"\b{Regex.Escape(alias)}\s*\.\s*(\w+)")
                                   .Select(m => m.Groups[1].Value)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

                foreach (var col in reached)
                    Assert.True(allowed.Contains(col, StringComparer.OrdinalIgnoreCase),
                        $"{route} reads dbo.manifests.{col} on an anonymous route. dbo.v_public_pallets used to " +
                        $"make that impossible; the join in {SourceFile} does not. Allowed through the join: " +
                        $"{string.Join(", ", allowed)}. Found: {string.Join(", ", reached)}.\n{sql}");
            }
        }

        // Anti-vacuity. If the join is ever removed — the views re-declared to
        // carry the columns, say — the wall is back and this test has nothing
        // left to guard. Delete it then, deliberately, rather than leaving it
        // green over an empty loop.
        Assert.True(joins > 0,
            $"No 'JOIN dbo.manifests <alias>' found in any public statement in {SourceFile}. Either the join was " +
            $"removed, in which case delete this test on purpose, or it was reformatted past this pin.");
    }

    /// <summary>
    /// A second, blunter net for the same risk, catching what the alias scan
    /// cannot: a cost column pasted in UNQUALIFIED, or pulled through some other
    /// alias, or a view column that quietly starts exposing margin. Any
    /// identifier containing cost / wholesale / margin / profit in a public
    /// statement is a mistake until a human decides otherwise.
    /// </summary>
    [Fact]
    public void No_public_statement_names_a_cost_or_margin_identifier()
    {
        var statements = PublicRouteSql();
        Assert.NotEmpty(statements);

        foreach (var (route, sql) in statements)
        {
            var hits = Regex.Matches(sql, @"\b\w*(?:cost|wholesale|margin|profit)\w*\b", RegexOptions.IgnoreCase)
                            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(hits.Count == 0,
                $"{route} in {SourceFile} names {string.Join(", ", hits)} — our margins, on an anonymous route.\n{sql}");
        }
    }

    /// <summary>
    /// The named internal columns, spelled out. The two tests above are the
    /// general guards; this is the readable record of WHAT is being kept off the
    /// public feed, and it catches the internal fields that carry no "cost" in
    /// their name — the auction lot we bought from, staff notes, the buyer's
    /// invoice URL, and sold_to_inventory_at, which would reveal a ghost box as
    /// a fake sale.
    ///
    /// Every name is cross-checked against the schema below, so a rename cannot
    /// leave a line here silently guarding a column that no longer exists while
    /// the real one flows through.
    /// </summary>
    internal static readonly string[] InternalColumns =
    {
        "total_cost", "unit_cost", "wholesale_price",
        "notes", "source", "pallet_reference", "status", "sell_mode",
        "is_ghost", "archived_at", "sold_to_inventory_at",
        "invoice_id", "invoice_url",
        "checkout_link_id", "checkout_order_id", "checkout_url", "checkout_created_at",
    };

    [Fact]
    public void No_public_statement_names_an_internal_column()
    {
        var statements = PublicRouteSql();
        Assert.NotEmpty(statements);

        foreach (var (route, sql) in statements)
            foreach (var col in InternalColumns)
                Assert.False(Regex.IsMatch(sql, $@"\b{Regex.Escape(col)}\b", RegexOptions.IgnoreCase),
                    $"{route} in {SourceFile} names '{col}', an internal column, on an anonymous route.\n{sql}");
    }

    // ------------------------------------------------------ keeping it honest

    /// <summary>
    /// Every name in <see cref="InternalColumns"/> is a column this schema
    /// really declares on dbo.manifests or dbo.line_items. Without this, a typo
    /// or a rename turns a line of that list into a guard against a string that
    /// can never appear — green forever, protecting nothing.
    /// </summary>
    [Fact]
    public void Every_internal_column_named_here_is_one_the_schema_declares()
    {
        var declared = DeclaredColumns("manifests").Concat(DeclaredColumns("line_items"))
                                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(declared);
        foreach (var col in InternalColumns)
            Assert.True(declared.Contains(col),
                $"'{col}' is guarded by this test but is not declared on dbo.manifests or dbo.line_items anywhere " +
                $"in db/*.sql. It was renamed or misspelled, and this file has been guarding nothing.");
    }

    /// <summary>
    /// And the same for the columns the join DOES let through: both are really
    /// declared, by db/hot-deal-toggle.sql, so the allowlist above describes the
    /// schema rather than a pair of hopeful strings.
    /// </summary>
    [Theory]
    [InlineData("is_hot_deal")]
    [InlineData("hot_deal_at")]
    public void The_hot_deal_columns_the_join_exists_for_are_declared(string column)
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), "db/hot-deal-toggle.sql"));
        Assert.Contains($"ALTER TABLE dbo.manifests ADD {column} ", sql);
        Assert.Contains($"COL_LENGTH('dbo.manifests', '{column}')", sql);
    }

    /// <summary>
    /// The routes this pin guards, by name and statement count. Assert.NotEmpty
    /// above only says SOME public statement was found; PublicPallets — the feed
    /// behind every page, and the only one that joins the base table — could
    /// drop out of the scan on its own (a reformatted [Function] attribute, the
    /// route moved to another file, the query switched to an interpolated or raw
    /// string literal) while the other route kept this file green.
    ///
    /// A genuinely new public route is expected to fail this. Read its SQL,
    /// satisfy yourself it is customer-safe, and add it here.
    /// </summary>
    [Fact]
    public void The_public_routes_this_pin_guards_are_all_still_being_found()
    {
        var byRoute = PublicRouteSql().GroupBy(x => x.Route)
                                      .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["PublicPallets"] = 1,       // the feed; one statement, and the only join to dbo.manifests
                ["PublicPalletItems"] = 2,   // the manifest modal; the pallet gate, then the item list
            },
            byRoute);
    }

    /// <summary>
    /// Column names declared on dbo.{table}: the CREATE TABLE body in
    /// db/schema.sql plus every re-runnable ALTER TABLE ... ADD across db/*.sql,
    /// including the multi-column ADD form (db/square-invoices.sql,
    /// db/square-payments.sql). Deliberately dumb and line-oriented, which is
    /// the shape these files are written in.
    /// </summary>
    private static List<string> DeclaredColumns(string table)
    {
        var root = RepoRoot();
        var cols = new List<string>();

        var schema = File.ReadAllText(Path.Combine(root, SchemaFile));
        var create = Regex.Match(schema,
            @"CREATE\s+TABLE\s+dbo\." + Regex.Escape(table) + @"\s*\((.*?)\n\s*\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(create.Success, $"No CREATE TABLE dbo.{table} found in {SchemaFile}.");

        foreach (var raw in create.Groups[1].Value.Split('\n'))
            AddColumn(cols, raw);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "db"), "*.sql"))
        {
            var text = File.ReadAllText(file);
            foreach (Match add in Regex.Matches(text,
                         @"ALTER\s+TABLE\s+dbo\." + Regex.Escape(table) + @"\s+ADD\s+([^;]*);",
                         RegexOptions.IgnoreCase))
                // One ADD may declare several columns, comma-separated, one per line.
                foreach (var raw in add.Groups[1].Value.Split(','))
                    AddColumn(cols, raw);
        }

        return cols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One "name TYPE ..." line, if that is what it is.</summary>
    private static void AddColumn(List<string> into, string raw)
    {
        var line = raw;
        var cut = line.IndexOf("--", StringComparison.Ordinal);
        if (cut >= 0) line = line[..cut];
        line = line.Trim();
        if (Regex.IsMatch(line, @"^(CONSTRAINT|PRIMARY\s+KEY|UNIQUE|CHECK|FOREIGN\s+KEY)\b", RegexOptions.IgnoreCase))
            return;
        var col = Regex.Match(line, @"^(\w+)\s+\w");
        if (col.Success) into.Add(col.Groups[1].Value);
    }
}
