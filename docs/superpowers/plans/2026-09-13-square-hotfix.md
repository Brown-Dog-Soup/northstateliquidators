# Square Webhook Hotfix (Phase 0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Square webhook from flagging floor (POS) sales as "needs refund", stop malformed webhook events from 500-ing, and stop Sold-to-inventory from fake-selling a box that has an outstanding invoice.

**Architecture:** A new pure static helper `SquareEvents` parses webhook JSON defensively and decides whether a payment belongs to our website/invoice products. `SquareFunction.Webhook` uses it and returns 200 without inserting for payments that are neither matched to a box nor from our products. A guard in `SoldToInventory` rejects invoiced boxes. One SQL script clears the two wrongly-flagged prod rows. A new xUnit test project covers the pure helper.

**Tech Stack:** .NET 8 isolated Azure Functions, Dapper, System.Text.Json, xUnit (new `api.Tests` project), T-SQL on Azure SQL.

**Spec:** `docs/superpowers/specs/2026-09-13-cart-checkout-design.md` §0 rows L1, L7, L11 and §6 "Phase 0".

## Global Constraints

- Branch: `feature/square-hotfix` off `main` (NOT the cart branch). Own PR, merged before the cart PR.
- No local Functions host is needed; `dotnet build api/api.csproj` and `dotnet test api.Tests/api.Tests.csproj` must pass locally (SDK 8.0.425 is installed). The PR preview build on GitHub Actions is the deploy-shaped compile check.
- Never delete `dbo.payments` rows except the two identified in Task 4; the table is the audit trail.
- Products treated as "ours": `ECOMMERCE_API` (payment links) and `INVOICES`. Observed prod value for the POS terminal: `RETAIL`.
- Commit messages end with the two attribution lines given in the session.
- Deploy is push-to-main; DB scripts are hand-applied with Invoke-Sqlcmd (see `nsl-stack-and-ops` memory: server `sql-nsl-prod-nc5h2y.database.windows.net`, db `sqldb-nsl-prod`, Entra token via `az account get-access-token --resource https://database.windows.net/`).

---

### Task 1: Test project + `SquareEvents` parser

**Files:**
- Create: `api.Tests/api.Tests.csproj`
- Create: `api.Tests/SquareEventsTests.cs`
- Create: `api/Services/SquareEvents.cs`
- Modify: `.gitignore` (add `**/bin/` and `**/obj/` if not already ignored — check with `git status` after the first build)

**Interfaces:**
- Produces: `NSL.Api.Services.SquareEvents` (static) with
  - `static string? EventType(JsonElement root)`
  - `static bool IsOurProduct(string? product)`
  - `static bool TryParsePayment(JsonElement root, out SquarePaymentEvent ev)`
  - `static bool TryParseRefund(JsonElement root, out SquareRefundEvent ev)`
  - `record SquarePaymentEvent(string PaymentId, string? Status, string? OrderId, long? AmountCents, string? Product)`
  - `record SquareRefundEvent(string RefundId, string? Status, string? PaymentId, long? AmountCents)`

- [ ] **Step 1: Create the branch**

```powershell
git checkout main; git pull; git checkout -b feature/square-hotfix
```

- [ ] **Step 2: Create the test project**

`api.Tests/api.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../api/api.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the failing tests**

`api.Tests/SquareEventsTests.cs`:
```csharp
using System.Text.Json;
using NSL.Api.Services;
using Xunit;

public class SquareEventsTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string RetailCash = """
    {"type":"payment.updated","event_id":"e1","data":{"type":"payment","id":"p1","object":{"payment":{
      "id":"PAY_RETAIL","status":"COMPLETED","order_id":"ORD_RETAIL","source_type":"CASH",
      "amount_money":{"amount":25000,"currency":"USD"},
      "application_details":{"square_product":"RETAIL"}}}}}
    """;

    private const string WebLink = """
    {"type":"payment.updated","event_id":"e2","data":{"type":"payment","id":"p2","object":{"payment":{
      "id":"PAY_WEB","status":"COMPLETED","order_id":"ORD_WEB",
      "amount_money":{"amount":100,"currency":"USD"},
      "application_details":{"square_product":"ECOMMERCE_API"}}}}}
    """;

    [Theory]
    [InlineData("ECOMMERCE_API", true)]
    [InlineData("INVOICES", true)]
    [InlineData("ecommerce_api", true)]
    [InlineData("RETAIL", false)]
    [InlineData("SQUARE_POS", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsOurProduct_matches_only_web_and_invoice_products(string? product, bool expected)
        => Assert.Equal(expected, SquareEvents.IsOurProduct(product));

    [Fact]
    public void EventType_reads_type_or_null()
    {
        Assert.Equal("payment.updated", SquareEvents.EventType(Root(WebLink)));
        Assert.Null(SquareEvents.EventType(Root("{}")));
        Assert.Null(SquareEvents.EventType(Root("{\"type\":123}")));
    }

    [Fact]
    public void TryParsePayment_reads_all_fields()
    {
        Assert.True(SquareEvents.TryParsePayment(Root(RetailCash), out var ev));
        Assert.Equal("PAY_RETAIL", ev.PaymentId);
        Assert.Equal("COMPLETED", ev.Status);
        Assert.Equal("ORD_RETAIL", ev.OrderId);
        Assert.Equal(25000, ev.AmountCents);
        Assert.Equal("RETAIL", ev.Product);
    }

    [Fact]
    public void TryParsePayment_tolerates_missing_optional_fields()
    {
        var json = """{"type":"payment.updated","data":{"object":{"payment":{"id":"PAY_MIN"}}}}""";
        Assert.True(SquareEvents.TryParsePayment(Root(json), out var ev));
        Assert.Equal("PAY_MIN", ev.PaymentId);
        Assert.Null(ev.Status);
        Assert.Null(ev.OrderId);
        Assert.Null(ev.AmountCents);
        Assert.Null(ev.Product);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type":"payment.updated","data":{}}""")]
    [InlineData("""{"type":"payment.updated","data":{"object":{"payment":{}}}}""")]
    [InlineData("""{"type":"payment.updated","data":{"object":{"payment":{"id":""}}}}""")]
    public void TryParsePayment_returns_false_without_a_payment_id(string json)
        => Assert.False(SquareEvents.TryParsePayment(Root(json), out _));

    [Fact]
    public void TryParseRefund_reads_fields()
    {
        var json = """{"type":"refund.updated","data":{"object":{"refund":{"id":"R1","status":"COMPLETED","payment_id":"PAY_WEB","amount_money":{"amount":100,"currency":"USD"}}}}}""";
        Assert.True(SquareEvents.TryParseRefund(Root(json), out var r));
        Assert.Equal("R1", r.RefundId);
        Assert.Equal("COMPLETED", r.Status);
        Assert.Equal("PAY_WEB", r.PaymentId);
        Assert.Equal(100, r.AmountCents);
    }

    [Fact]
    public void TryParseRefund_returns_false_without_refund_object()
        => Assert.False(SquareEvents.TryParseRefund(Root("""{"type":"refund.updated","data":{}}"""), out _));
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: build FAILS with `The type or namespace name 'SquareEvents' does not exist`.

- [ ] **Step 5: Write the implementation**

`api/Services/SquareEvents.cs`:
```csharp
using System.Text.Json;

namespace NSL.Api.Services;

public sealed record SquarePaymentEvent(string PaymentId, string? Status, string? OrderId, long? AmountCents, string? Product);
public sealed record SquareRefundEvent(string RefundId, string? Status, string? PaymentId, long? AmountCents);

/// <summary>
/// Defensive readers for Square webhook payloads. Everything is TryGet —
/// a malformed or unexpected event must never throw (a 500 makes Square
/// retry ~11 times over 24h). Also the one place that decides whether a
/// payment is "ours": the merchant account is shared with the floor POS,
/// so a payment.updated for a cash sale at the counter arrives here too.
/// </summary>
public static class SquareEvents
{
    /// <summary>application_details.square_product values produced by our
    /// payment links (ECOMMERCE_API) and wholesale invoices (INVOICES).</summary>
    private static readonly HashSet<string> OurProducts =
        new(StringComparer.OrdinalIgnoreCase) { "ECOMMERCE_API", "INVOICES" };

    public static bool IsOurProduct(string? product)
        => !string.IsNullOrEmpty(product) && OurProducts.Contains(product);

    public static string? EventType(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) &&
           t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    public static bool TryParsePayment(JsonElement root, out SquarePaymentEvent ev)
    {
        ev = null!;
        if (!TryObject(root, "data", out var data) || !TryObject(data, "object", out var obj) ||
            !TryObject(obj, "payment", out var payment))
            return false;
        var id = Str(payment, "id");
        if (string.IsNullOrEmpty(id)) return false;
        string? product = TryObject(payment, "application_details", out var app) ? Str(app, "square_product") : null;
        ev = new SquarePaymentEvent(id, Str(payment, "status"), Str(payment, "order_id"), Money(payment, "amount_money"), product);
        return true;
    }

    public static bool TryParseRefund(JsonElement root, out SquareRefundEvent ev)
    {
        ev = null!;
        if (!TryObject(root, "data", out var data) || !TryObject(data, "object", out var obj) ||
            !TryObject(obj, "refund", out var refund))
            return false;
        var id = Str(refund, "id");
        if (string.IsNullOrEmpty(id)) return false;
        ev = new SquareRefundEvent(id, Str(refund, "status"), Str(refund, "payment_id"), Money(refund, "amount_money"));
        return true;
    }

    private static bool TryObject(JsonElement el, string name, out JsonElement child)
    {
        child = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out child) &&
               child.ValueKind == JsonValueKind.Object;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Money(JsonElement el, string name)
        => TryObject(el, name, out var m) && m.TryGetProperty("amount", out var a) &&
           a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var n) ? n : null;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 14` (7 theory rows + 7 facts/rows).

- [ ] **Step 7: Make sure build output is ignored, then commit**

Run: `git status --short` — if `api.Tests/bin/` or `api.Tests/obj/` show up, append to `.gitignore`:
```
# .NET build output
**/bin/
**/obj/
```
(`api/bin` and `api/obj` already exist untracked in the repo history checks — confirm they are not tracked with `git ls-files api/bin | head -1`; if they ARE tracked, do NOT add the ignore rule for `api/`, only add `api.Tests/bin/` and `api.Tests/obj/`.)

```powershell
git add api.Tests api/Services/SquareEvents.cs .gitignore
git commit -m "test: add api.Tests project + SquareEvents webhook parser"
```

---

### Task 2: Webhook ignores floor payments and never throws on shape

**Files:**
- Modify: `api/Functions/SquareFunction.cs:96-196` (the `Webhook` function body after signature verification)

**Interfaces:**
- Consumes: `SquareEvents.EventType`, `SquareEvents.TryParsePayment`, `SquareEvents.TryParseRefund`, `SquareEvents.IsOurProduct` (Task 1).
- Produces: unchanged HTTP contract; new 200 bodies `{ ignored: "floor" }` and `{ ignored: "malformed" }`.

- [ ] **Step 1: Replace the parsing block**

Replace everything from `using var doc = JsonDocument.Parse(raw);` (line 114) through the refund branch and the `payment.updated` gate (through line 148, ending at `long? amount = ...;`) with:

```csharp
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException)
        {
            _log.LogWarning("SquareWebhook: body is not JSON ({Len} bytes)", raw.Length);
            return new OkObjectResult(new { ignored = "malformed" });
        }
        using var _ = doc;
        var root = doc.RootElement;
        var type = SquareEvents.EventType(root);

        // Refunds: mark our audit row REFUNDED (and clear the attention flag)
        // when Square confirms the money went back — whether we triggered it
        // via the admin button or someone did it in the Square Dashboard.
        if (type is "refund.updated" or "refund.created")
        {
            if (!SquareEvents.TryParseRefund(root, out var refund))
                return new OkObjectResult(new { ignored = "malformed" });
            if (refund.Status == "COMPLETED" && refund.PaymentId != null)
            {
                await using var rconn = await _sql.OpenAsync(ct);
                var n = await rconn.ExecuteAsync(
                    "UPDATE dbo.payments SET status = 'REFUNDED', needs_refund = 0 WHERE square_payment_id = @pid",
                    new { pid = refund.PaymentId });
                _log.LogInformation("SquareWebhook: refund COMPLETED for payment {PaymentId} ({N} row updated)", refund.PaymentId, n);
            }
            return new OkObjectResult(new { refund = refund.Status });
        }

        if (type != "payment.updated" && type != "payment.created")
            return new OkObjectResult(new { ignored = type });   // subscribed but not relevant

        if (!SquareEvents.TryParsePayment(root, out var pay))
            return new OkObjectResult(new { ignored = "malformed" });
        if (pay.Status != "COMPLETED")
            return new OkObjectResult(new { ignored = pay.Status });

        var paymentId = pay.PaymentId;
        var orderId   = pay.OrderId;
        long? amount  = pay.AmountCents;
```

- [ ] **Step 2: Gate the insert on "ours OR matches a box"**

Replace the block from `await using var conn = await _sql.OpenAsync(ct);` (old line 150) through the `if (box == null) { ... }` unmatched branch (old line 174) with:

```csharp
        await using var conn = await _sql.OpenAsync(ct);

        // The merchant account is shared with the floor POS. Match by order
        // first; a payment that matches no box AND did not come from our
        // payment links / invoices is a counter sale — not ours, not logged.
        var box = orderId == null ? null : await conn.QueryFirstOrDefaultAsync(
            "SELECT id, pallet_number, publish_state FROM dbo.manifests WHERE checkout_order_id = @oid",
            new { oid = orderId });
        if (box == null && !SquareEvents.IsOurProduct(pay.Product))
        {
            _log.LogInformation("SquareWebhook: ignoring {Product} payment {PaymentId} (floor/other)", pay.Product ?? "?", paymentId);
            return new OkObjectResult(new { ignored = "floor" });
        }

        // Idempotency anchor: one row per Square payment, ever.
        var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, amount_cents, currency, status, event_json)
SELECT @pid, @oid, @amt, 'USD', 'COMPLETED', @json
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
            new { pid = paymentId, oid = orderId, amt = amount, json = raw });
        if (inserted == 0)
            return new OkObjectResult(new { duplicate = true });   // retry/replay — already handled

        if (box == null)
        {
            // Money arrived from OUR product for an order we can't match — flag for a human.
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                new { pid = paymentId });
            _log.LogError("SquareWebhook: COMPLETED {Product} payment {PaymentId} matched no box (order {OrderId})",
                pay.Product, paymentId, orderId);
            return new OkObjectResult(new { unmatched = true });
        }
```

The rest of the function (already-sold branch, mark sold, history) stays as is.

- [ ] **Step 3: Build**

Run: `dotnet build api/api.csproj`
Expected: `Build succeeded. 0 Error(s)`. (Warnings about the discarded `_` variable name are fine; if CS0136 complains about `_` reuse, rename to `docScope`.)

- [ ] **Step 4: Commit**

```powershell
git add api/Functions/SquareFunction.cs
git commit -m "fix(square): ignore floor POS payments in webhook; never 500 on malformed events"
```

---

### Task 3: Sold-to-inventory refuses a box with an outstanding invoice

**Files:**
- Modify: `api/Functions/PalletsFunction.cs:499-505` (`SoldToInventory`, the first query)

**Interfaces:** none new.

- [ ] **Step 1: Extend the pre-check query and add the guard**

Replace:
```csharp
        var orig = await conn.QueryFirstOrDefaultAsync(
            "SELECT publish_state, checkout_link_id FROM dbo.manifests WHERE id = @id", new { id });
        if (orig == null) return new NotFoundResult();
```
with:
```csharp
        var orig = await conn.QueryFirstOrDefaultAsync(
            "SELECT publish_state, checkout_link_id, invoice_id FROM dbo.manifests WHERE id = @id", new { id });
        if (orig == null) return new NotFoundResult();
        if (orig.invoice_id != null)
            return new ConflictObjectResult(new { error = "This box has an outstanding Square invoice — cancel the invoice first, or wait for it to be paid." });
```

- [ ] **Step 2: Build**

Run: `dotnet build api/api.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```powershell
git add api/Functions/PalletsFunction.cs
git commit -m "fix(admin): Sold-to-inventory refuses a box with an outstanding invoice"
```

---

### Task 4: SQL to clear the two flagged floor payments

**Files:**
- Create: `db/hotfix-floor-payments.sql`

- [ ] **Step 1: Write the script**

```sql
-- ----------------------------------------------------------------------------
-- Hotfix 2026-09-13: the Square webhook flagged floor (POS) sales as UNMATCHED
-- / needs_refund because the merchant account is shared with the counter.
-- Remove only rows whose event payload says square_product = RETAIL (the POS)
-- and that were never matched to a box. Print what is removed.
-- ----------------------------------------------------------------------------
SELECT square_payment_id, amount_cents, created_at,
       JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') AS product
FROM dbo.payments
WHERE status = 'UNMATCHED' AND manifest_id IS NULL
  AND JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') NOT IN ('ECOMMERCE_API','INVOICES');

DELETE FROM dbo.payments
WHERE status = 'UNMATCHED' AND manifest_id IS NULL
  AND JSON_VALUE(event_json, '$.data.object.payment.application_details.square_product') NOT IN ('ECOMMERCE_API','INVOICES');

PRINT 'hotfix-floor-payments: removed ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' floor payment row(s).';
```

- [ ] **Step 2: Commit (do NOT apply yet — apply after the code is deployed, Task 5)**

```powershell
git add db/hotfix-floor-payments.sql
git commit -m "db: hotfix script clearing floor payments wrongly flagged UNMATCHED"
```

---

### Task 5: PR, deploy, apply SQL, verify

**Files:** none.

- [ ] **Step 1: Push and open the PR**

```powershell
git push -u origin feature/square-hotfix
gh pr create --title "Square hotfix: ignore floor POS payments, guard invoiced boxes, harden webhook parsing" --body-file - <<'EOF'
## Why
Prod `dbo.payments` holds two $250 CASH/RETAIL counter sales (2026-09-11) flagged UNMATCHED / needs_refund: the webhook subscription covers the whole merchant account and the handler flagged everything it could not match. Found in the 2026-09-13 cart design review (spec §0 L1, L7, L11).

## What
- `SquareEvents` parser (TryGet everywhere; new `api.Tests` xUnit project covers it)
- Webhook: unmatched payments are only flagged when `square_product` is ECOMMERCE_API or INVOICES; RETAIL/POS → 200 `ignored: floor`, no row
- Malformed events → 200 instead of 500 (Square retries 500s ~11×)
- Sold-to-inventory → 409 when the box has an outstanding invoice
- `db/hotfix-floor-payments.sql` removes the two flagged rows (apply after deploy)

## Verify
- Preview build green
- After merge: run the SQL; ring up a test sale on the terminal → no new UNMATCHED row

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01SBzv2wxVMJKqKEsddmWY2Q
EOF
```

- [ ] **Step 2: Wait for "Build and Deploy" to succeed on the PR**

Run: `gh pr checks --watch`
Expected: `Build and Deploy  pass`.

- [ ] **Step 3: Merge (Jeff's call — ask before merging)**

```powershell
gh pr merge --merge
```

- [ ] **Step 4: Apply the SQL after the prod deploy finishes**

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -InputFile db/hotfix-floor-payments.sql -Verbose
```
Expected: two rows listed, then `removed 2 floor payment row(s)`.

- [ ] **Step 5: Verify**

```powershell
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -Query "SELECT status, needs_refund, COUNT(*) n FROM dbo.payments GROUP BY status, needs_refund"
```
Expected: only `COMPLETED / False / 1`. Then open `staff/sales.html` → the "Needs attention" panel is hidden. Ask Rob to ring one small cash sale on the terminal; re-run the query → still no UNMATCHED row.

- [ ] **Step 6: Update the design doc**

Append to `SQUARE-INTEGRATION.md` under "Key mechanics":
```
- **Shared account:** the webhook fires for floor POS sales too. Only payments
  with `application_details.square_product` ECOMMERCE_API or INVOICES are
  ours; unmatched payments from other products are ignored (2026-09-13 hotfix).
```
```powershell
git checkout main; git pull
git add SQUARE-INTEGRATION.md; git commit -m "docs: note shared-account webhook filter"; git push
```
