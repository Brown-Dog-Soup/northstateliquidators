# Cart + Combined Checkout (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shoppers add any mix of live boxes to a cart and pay for all of them in one Square hosted checkout; every box on a paid order flips SOLD, with overlap between shoppers resolved by DB-first link cancellation and partial-refund flagging.

**Architecture:** Two new tables (`checkout_orders`, `checkout_order_boxes`) replace the per-box `manifests.checkout_*` columns as the correlation between a Square order and N boxes. A new `CheckoutFulfillment` service owns the one transactional routine that sells every box on a paid order, records per-box outcomes, and cancels competing links; the webhook and Reconcile both call it. The browser keeps an ids-only cart in localStorage and re-prices it from the public pallets feed. Square's hosted page is unchanged; the create call switches from `quick_pay` to a full `order`.

**Tech Stack:** .NET 8 isolated Azure Functions, Dapper + Microsoft.Data.SqlClient (explicit `SqlTransaction`), System.Text.Json, xUnit (`api.Tests`, created in the Phase 0 plan), vanilla JS/CSS (`js/site.js`, `css/site.css`), T-SQL, Square Checkout/Orders/Refunds APIs, GitHub Actions cron.

**Spec:** `docs/superpowers/specs/2026-09-13-cart-checkout-design.md` (§1–§7). The Phase 0 hotfix plan (`docs/superpowers/plans/2026-09-13-square-hotfix.md`) must be merged first — this plan assumes `SquareEvents` and `api.Tests` exist.

## Global Constraints

- Branch: `feature/cart-checkout` (already exists with the spec committed). Rebase on `main` after the hotfix PR merges.
- Cart cap: **20** boxes. Kill switch `SQUARE_CHECKOUT_ENABLED` gates every cart control and the checkout endpoint (503).
- localStorage key: `nsl.cart` (JSON array of manifest_id strings). Thanks page query: `?boxes=12,14` (legacy `?box=N` still accepted).
- Square: `checkout_options.enable_coupon=false`, `allow_tipping=false`, `ask_for_shipping_address` omitted; line item `uid` = manifest_id (max 60), `name` ≤ 512, `payment_note` ≤ 500; refund `idempotency_key` ≤ **45 chars**; `DeletePaymentLink` only counts as confirmed when the response has `cancelled_order_id`.
- Availability for a `kind='link'` order: `publish_state='live' AND archived_at IS NULL AND is_ghost=0 AND invoice_id IS NULL`. For `kind='invoice'`: `publish_state <> 'sold'`.
- Reconcile ages out open link orders after **7 days**.
- SOLD only via `EXEC dbo.sp_SetPublishState`; history rows via `PalletsFunction.InsertHistoryAsync` with `changed_by='square'`.
- DB migrations are hand-applied to prod with Invoke-Sqlcmd (server `sql-nsl-prod-nc5h2y.database.windows.net`, db `sqldb-nsl-prod`, Entra token). `db/cart-checkout.sql` is additive and idempotent; `db/cart-checkout-drop.sql` runs days after deploy.
- `dotnet build api/api.csproj` and `dotnet test api.Tests/api.Tests.csproj` must pass locally; the PR preview build is the deploy-shaped check.
- Never render cost/margin on any `/api/public/*` route or in `js/site.js`.
- Commit messages end with the two attribution lines given in the session.

---

### Task 1: Additive DB migration `db/cart-checkout.sql`

**Files:**
- Create: `db/cart-checkout.sql`
- Modify: `db/square-payments.sql:1-8` (header note only)

**Interfaces:**
- Produces tables `dbo.checkout_orders`, `dbo.checkout_order_boxes`; columns `dbo.payments.refund_due_cents BIGINT NULL`, `dbo.payments.refunded_cents BIGINT NOT NULL DEFAULT 0`. All later tasks' SQL depends on these exact names.

- [ ] **Step 1: Write the script**

```sql
-- ----------------------------------------------------------------------------
-- Cart + combined checkout (docs/superpowers/specs/2026-09-13-cart-checkout-design.md §2)
-- ADDITIVE + IDEMPOTENT. Apply BEFORE deploying the cart code, then re-run once
-- AFTER the deploy (the copy at the bottom picks up any link the old code
-- minted in between). The manifests.checkout_* columns are dropped later by
-- db/cart-checkout-drop.sql.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.checkout_orders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_orders (
        square_order_id  VARCHAR(64)   NOT NULL PRIMARY KEY,
        kind             VARCHAR(16)   NOT NULL,                  -- 'link' | 'invoice'
        square_link_id   VARCHAR(64)   NULL,                      -- links only
        url              NVARCHAR(500) NULL,
        status           VARCHAR(16)   NOT NULL DEFAULT 'open',   -- open | paid | canceled
        total_cents      BIGINT        NOT NULL,
        created_at       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        closed_at        DATETIME2     NULL,
        link_deleted_at  DATETIME2     NULL,                      -- Square confirmed (cancelled_order_id present)
        CONSTRAINT CK_checkout_orders_kind   CHECK (kind IN ('link','invoice')),
        CONSTRAINT CK_checkout_orders_status CHECK (status IN ('open','paid','canceled'))
    );
    CREATE INDEX IX_co_status ON dbo.checkout_orders (status, kind, created_at);
END;
GO

IF OBJECT_ID('dbo.checkout_order_boxes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_order_boxes (
        square_order_id  VARCHAR(64)      NOT NULL
            CONSTRAINT FK_cob_order REFERENCES dbo.checkout_orders (square_order_id),
        manifest_id      UNIQUEIDENTIFIER NOT NULL,   -- no FK on purpose: DeletePallet cleans up explicitly
        amount_cents     BIGINT           NOT NULL,   -- price at link time
        outcome          VARCHAR(16)      NULL,       -- NULL | 'sold' | 'unavailable'
        fulfilled_at     DATETIME2        NULL,
        CONSTRAINT PK_cob PRIMARY KEY (square_order_id, manifest_id),
        CONSTRAINT CK_cob_outcome CHECK (outcome IS NULL OR outcome IN ('sold','unavailable'))
    );
    CREATE INDEX IX_cob_manifest ON dbo.checkout_order_boxes (manifest_id);
END;
GO

IF COL_LENGTH('dbo.payments', 'refund_due_cents') IS NULL
    ALTER TABLE dbo.payments ADD refund_due_cents BIGINT NULL;
IF COL_LENGTH('dbo.payments', 'refunded_cents') IS NULL
    ALTER TABLE dbo.payments ADD refunded_cents BIGINT NOT NULL CONSTRAINT DF_payments_refunded DEFAULT 0;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_orders       TO nsl_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_order_boxes  TO nsl_api;
GO

-- Copy existing per-box links / invoices (old model) into the new tables.
-- Amount = current ask price (the old code never stored the link amount).
IF COL_LENGTH('dbo.manifests', 'checkout_order_id') IS NOT NULL
BEGIN
    INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status, total_cents, created_at)
    SELECT m.checkout_order_id,
           CASE WHEN m.invoice_id IS NOT NULL THEN 'invoice' ELSE 'link' END,
           m.checkout_link_id,
           COALESCE(m.checkout_url, m.invoice_url),
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN 'paid' ELSE 'open' END,
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
           COALESCE(m.checkout_created_at, SYSUTCDATETIME())
    FROM dbo.manifests m
    WHERE m.checkout_order_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.checkout_orders o WHERE o.square_order_id = m.checkout_order_id);

    INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, outcome, fulfilled_at)
    SELECT m.checkout_order_id, m.id,
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN 'sold' ELSE NULL END,
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN m.sold_at ELSE NULL END
    FROM dbo.manifests m
    WHERE m.checkout_order_id IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
                      WHERE b.square_order_id = m.checkout_order_id AND b.manifest_id = m.id);
END;
GO

PRINT 'cart-checkout: checkout_orders + checkout_order_boxes + payments.refund columns ready.';
```

- [ ] **Step 2: Mark the old script superseded**

At the top of `db/square-payments.sql`, after line 7 (`-- UNIQUE square_payment_id ...`), insert:
```sql
-- SUPERSEDED 2026-09: the manifests.checkout_* columns below are replaced by
-- dbo.checkout_orders / checkout_order_boxes (db/cart-checkout.sql) and dropped
-- by db/cart-checkout-drop.sql. Do NOT re-apply this file on prod.
```

- [ ] **Step 3: Dry-run the script against prod (it is additive; new tables are unused until deploy)**

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -InputFile db/cart-checkout.sql -Verbose
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -Query "SELECT kind, status, COUNT(*) n FROM dbo.checkout_orders GROUP BY kind, status; SELECT COUNT(*) boxes FROM dbo.checkout_order_boxes"
```
Expected: the PRINT line; then rows matching the current `manifests` state (as of 2026-09-13: 2 open links + 1 paid, 3 box rows). Run the script a second time → no errors, same counts (idempotent).

- [ ] **Step 4: Commit**

```powershell
git add db/cart-checkout.sql db/square-payments.sql
git commit -m "db: checkout_orders + checkout_order_boxes (additive cart migration)"
```

---

### Task 2: Square payloads (pure) + SquareService cart/refund/delete changes

**Files:**
- Create: `api/Services/SquarePayloads.cs`
- Create: `api.Tests/SquarePayloadsTests.cs`
- Modify: `api/Services/SquareService.cs:66-102` (replace `CreatePaymentLinkAsync`), `:135-146` (`DeletePaymentLinkAsync`), `:288-311` (`RefundPaymentAsync`), plus a new `OrderLineUidsAsync`

**Interfaces:**
- Produces:
  - `record CartLine(Guid ManifestId, string Name, long AmountCents)`
  - `static object SquarePayloads.CartLink(IReadOnlyList<CartLine> lines, string locationId, string redirectUrl, string idempotencyKey, string referenceId, string paymentNote, string supportEmail)`
  - `static string SquarePayloads.RefundKey(string paymentId, long amountCents)` — always 45 chars
  - `static string SquarePayloads.BoxLineName(int palletNumber, string? displayName)`
  - `static string SquarePayloads.PaymentNote(IEnumerable<int> palletNumbers)`
  - `record CartLink(string Id, string OrderId, string Url, long TotalCents)`
  - `Task<CartLink> SquareService.CreateCartPaymentLinkAsync(IReadOnlyList<CartLine> lines, string redirectUrl, string idempotencyKey, string referenceId, string paymentNote, CancellationToken ct)`
  - `Task<bool> SquareService.DeletePaymentLinkAsync(string linkId, CancellationToken ct)` — true = confirmed dead (`cancelled_order_id` present or 404)
  - `Task<JsonDocument> SquareService.RefundPaymentAsync(string paymentId, long amountCents, string? reason, CancellationToken ct)` — key from `RefundKey`
  - `Task<List<Guid>> SquareService.OrderLineUidsAsync(string orderId, CancellationToken ct)` — manifest ids parsed from `line_items[].uid`
  - `string SquareService.SupportEmail` (config `SQUARE_SUPPORT_EMAIL`, default `hello@northstateliquidators.com`)

- [ ] **Step 1: Write the failing tests**

`api.Tests/SquarePayloadsTests.cs`:
```csharp
using System.Text.Json;
using NSL.Api.Services;
using Xunit;

public class SquarePayloadsTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static JsonElement Build(params CartLine[] lines)
    {
        var payload = SquarePayloads.CartLink(lines, "LOC1", "https://x/thanks.html?boxes=1,2",
            "nsl-cart-abc", "NSL #1, #2", "NSL boxes #1, #2", "hello@example.com");
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    [Fact]
    public void CartLink_uses_order_not_quick_pay_and_one_line_per_box()
    {
        var root = Build(new CartLine(A, "BOX #1 — Tools", 18000), new CartLine(B, "BOX #2 — Toys", 25000));
        Assert.False(root.TryGetProperty("quick_pay", out _));
        var order = root.GetProperty("order");
        Assert.Equal("LOC1", order.GetProperty("location_id").GetString());
        Assert.Equal("NSL #1, #2", order.GetProperty("reference_id").GetString());
        var items = order.GetProperty("line_items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(A.ToString(), items[0].GetProperty("uid").GetString());
        Assert.Equal("1", items[0].GetProperty("quantity").GetString());
        Assert.Equal(18000, items[0].GetProperty("base_price_money").GetProperty("amount").GetInt64());
        Assert.Equal("USD", items[0].GetProperty("base_price_money").GetProperty("currency").GetString());
        Assert.Equal("BOX #2 — Toys", items[1].GetProperty("name").GetString());
        Assert.Equal("nsl-cart-abc", root.GetProperty("idempotency_key").GetString());
        Assert.Equal("NSL boxes #1, #2", root.GetProperty("payment_note").GetString());
    }

    [Fact]
    public void CartLink_disables_coupons_and_tips_and_sets_redirect_and_support_email()
    {
        var co = Build(new CartLine(A, "BOX #1", 100)).GetProperty("checkout_options");
        Assert.False(co.GetProperty("enable_coupon").GetBoolean());
        Assert.False(co.GetProperty("allow_tipping").GetBoolean());
        Assert.Equal("https://x/thanks.html?boxes=1,2", co.GetProperty("redirect_url").GetString());
        Assert.Equal("hello@example.com", co.GetProperty("merchant_support_email").GetString());
        Assert.False(co.TryGetProperty("ask_for_shipping_address", out _));
        var cf = co.GetProperty("custom_fields");
        Assert.Equal(1, cf.GetArrayLength());
        Assert.Equal("Name & phone for pickup", cf[0].GetProperty("title").GetString());
    }

    [Fact]
    public void BoxLineName_is_capped_at_512()
    {
        Assert.Equal("BOX #12 — Tools", SquarePayloads.BoxLineName(12, "Tools"));
        Assert.Equal("BOX #12 — NSL Box", SquarePayloads.BoxLineName(12, null));
        Assert.Equal(512, SquarePayloads.BoxLineName(12, new string('x', 600)).Length);
    }

    [Fact]
    public void PaymentNote_lists_boxes_and_is_capped_at_500()
    {
        Assert.Equal("NSL boxes #12, #14", SquarePayloads.PaymentNote(new[] { 12, 14 }));
        Assert.True(SquarePayloads.PaymentNote(Enumerable.Range(100000, 200)).Length <= 500);
    }

    [Fact]
    public void RefundKey_is_deterministic_45_chars_and_amount_sensitive()
    {
        var k1 = SquarePayloads.RefundKey("xWNLYnGXabPUAraqnJWIOjlKxVMZY", 25000);
        var k2 = SquarePayloads.RefundKey("xWNLYnGXabPUAraqnJWIOjlKxVMZY", 25000);
        var k3 = SquarePayloads.RefundKey("xWNLYnGXabPUAraqnJWIOjlKxVMZY", 100);
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(45, k1.Length);
        Assert.StartsWith("nslr-", k1);
        Assert.Equal(45, SquarePayloads.RefundKey(new string('p', 192), 1).Length);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: build FAILS — `SquarePayloads` / `CartLine` not found.

- [ ] **Step 3: Write `SquarePayloads.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace NSL.Api.Services;

/// <summary>One box on a cart checkout. ManifestId becomes the Square line-item uid.</summary>
public sealed record CartLine(Guid ManifestId, string Name, long AmountCents);

/// <summary>
/// Pure builders for the Square request bodies the cart needs. Kept free of
/// I/O so they are unit-testable; SquareService serializes and posts them.
/// Limits (Square docs, verified 2026-09-13): line name ≤512, line uid ≤60,
/// payment_note ≤500, redirect_url ≤2048, refund idempotency_key ≤45.
/// </summary>
public static class SquarePayloads
{
    public const string PickupFieldTitle = "Name & phone for pickup";

    public static object CartLink(IReadOnlyList<CartLine> lines, string locationId, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, string supportEmail)
        => new
        {
            idempotency_key = idempotencyKey,
            order = new
            {
                location_id = locationId,
                reference_id = Cap(referenceId, 40),
                line_items = lines.Select(l => new
                {
                    uid = l.ManifestId.ToString(),
                    name = Cap(l.Name, 512),
                    quantity = "1",
                    base_price_money = new { amount = l.AmountCents, currency = "USD" },
                    note = "Pickup in Wake Forest, NC"
                }).ToArray()
            },
            checkout_options = new
            {
                redirect_url = redirectUrl,
                enable_coupon = false,
                allow_tipping = false,
                merchant_support_email = supportEmail,
                custom_fields = new[] { new { title = PickupFieldTitle } }
            },
            payment_note = Cap(paymentNote, 500)
        };

    public static string BoxLineName(int palletNumber, string? displayName)
        => Cap($"BOX #{palletNumber} — {(string.IsNullOrWhiteSpace(displayName) ? "NSL Box" : displayName)}", 512);

    public static string PaymentNote(IEnumerable<int> palletNumbers)
        => Cap("NSL boxes " + string.Join(", ", palletNumbers.Select(n => "#" + n)), 500);

    /// <summary>Square caps refund idempotency keys at 45 chars; payment ids
    /// alone can be longer than that, so hash (payment, amount) → 40 hex + prefix.</summary>
    public static string RefundKey(string paymentId, long amountCents)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(paymentId + ":" + amountCents));
        return "nslr-" + Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: all SquarePayloadsTests pass (plus the Phase 0 tests).

- [ ] **Step 5: Change SquareService**

(a) Add the support email property. In the constructor (after `_webhookUrl = ...`):
```csharp
        SupportEmail = cfg["SQUARE_SUPPORT_EMAIL"] ?? "hello@northstateliquidators.com";
```
and next to the other properties:
```csharp
    public string SupportEmail { get; }
```

(b) Replace the whole `CreatePaymentLinkAsync` method (from its `/// <summary>` at line 66 through the closing brace at line 102) and the `PaymentLink` record with:
```csharp
    public sealed record CartLink(string Id, string OrderId, string Url, long TotalCents);

    /// <summary>
    /// One payment link for N boxes: a full `order` with one ad-hoc line item
    /// per box (uid = manifest_id) instead of quick_pay. Idempotency key is
    /// per attempt (nsl-cart-{guid}); reuse is decided by our DB, not Square.
    /// TotalCents comes from Square's own order total so amount checks never
    /// depend on our arithmetic.
    /// </summary>
    public async Task<CartLink> CreateCartPaymentLinkAsync(IReadOnlyList<CartLine> lines, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, CancellationToken ct)
    {
        var payload = SquarePayloads.CartLink(lines, LocationId, redirectUrl, idempotencyKey, referenceId, paymentNote, SupportEmail);
        using var client = Client();
        var resp = await client.PostAsync("/v2/online-checkout/payment-links",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square CreatePaymentLink failed {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square CreatePaymentLink -> {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        var link = doc.RootElement.GetProperty("payment_link");
        long total = lines.Sum(l => l.AmountCents);
        if (doc.RootElement.TryGetProperty("related_resources", out var rr) &&
            rr.TryGetProperty("orders", out var orders) && orders.GetArrayLength() > 0 &&
            orders[0].TryGetProperty("total_money", out var tm) && tm.TryGetProperty("amount", out var ta))
            total = ta.GetInt64();
        return new CartLink(
            link.GetProperty("id").GetString()!,
            link.GetProperty("order_id").GetString()!,
            link.GetProperty("url").GetString()!,
            total);
    }

    /// <summary>Manifest ids we stamped as line-item uids on a cart order (webhook fallback correlation).</summary>
    public async Task<List<Guid>> OrderLineUidsAsync(string orderId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        using var order = await RetrieveOrderAsync(orderId, ct);
        if (order == null) return ids;
        if (order.RootElement.TryGetProperty("order", out var o) && o.TryGetProperty("line_items", out var items))
            foreach (var li in items.EnumerateArray())
                if (li.TryGetProperty("uid", out var uid) && Guid.TryParse(uid.GetString(), out var g)) ids.Add(g);
        return ids;
    }
```

(c) Replace `DeletePaymentLinkAsync` (lines 135-146) with:
```csharp
    /// <summary>
    /// Delete (deactivate) a payment link. Returns true only when Square
    /// confirms the order was cancelled (response carries cancelled_order_id)
    /// or the link is already gone (404). Square has been seen returning 200
    /// without cancelled_order_id and leaving the link payable — callers
    /// must treat false as "still open, re-check later", never as deleted.
    /// </summary>
    public async Task<bool> DeletePaymentLinkAsync(string linkId, CancellationToken ct)
    {
        using var client = Client();
        var resp = await client.DeleteAsync($"/v2/online-checkout/payment-links/{Uri.EscapeDataString(linkId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Square DeletePaymentLink {LinkId} failed {Status}: {Body}", linkId, (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Square DeletePaymentLink -> {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(body);
        var confirmed = doc.RootElement.TryGetProperty("cancelled_order_id", out var c) &&
                        c.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(c.GetString());
        if (!confirmed)
            _log.LogWarning("Square DeletePaymentLink {LinkId}: 200 without cancelled_order_id — treating as still open", linkId);
        return confirmed;
    }
```

(d) In `RefundPaymentAsync`, replace `idempotency_key = $"nsl-refund-{paymentId}",` with `idempotency_key = SquarePayloads.RefundKey(paymentId, amountCents),` and update its summary to: `/// Refund (full or partial). Key = hash(payment, amount), ≤45 chars, so the same amount can't be refunded twice by a double click but a later different-amount refund is allowed.`

- [ ] **Step 6: Build**

Run: `dotnet build api/api.csproj`
Expected: errors ONLY in `SquareFunction.cs` and `PalletsFunction.cs` where `CreatePaymentLinkAsync` / `PaymentLink` / `DeletePaymentLinkAsync` (now returns bool — `await` of a bool inside a `try` is fine, but any `Task` typed variable will break) are used. Those call sites are rewritten in Tasks 4, 6, 7. To keep the branch compiling between tasks, temporarily add at the end of `SquareService`:
```csharp
    // TEMP shim removed in Task 4 — old single-box link creation.
    public sealed record PaymentLink(string Id, string OrderId, string Url);
    public async Task<PaymentLink> CreatePaymentLinkAsync(string name, long amountCents, string redirectUrl, string idempotencyKey, string? note, CancellationToken ct)
    {
        var l = await CreateCartPaymentLinkAsync(new[] { new CartLine(Guid.Empty, name, amountCents) }, redirectUrl, idempotencyKey, name, note ?? name, ct);
        return new PaymentLink(l.Id, l.OrderId, l.Url);
    }
```
Re-run the build. Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```powershell
git add api/Services/SquarePayloads.cs api/Services/SquareService.cs api.Tests/SquarePayloadsTests.cs
git commit -m "feat(square): order-based cart payment links, confirmed deletes, 45-char refund keys"
```

---

### Task 3: `Availability` rule (pure) + `CheckoutFulfillment` service

**Files:**
- Create: `api/Services/Availability.cs`
- Create: `api/Services/CheckoutFulfillment.cs`
- Create: `api.Tests/AvailabilityTests.cs`
- Modify: `api/Program.cs:21` (register the service)

**Interfaces:**
- Consumes: `SquareService.OrderLineUidsAsync`, `SquareService.DeletePaymentLinkAsync` (Task 2), `PalletsFunction.InsertHistoryAsync(conn, id, field, old, new, who, tx)` (exists), tables from Task 1.
- Produces:
  - `static bool Availability.ForLink(string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)`
  - `static bool Availability.ForInvoice(string? publishState)`
  - `static bool Availability.For(string kind, string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)`
  - `record FulfillResult(string Outcome, int Sold, int Unavailable, long RefundDueCents, List<int> PalletNumbers)` — `Outcome` ∈ `"duplicate" | "unmatched" | "fulfilled"`
  - `record CanceledLink(string OrderId, string? LinkId)`
  - `Task<FulfillResult> CheckoutFulfillment.FulfillOrderAsync(SqlConnection conn, string orderId, string paymentId, long? amountCents, string? rawJson, string source, CancellationToken ct)`
  - `Task<List<CanceledLink>> CheckoutFulfillment.CancelOpenLinksForBoxesAsync(SqlConnection conn, SqlTransaction tx, IReadOnlyCollection<Guid> manifestIds, string? exceptOrderId)`
  - `Task CheckoutFulfillment.RetireLinksAsync(SqlConnection conn, IEnumerable<CanceledLink> links, CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

`api.Tests/AvailabilityTests.cs`:
```csharp
using NSL.Api.Services;
using Xunit;

public class AvailabilityTests
{
    [Fact] public void Link_live_clean_box_is_available()
        => Assert.True(Availability.ForLink("live", null, false, null));
    [Theory]
    [InlineData("draft")] [InlineData("sold")] [InlineData("ghost")] [InlineData(null)]
    public void Link_non_live_states_are_unavailable(string? state)
        => Assert.False(Availability.ForLink(state, null, false, null));
    [Fact] public void Link_archived_is_unavailable()
        => Assert.False(Availability.ForLink("live", DateTime.UtcNow, false, null));
    [Fact] public void Link_ghost_flag_is_unavailable()
        => Assert.False(Availability.ForLink("live", null, true, null));
    [Fact] public void Link_invoiced_box_is_unavailable()
        => Assert.False(Availability.ForLink("live", null, false, "INV1"));
    [Theory]
    [InlineData("draft", true)] [InlineData("live", true)] [InlineData("sold", false)]
    public void Invoice_only_sold_blocks(string state, bool expected)
        => Assert.Equal(expected, Availability.ForInvoice(state));
    [Fact] public void For_dispatches_on_kind()
    {
        Assert.True(Availability.For("invoice", "draft", null, false, "INV1"));
        Assert.False(Availability.For("link", "draft", null, false, "INV1"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: build FAILS — `Availability` not found.

- [ ] **Step 3: Write `Availability.cs`**

```csharp
namespace NSL.Api.Services;

/// <summary>
/// The ONE definition of "can this order still sell this box" (spec §4).
/// A public cart link may only sell a box that is live, not archived, not a
/// ghost and not reserved by an outstanding wholesale invoice. An invoice
/// order may sell its (drafted) box as long as nobody else sold it first.
/// </summary>
public static class Availability
{
    public static bool ForLink(string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)
        => publishState == "live" && archivedAt == null && !isGhost && invoiceId == null;

    public static bool ForInvoice(string? publishState) => publishState != "sold";

    public static bool For(string kind, string? publishState, DateTime? archivedAt, bool isGhost, string? invoiceId)
        => kind == "invoice" ? ForInvoice(publishState) : ForLink(publishState, archivedAt, isGhost, invoiceId);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: all AvailabilityTests pass.

- [ ] **Step 5: Write `CheckoutFulfillment.cs`**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NSL.Api.Functions;

namespace NSL.Api.Services;

public sealed record FulfillResult(string Outcome, int Sold, int Unavailable, long RefundDueCents, List<int> PalletNumbers);
public sealed record CanceledLink(string OrderId, string? LinkId);

/// <summary>
/// The one routine that turns "Square says this order is paid" into SOLD
/// boxes (spec §4). Called by the webhook and by Reconcile. Everything from
/// the payments INSERT (idempotency anchor) to the competing-link cancel
/// happens in ONE SqlTransaction, so a crash mid-way rolls back the anchor
/// and Square's retry redoes the whole thing. Square deletes of the canceled
/// links happen AFTER commit, best-effort; Reconcile sweeps the rest.
/// </summary>
public sealed class CheckoutFulfillment
{
    private readonly SquareService _square;
    private readonly ILogger<CheckoutFulfillment> _log;

    public CheckoutFulfillment(SquareService square, ILogger<CheckoutFulfillment> log)
    {
        _square = square;
        _log = log;
    }

    public async Task<FulfillResult> FulfillOrderAsync(SqlConnection conn, string orderId, string paymentId,
        long? amountCents, string? rawJson, string source, CancellationToken ct)
    {
        List<CanceledLink> canceled = new();
        FulfillResult result;
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payments (square_payment_id, square_order_id, amount_cents, currency, status, event_json)
SELECT @pid, @oid, @amt, 'USD', 'COMPLETED', @json
WHERE NOT EXISTS (SELECT 1 FROM dbo.payments WHERE square_payment_id = @pid)",
                    new { pid = paymentId, oid = orderId, amt = amountCents, json = rawJson }, transaction: tx);
                if (inserted == 0)
                {
                    tx.Rollback();
                    return new FulfillResult("duplicate", 0, 0, 0, new List<int>());
                }

                var order = await conn.QueryFirstOrDefaultAsync(
                    "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                if (order == null)
                {
                    // Fallback correlation: the line-item uids ARE our manifest ids.
                    var ids = _square.Configured ? await _square.OrderLineUidsAsync(orderId, ct) : new List<Guid>();
                    if (ids.Count == 0)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                            new { pid = paymentId }, transaction: tx);
                        tx.Commit();
                        _log.LogError("Fulfill: payment {PaymentId} matched no order and no line uids (order {OrderId})", paymentId, orderId);
                        return new FulfillResult("unmatched", 0, 0, 0, new List<int>());
                    }
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, status, total_cents) VALUES (@oid, 'link', 'open', @total)",
                        new { oid = orderId, total = amountCents ?? 0 }, transaction: tx);
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents)
SELECT @oid, p.manifest_id, CAST(ROUND(COALESCE(p.sale_price, p.list_price, p.total_wholesale, 0) * 100, 0) AS BIGINT)
FROM dbo.v_pallets p WHERE p.manifest_id IN @ids",
                        new { oid = orderId, ids }, transaction: tx);
                    _log.LogWarning("Fulfill: recovered order {OrderId} from {N} line uids", orderId, ids.Count);
                    order = await conn.QueryFirstOrDefaultAsync(
                        "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                }

                string kind = (string)order.kind;
                var boxes = (await conn.QueryAsync(@"
SELECT b.manifest_id, b.amount_cents, b.outcome, m.pallet_number, m.publish_state, m.archived_at, m.is_ghost, m.invoice_id
FROM dbo.checkout_order_boxes b JOIN dbo.manifests m ON m.id = b.manifest_id
WHERE b.square_order_id = @oid ORDER BY b.manifest_id",
                    new { oid = orderId }, transaction: tx)).ToList();

                int sold = 0, unavailable = 0;
                long refundDue = 0;
                var soldIds = new List<Guid>();
                var pallets = new List<int>();
                foreach (var b in boxes)
                {
                    Guid mid = (Guid)b.manifest_id;
                    pallets.Add((int)b.pallet_number);
                    string? outcome = (string?)b.outcome;
                    if (outcome == "sold") { sold++; continue; }                     // re-entrant: already ours
                    if (outcome == "unavailable") { unavailable++; refundDue += (long)b.amount_cents; continue; }

                    bool ok = Availability.For(kind, (string?)b.publish_state, (DateTime?)b.archived_at,
                        b.is_ghost == true, (string?)b.invoice_id);
                    if (ok)
                    {
                        await conn.ExecuteAsync("EXEC dbo.sp_SetPublishState @manifest_id = @mid, @publish_state = 'sold'",
                            new { mid }, transaction: tx);
                        await PalletsFunction.InsertHistoryAsync(conn, mid, "publish_state", (string?)b.publish_state, "sold", source, tx);
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'sold', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        sold++;
                        soldIds.Add(mid);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.checkout_order_boxes SET outcome = 'unavailable', fulfilled_at = SYSUTCDATETIME() WHERE square_order_id = @oid AND manifest_id = @mid",
                            new { oid = orderId, mid }, transaction: tx);
                        unavailable++;
                        refundDue += (long)b.amount_cents;
                        _log.LogWarning("Fulfill: BOX #{Num} on order {OrderId} no longer available (state {State}) — refund due",
                            (object?)b.pallet_number, orderId, (string?)b.publish_state);
                    }
                }

                string status = refundDue == 0 ? "COMPLETED" : (sold == 0 ? "REFUND_FLAGGED" : "PARTIAL_REFUND_FLAGGED");
                Guid? single = boxes.Count == 1 ? (Guid)boxes[0].manifest_id : null;
                await conn.ExecuteAsync(@"
UPDATE dbo.payments SET manifest_id = @mid, refund_due_cents = @due, needs_refund = @flag, status = @status
WHERE square_payment_id = @pid",
                    new { mid = single, due = refundDue > 0 ? refundDue : (long?)null, flag = refundDue > 0, status, pid = paymentId },
                    transaction: tx);
                await conn.ExecuteAsync(
                    "UPDATE dbo.checkout_orders SET status = 'paid', closed_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                    new { oid = orderId }, transaction: tx);

                long total = (long)order.total_cents;
                if (amountCents.HasValue && total > 0 && amountCents.Value != total)
                    _log.LogWarning("Fulfill: payment {PaymentId} amount {Amt} != order total {Total}", paymentId, amountCents, total);

                if (soldIds.Count > 0)
                    canceled = await CancelOpenLinksForBoxesAsync(conn, tx, soldIds, exceptOrderId: orderId);

                tx.Commit();
                result = new FulfillResult("fulfilled", sold, unavailable, refundDue, pallets);
                _log.LogInformation("Fulfill: order {OrderId} payment {PaymentId} via {Source}: {Sold} sold, {Unav} unavailable, refund due {Due}",
                    orderId, paymentId, source, sold, unavailable, refundDue);
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }

        await RetireLinksAsync(conn, canceled, ct);
        return result;
    }

    /// <summary>
    /// DB-first fence: mark every OTHER open cart link that contains any of
    /// these boxes as canceled. Runs inside the caller's transaction. The
    /// Square deletes happen later via RetireLinksAsync.
    /// </summary>
    public async Task<List<CanceledLink>> CancelOpenLinksForBoxesAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyCollection<Guid> manifestIds, string? exceptOrderId)
    {
        if (manifestIds.Count == 0) return new List<CanceledLink>();
        var rows = await conn.QueryAsync(@"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
OUTPUT inserted.square_order_id, inserted.square_link_id
FROM dbo.checkout_orders o
WHERE o.status = 'open' AND o.kind = 'link'
  AND (@except IS NULL OR o.square_order_id <> @except)
  AND EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
              WHERE b.square_order_id = o.square_order_id AND b.manifest_id IN @ids)",
            new { except = exceptOrderId, ids = manifestIds.ToArray() }, transaction: tx);
        return rows.Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();
    }

    /// <summary>
    /// Best-effort Square deletes for DB-canceled links. link_deleted_at is
    /// stamped ONLY when Square confirms; anything else is left for Reconcile.
    /// Never throws — the sale is already committed.
    /// </summary>
    public async Task RetireLinksAsync(SqlConnection conn, IEnumerable<CanceledLink> links, CancellationToken ct)
    {
        foreach (var l in links)
        {
            try
            {
                bool confirmed;
                if (l.LinkId == null) confirmed = true;                 // nothing at Square to delete
                else if (!_square.Configured) confirmed = false;       // leave for Reconcile
                else confirmed = await _square.DeletePaymentLinkAsync(l.LinkId, ct);
                if (confirmed)
                    await conn.ExecuteAsync(
                        "UPDATE dbo.checkout_orders SET link_deleted_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                        new { oid = l.OrderId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "RetireLinks: could not delete link {LinkId} (order {OrderId}) — Reconcile will retry", l.LinkId, l.OrderId);
            }
        }
    }
}
```

- [ ] **Step 6: Register in DI**

In `api/Program.cs`, after `builder.Services.AddSingleton<SquareService>();` add:
```csharp
builder.Services.AddSingleton<CheckoutFulfillment>();
```

- [ ] **Step 7: Build and commit**

Run: `dotnet build api/api.csproj` → `Build succeeded.`
```powershell
git add api/Services/Availability.cs api/Services/CheckoutFulfillment.cs api.Tests/AvailabilityTests.cs api/Program.cs
git commit -m "feat(checkout): Availability rule + transactional CheckoutFulfillment service"
```

---

### Task 4: `POST /api/public/checkout` (cart) + single-box wrapper

**Files:**
- Modify: `api/Functions/SquareFunction.cs:26-93` (constructor + `CreateCheckout`)
- Modify: `api/Services/SquareService.cs` (add `PublicBaseUrl`)
- Delete: the TEMP shim added in Task 2 Step 6 (`PaymentLink` record + `CreatePaymentLinkAsync`) — in Task 6 if a green build is wanted in between

**Interfaces:**
- Consumes: `SquareService.CreateCartPaymentLinkAsync`, `SquarePayloads.BoxLineName/PaymentNote`, `Availability.ForLink` (Tasks 2–3).
- Produces: `POST /api/public/checkout` body `{ "ids": ["<guid>", …] }` → `200 { url }` | `400 { error }` | `409 { error, unavailable: [guid…] }` | `503 { error }`. `POST /api/public/checkout/{id}` → same, cart of one. `GET /api/public/checkout-status` → `{ enabled, cartMax }`.

- [ ] **Step 1: Add `PublicBaseUrl` to SquareService**

In the constructor: `PublicBaseUrl = (cfg["SQUARE_PUBLIC_BASE_URL"] ?? "https://northstateliquidators.com").TrimEnd('/');` and the property `public string PublicBaseUrl { get; }`. Update the class summary comment with `SQUARE_PUBLIC_BASE_URL  site origin for redirect URLs (preview slots differ)` and `SQUARE_SUPPORT_EMAIL`.

- [ ] **Step 2: Rewrite `CreateCheckout`**

Replace the constructor fields/ctor, `Status`, and the whole `CreateCheckout` function (lines 26-93) with:

```csharp
    private readonly SqlService _sql;
    private readonly SquareService _square;
    private readonly CheckoutFulfillment _fulfill;
    private readonly ILogger<SquareFunction> _log;

    public SquareFunction(SqlService sql, SquareService square, CheckoutFulfillment fulfill, ILogger<SquareFunction> log)
    {
        _sql = sql;
        _square = square;
        _fulfill = fulfill;
        _log = log;
    }

    public const int CartMax = 20;
    public sealed record CheckoutRequest(Guid[]? ids);

    [Function("CheckoutStatus")]
    public IActionResult Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/checkout-status")] HttpRequest req)
        => new OkObjectResult(new { enabled = _square.CheckoutEnabled && _square.Configured, cartMax = CartMax });

    /// <summary>Legacy single-box route (tabs loaded before the cart deploy): a cart of one.</summary>
    [Function("CreateCheckoutOne")]
    public Task<IActionResult> CreateCheckoutOne(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout/{id}")] HttpRequest req,
        Guid id, CancellationToken ct)
        => CreateCartCheckoutCore(new[] { id }, ct);

    [Function("CreateCheckout")]
    public async Task<IActionResult> CreateCheckout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout")] HttpRequest req,
        CancellationToken ct)
    {
        CheckoutRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<CheckoutRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException) { return new BadRequestObjectResult(new { error = "Invalid JSON" }); }
        return await CreateCartCheckoutCore(body?.ids ?? Array.Empty<Guid>(), ct);
    }

    private async Task<IActionResult> CreateCartCheckoutCore(Guid[] rawIds, CancellationToken ct)
    {
        if (!_square.CheckoutEnabled || !_square.Configured)
            return new ObjectResult(new { error = "Online checkout is not available right now." }) { StatusCode = 503 };

        var ids = rawIds.Where(g => g != Guid.Empty).Distinct().OrderBy(g => g).ToArray();
        if (ids.Length == 0) return new BadRequestObjectResult(new { error = "Add at least one box." });
        if (ids.Length > CartMax) return new BadRequestObjectResult(new { error = $"A cart holds at most {CartMax} boxes." });

        await using var conn = await _sql.OpenAsync(ct);
        var rows = (await conn.QueryAsync(@"
SELECT p.manifest_id, p.pallet_number, p.display_name, p.publish_state, p.is_ghost, p.archived_at, m.invoice_id,
       COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p JOIN dbo.manifests m ON m.id = p.manifest_id
WHERE p.manifest_id IN @ids", new { ids })).ToDictionary(r => (Guid)r.manifest_id);

        var unavailable = new List<Guid>();
        var lines = new List<CartLine>();
        var numbers = new List<int>();
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var b)) { unavailable.Add(id); continue; }
            decimal? ask = (decimal?)b.ask_price;
            bool ok = Availability.ForLink((string?)b.publish_state, (DateTime?)b.archived_at, b.is_ghost == true, (string?)b.invoice_id)
                      && ask is > 0;
            if (!ok) { unavailable.Add(id); continue; }
            lines.Add(new CartLine(id, SquarePayloads.BoxLineName((int)b.pallet_number, (string?)b.display_name),
                (long)Math.Round(ask!.Value * 100m)));
            numbers.Add((int)b.pallet_number);
        }
        if (unavailable.Count > 0)
            return new ConflictObjectResult(new
            {
                error = unavailable.Count == ids.Length
                    ? "These boxes are no longer available."
                    : "Some boxes in your cart are no longer available.",
                unavailable
            });

        // Reuse an open link with the IDENTICAL (box, amount) set — open +
        // identical means current, because price/state changes cancel links.
        var wanted = lines.ToDictionary(l => l.ManifestId, l => l.AmountCents);
        var candidates = (await conn.QueryAsync(@"
SELECT o.square_order_id, o.url, b.manifest_id, b.amount_cents
FROM dbo.checkout_orders o
JOIN dbo.checkout_order_boxes b ON b.square_order_id = o.square_order_id
WHERE o.status = 'open' AND o.kind = 'link' AND o.url IS NOT NULL
  AND o.square_order_id IN (SELECT square_order_id FROM dbo.checkout_order_boxes WHERE manifest_id = @first)",
            new { first = ids[0] })).GroupBy(r => (string)r.square_order_id);
        foreach (var g in candidates)
        {
            var set = g.ToDictionary(r => (Guid)r.manifest_id, r => (long)r.amount_cents);
            if (set.Count == wanted.Count && wanted.All(kv => set.TryGetValue(kv.Key, out var amt) && amt == kv.Value))
                return new OkObjectResult(new { url = (string)g.First().url });
        }

        var redirect = $"{_square.PublicBaseUrl}/thanks.html?boxes={string.Join(",", numbers)}";
        var link = await _square.CreateCartPaymentLinkAsync(lines, redirect,
            idempotencyKey: $"nsl-cart-{Guid.NewGuid():N}",
            referenceId: "NSL " + string.Join(" ", numbers.Select(n => "#" + n)),
            paymentNote: SquarePayloads.PaymentNote(numbers), ct);

        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status, total_cents)
VALUES (@oid, 'link', @lid, @url, 'open', @total)",
                new { oid = link.OrderId, lid = link.Id, url = link.Url, total = link.TotalCents }, transaction: tx);
            foreach (var l in lines)
                await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents) VALUES (@oid, @mid, @amt)",
                    new { oid = link.OrderId, mid = l.ManifestId, amt = l.AmountCents }, transaction: tx);
            tx.Commit();
        }

        _log.LogInformation("CreateCheckout: {N} box(es) {Boxes} -> link {LinkId} order {OrderId} total {Total}",
            lines.Count, string.Join(",", numbers), link.Id, link.OrderId, link.TotalCents);
        return new OkObjectResult(new { url = link.Url });
    }
```

- [ ] **Step 3: Build**

Run `dotnet build api/api.csproj`. Expected: `Build succeeded.` (the Task 2 shim still satisfies `InvoiceBox`/`Reconcile`/`SoldToInventory` until Tasks 6–7 rewrite them; `DeletePaymentLinkAsync` now returns `Task<bool>`, which the old `await` statements accept).

- [ ] **Step 4: Commit**

```powershell
git add api/Functions/SquareFunction.cs api/Services/SquareService.cs
git commit -m "feat(checkout): POST /api/public/checkout for N boxes; single-box route becomes a cart of one"
```

---

### Task 5: Webhook uses `FulfillOrderAsync`; refunds accumulate and dedupe

**Files:**
- Modify: `db/cart-checkout.sql` (append the `payment_refunds` table; re-apply)
- Modify: `api/Services/CheckoutFulfillment.cs` (`orderId` becomes nullable; add `RecordRefundAsync`)
- Modify: `api/Functions/SquareFunction.cs` — the `Webhook` function (everything after signature verification)

**Interfaces:**
- Consumes: `SquareEvents.*` (Phase 0), `CheckoutFulfillment.FulfillOrderAsync` (Task 3).
- Produces:
  - table `dbo.payment_refunds (square_refund_id VARCHAR(64) PK, square_payment_id VARCHAR(64), amount_cents BIGINT, created_at)`
  - `static Task<bool> CheckoutFulfillment.RecordRefundAsync(SqlConnection conn, string refundId, string paymentId, long amountCents)` — true if newly recorded (and `payments.refunded_cents`/`status`/`needs_refund` updated), false on replay.
  - Webhook 200 bodies: `{ ignored: "floor"|"malformed"|<type>|<status> }`, `{ duplicate: true }`, `{ unmatched: true }`, `{ fulfilled: true, sold, unavailable, refundDue, boxes }`, `{ refund: <status>, recorded }`.

- [ ] **Step 1: Append the refunds table to the migration and re-apply**

Append to `db/cart-checkout.sql` before the final `PRINT`:
```sql
-- One row per Square refund we have applied to payments.refunded_cents, so a
-- replayed refund.updated (or the admin endpoint + the webhook both seeing the
-- same refund) can never double-count.
IF OBJECT_ID('dbo.payment_refunds', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.payment_refunds (
        square_refund_id  VARCHAR(64) NOT NULL PRIMARY KEY,
        square_payment_id VARCHAR(64) NOT NULL,
        amount_cents      BIGINT      NOT NULL,
        created_at        DATETIME2   NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_payment_refunds_payment ON dbo.payment_refunds (square_payment_id);
END;
GO
GRANT SELECT, INSERT ON dbo.payment_refunds TO nsl_api;
GO
```
Re-run the script on prod (same Invoke-Sqlcmd command as Task 1 Step 3). Expected: no errors, `payment_refunds` exists.

- [ ] **Step 2: Make `orderId` nullable in `FulfillOrderAsync` and add `RecordRefundAsync`**

In `CheckoutFulfillment.cs` change the signature to `string? orderId` and the fallback line to:
```csharp
                    var ids = orderId != null && _square.Configured ? await _square.OrderLineUidsAsync(orderId, ct) : new List<Guid>();
```
(the `checkout_orders` lookup with a null `@oid` simply returns null; the `payments` INSERT already accepts a null order id). Also guard the two INSERTs in the fallback with `orderId!` since ids are only non-empty when orderId is non-null.

Add to the class:
```csharp
    /// <summary>
    /// Apply one Square refund to our audit row exactly once. Cumulative:
    /// REFUNDED when everything is back, PARTIAL_REFUNDED otherwise; the
    /// attention flag clears once the amount owed (refund_due_cents, or the
    /// whole payment) has been returned.
    /// </summary>
    public static async Task<bool> RecordRefundAsync(SqlConnection conn, string refundId, string paymentId, long amountCents)
    {
        var inserted = await conn.ExecuteAsync(@"
INSERT INTO dbo.payment_refunds (square_refund_id, square_payment_id, amount_cents)
SELECT @rid, @pid, @amt
WHERE NOT EXISTS (SELECT 1 FROM dbo.payment_refunds WHERE square_refund_id = @rid)",
            new { rid = refundId, pid = paymentId, amt = amountCents });
        if (inserted == 0) return false;
        await conn.ExecuteAsync(@"
UPDATE dbo.payments SET
    refunded_cents = refunded_cents + @amt,
    status = CASE WHEN refunded_cents + @amt >= COALESCE(amount_cents, 0) THEN 'REFUNDED' ELSE 'PARTIAL_REFUNDED' END,
    needs_refund = CASE WHEN refunded_cents + @amt >= COALESCE(refund_due_cents, amount_cents, 0) THEN 0 ELSE needs_refund END
WHERE square_payment_id = @pid", new { pid = paymentId, amt = amountCents });
        return true;
    }
```

- [ ] **Step 3: Rewrite the webhook body after `VerifyWebhookSignature`**

Replace everything from `JsonDocument doc;` (Phase 0 code) to the end of the `Webhook` function with:

```csharp
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException)
        {
            _log.LogWarning("SquareWebhook: body is not JSON ({Len} bytes)", raw.Length);
            return new OkObjectResult(new { ignored = "malformed" });
        }
        using var docScope = doc;
        var root = doc.RootElement;
        var type = SquareEvents.EventType(root);

        if (type is "refund.updated" or "refund.created")
        {
            if (!SquareEvents.TryParseRefund(root, out var refund))
                return new OkObjectResult(new { ignored = "malformed" });
            await using var rconn = await _sql.OpenAsync(ct);
            bool recorded = false;
            if (refund.PaymentId != null)
            {
                if (refund.Status == "COMPLETED" && refund.AmountCents is > 0)
                {
                    recorded = await CheckoutFulfillment.RecordRefundAsync(rconn, refund.RefundId, refund.PaymentId, refund.AmountCents.Value);
                    _log.LogInformation("SquareWebhook: refund {RefundId} COMPLETED for payment {PaymentId} ({Amt}c) recorded={Rec}",
                        refund.RefundId, refund.PaymentId, refund.AmountCents, recorded);
                }
                else if (refund.Status is "FAILED" or "REJECTED")
                {
                    await rconn.ExecuteAsync(
                        "UPDATE dbo.payments SET needs_refund = 1, status = 'REFUND_' + @rs WHERE square_payment_id = @pid AND status NOT IN ('REFUNDED')",
                        new { pid = refund.PaymentId, rs = refund.Status });
                    _log.LogError("SquareWebhook: refund {RefundId} {Status} for payment {PaymentId} — re-flagged", refund.RefundId, refund.Status, refund.PaymentId);
                }
            }
            return new OkObjectResult(new { refund = refund.Status, recorded });
        }

        if (type != "payment.updated" && type != "payment.created")
            return new OkObjectResult(new { ignored = type });

        if (!SquareEvents.TryParsePayment(root, out var pay))
            return new OkObjectResult(new { ignored = "malformed" });
        if (pay.Status != "COMPLETED")
            return new OkObjectResult(new { ignored = pay.Status });

        await using var conn = await _sql.OpenAsync(ct);

        // Shared merchant account: only fulfil orders we know, or payments from
        // our own products (links / invoices). A counter sale is neither.
        bool known = pay.OrderId != null && await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.checkout_orders WHERE square_order_id = @oid", new { oid = pay.OrderId }) > 0;
        if (!known && !SquareEvents.IsOurProduct(pay.Product))
        {
            _log.LogInformation("SquareWebhook: ignoring {Product} payment {PaymentId} (floor/other)", pay.Product ?? "?", pay.PaymentId);
            return new OkObjectResult(new { ignored = "floor" });
        }

        var r = await _fulfill.FulfillOrderAsync(conn, pay.OrderId, pay.PaymentId, pay.AmountCents, raw, "square", ct);
        return r.Outcome switch
        {
            "duplicate" => new OkObjectResult(new { duplicate = true }),
            "unmatched" => new OkObjectResult(new { unmatched = true }),
            _ => new OkObjectResult(new { fulfilled = true, sold = r.Sold, unavailable = r.Unavailable, refundDue = r.RefundDueCents, boxes = r.PalletNumbers })
        };
    }
```

- [ ] **Step 4: Build, run tests, commit**

Run: `dotnet build api/api.csproj` → `Build succeeded.`; `dotnet test api.Tests/api.Tests.csproj` → all pass.
```powershell
git add db/cart-checkout.sql api/Services/CheckoutFulfillment.cs api/Functions/SquareFunction.cs
git commit -m "feat(webhook): fulfil multi-box orders transactionally; cumulative, deduped refunds"
```

---

### Task 6: Cancel-on-change — UpdatePallet, InvoiceBox, CancelBoxInvoice, SoldToInventory, DeletePallet

**Files:**
- Modify: `api/Functions/PalletsFunction.cs` — constructor (`:56-64`), `UpdatePallet` (after `WriteHistoryAsync`, `:331`), `DeletePallet` (`:451-481`), `SoldToInventory` (`:499-528`)
- Modify: `api/Functions/SquareFunction.cs` — `InvoiceBox` (`:206-274`), `CancelBoxInvoice` (`:280-297`)
- Modify: `api/Services/SquareService.cs` — delete the Task 2 TEMP shim

**Interfaces:**
- Consumes: `CheckoutFulfillment.CancelOpenLinksForBoxesAsync`, `RetireLinksAsync` (Task 3).
- Produces: no new endpoints. `DeletePallet` gains `409 { error }` when the box has a completed web sale.

- [ ] **Step 1: Inject `CheckoutFulfillment` into `PalletsFunction`**

Change the constructor to:
```csharp
    private readonly CheckoutFulfillment _fulfill;

    public PalletsFunction(SqlService sql, BlobService blob, SquareService square, CheckoutFulfillment fulfill,
        IConfiguration config, ILogger<PalletsFunction> log)
    {
        _sql = sql;
        _blob = blob;
        _square = square;
        _fulfill = fulfill;
```
(keep the remaining assignments as they are).

- [ ] **Step 2: `UpdatePallet` — retire open cart links on price or availability change**

Immediately after `await WriteHistoryAsync(conn, id, before, after, ClientPrincipal.UserDetails(req));` add:
```csharp
        // Cart links (spec §4): a price change, leaving 'live', or archiving
        // retires every open cart link that holds this box — DB first, Square
        // best-effort. A shopper holding an old link sees Square refuse it;
        // the drawer re-validates on open.
        bool priceChanged = before?.list_price != after?.list_price || before?.sale_price != after?.sale_price;
        bool leftLive = before?.publish_state == "live" && after?.publish_state != "live";
        bool archivedNow = body?.archived == true;
        if (priceChanged || leftLive || archivedNow)
        {
            List<CanceledLink> canceled;
            using (var tx = conn.BeginTransaction())
            {
                canceled = await _fulfill.CancelOpenLinksForBoxesAsync(conn, tx, new[] { id }, null);
                tx.Commit();
            }
            if (canceled.Count > 0)
            {
                _log.LogInformation("UpdatePallet {Id}: canceled {N} open cart link(s) (price/state change)", id, canceled.Count);
                await _fulfill.RetireLinksAsync(conn, canceled, ct);
            }
        }
```
`before`/`after` are `AuditSnapshot` records (`publish_state`, `list_price`, `sale_price`, …) already loaded around the update.

- [ ] **Step 3: `SoldToInventory` — DB-first cancel replaces the fail-closed delete**

Replace from `var orig = await conn.QueryFirstOrDefaultAsync(` through the closing of the `if (linkId != null && _square.Configured) { ... }` block with:
```csharp
        var orig = await conn.QueryFirstOrDefaultAsync(
            "SELECT publish_state, invoice_id FROM dbo.manifests WHERE id = @id", new { id });
        if (orig == null) return new NotFoundResult();
        if (orig.invoice_id != null)
            return new ConflictObjectResult(new { error = "This box has an outstanding Square invoice — cancel the invoice first, or wait for it to be paid." });
        string? prevState = (string?)orig.publish_state;

        // Retire every open cart link holding this box BEFORE it reads SOLD.
        // The DB cancel is the fence; Square deletes are best-effort after.
        List<CanceledLink> canceled;
        using (var tx = conn.BeginTransaction())
        {
            canceled = await _fulfill.CancelOpenLinksForBoxesAsync(conn, tx, new[] { id }, null);
            tx.Commit();
        }
```
and after the `sp_SoldToInventory` call succeeds (right after `row` is obtained and before the history rows), add:
```csharp
        await _fulfill.RetireLinksAsync(conn, canceled, ct);
```
Remove the now-unused `linkId` variable and the old `UPDATE dbo.manifests SET checkout_link_id = NULL …` statement.

- [ ] **Step 4: `DeletePallet` — refuse sold-on-web boxes, clean up cart rows**

Inside the `try`, before the `manifest_history` delete:
```csharp
            var webSold = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.checkout_order_boxes WHERE manifest_id = @id AND outcome = 'sold'",
                new { id }, transaction: tx);
            if (webSold > 0)
            {
                tx.Rollback();
                return new ConflictObjectResult(new { error = "This box was sold through the website — archive it instead of deleting so the sale record stays intact." });
            }
            var canceled = await _fulfill.CancelOpenLinksForBoxesAsync(conn, tx, new[] { id }, null);
            await conn.ExecuteAsync("DELETE FROM dbo.checkout_order_boxes WHERE manifest_id = @id", new { id }, transaction: tx);
```
and after `tx.Commit();`:
```csharp
            await _fulfill.RetireLinksAsync(conn, canceled, ct);
```

- [ ] **Step 5: `InvoiceBox` — cancel links DB-first, record the invoice order**

Replace the box query's column list `m.checkout_link_id, m.invoice_id,` with `m.invoice_id,`. Replace the block
```csharp
        // Retire the public Buy link (its order would be a second sale channel).
        if (box.checkout_link_id != null)
            await _square.DeletePaymentLinkAsync((string)box.checkout_link_id, ct);
```
with:
```csharp
        // Retire every open cart link holding this box (DB-first fence), then
        // create the invoice. If Square fails below, the box is simply
        // link-less until a shopper re-adds it — never a live box with a dead link.
        List<CanceledLink> canceled;
        using (var tx0 = conn.BeginTransaction())
        {
            canceled = await _fulfill.CancelOpenLinksForBoxesAsync(conn, tx0, new[] { id }, null);
            tx0.Commit();
        }
        await _fulfill.RetireLinksAsync(conn, canceled, ct);
```
Replace the `UPDATE dbo.manifests SET invoice_id = @iid, invoice_url = @iurl, checkout_link_id = NULL, …` statement with:
```csharp
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
UPDATE dbo.manifests SET invoice_id = @iid, invoice_url = @iurl WHERE id = @id",
                new { id, iid = inv.InvoiceId, iurl = inv.PublicUrl }, transaction: tx);
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, url, status, total_cents)
VALUES (@oid, 'invoice', @url, 'open', @total)",
                new { oid = inv.OrderId, url = inv.PublicUrl, total = (long)Math.Round(price.Value * 100m) }, transaction: tx);
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents) VALUES (@oid, @mid, @amt)",
                new { oid = inv.OrderId, mid = id, amt = (long)Math.Round(price.Value * 100m) }, transaction: tx);
            tx.Commit();
        }
```
The "park in draft" block that follows stays unchanged.

- [ ] **Step 6: `CancelBoxInvoice` — cancel the invoice order row**

Replace the `UPDATE dbo.manifests SET invoice_id = NULL, invoice_url = NULL, checkout_order_id = NULL, checkout_created_at = NULL WHERE id = @id` statement with:
```csharp
        await conn.ExecuteAsync(@"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
FROM dbo.checkout_orders o
WHERE o.kind = 'invoice' AND o.status = 'open'
  AND EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b WHERE b.square_order_id = o.square_order_id AND b.manifest_id = @id);
UPDATE dbo.manifests SET invoice_id = NULL, invoice_url = NULL WHERE id = @id", new { id });
```

- [ ] **Step 7: Delete the TEMP shim in `SquareService`, build, commit**

Remove the `// TEMP shim` block. Run `dotnet build api/api.csproj`. Expected: the ONLY remaining errors are in `Reconcile` (references `checkout_*` columns and `DeletePaymentLinkAsync` as a statement — that still compiles; the column references are strings and compile fine). If the build is green, good; if `PaymentLink` is still referenced anywhere, fix that call site.
```powershell
git add api/Functions/PalletsFunction.cs api/Functions/SquareFunction.cs api/Services/SquareService.cs
git commit -m "feat(checkout): cancel open cart links on price/state change, invoice, fake-sale and delete"
```

---

### Task 7: Reconcile — set-based cancel, confirmed deletes, newest-first order checks

**Files:**
- Modify: `api/Functions/SquareFunction.cs` — replace the whole `Reconcile` function (the last function in the file)

**Interfaces:**
- Consumes: `CheckoutFulfillment.FulfillOrderAsync`, `RetireLinksAsync`, `SquareService.RetrieveOrderAsync`, `SquareService.IsOrderPaid`.
- Produces: `POST /api/square-reconcile` → `{ canceledByRule, linksDeleted, healed, canceledAtSquare, stillOpen, needsRefund }`. Also accepts header `x-functions-key`-free calls only via SWA auth (unchanged) — the cron in Task 13 uses a GitHub secret with a staff session cookie alternative described there.

- [ ] **Step 1: Replace `Reconcile`**

```csharp
    public const int ReconcileMaxSquareCalls = 40;

    /// <summary>
    /// Reconciliation sweep (spec §4). SWA-managed Functions have no timers,
    /// so this runs from the staff button and the GitHub Actions cron.
    /// 1. DB-only pass: cancel open cart links whose boxes are no longer
    ///    available, whose amounts drifted from the current ask, or that are
    ///    older than 7 days (Square links never expire — we must).
    /// 2. Delete at Square every canceled link not yet confirmed deleted.
    /// 3. RetrieveOrder the remaining open orders, newest first (capped):
    ///    paid → fulfil (heals a missed webhook); CANCELED at Square → mark.
    /// Invoice orders are never canceled here (explicit staff action only).
    /// </summary>
    [Function("SquareReconcile")]
    public async Task<IActionResult> Reconcile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-reconcile")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };

        await using var conn = await _sql.OpenAsync(ct);

        // 1. Set-based cancel (no Square calls).
        var ruleCanceled = (await conn.QueryAsync(@"
UPDATE o SET status = 'canceled', closed_at = SYSUTCDATETIME()
OUTPUT inserted.square_order_id, inserted.square_link_id
FROM dbo.checkout_orders o
WHERE o.status = 'open' AND o.kind = 'link'
  AND (
        o.created_at < DATEADD(DAY, -7, SYSUTCDATETIME())
     OR EXISTS (SELECT 1
                FROM dbo.checkout_order_boxes b
                JOIN dbo.manifests m ON m.id = b.manifest_id
                LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
                WHERE b.square_order_id = o.square_order_id
                  AND (m.publish_state <> 'live' OR m.archived_at IS NOT NULL OR m.is_ghost = 1 OR m.invoice_id IS NOT NULL
                       OR b.amount_cents <> CAST(ROUND(COALESCE(v.sale_price, v.list_price, v.total_wholesale, 0) * 100, 0) AS BIGINT)))
     OR EXISTS (SELECT 1 FROM dbo.checkout_order_boxes b
                WHERE b.square_order_id = o.square_order_id
                  AND NOT EXISTS (SELECT 1 FROM dbo.manifests m WHERE m.id = b.manifest_id))
  )")).Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();

        // 2. Square deletes for anything canceled but not confirmed (includes step 1's rows).
        var pending = (await conn.QueryAsync(@"
SELECT TOP (@cap) square_order_id, square_link_id FROM dbo.checkout_orders
WHERE status = 'canceled' AND kind = 'link' AND link_deleted_at IS NULL
ORDER BY closed_at ASC", new { cap = ReconcileMaxSquareCalls }))
            .Select(r => new CanceledLink((string)r.square_order_id, (string?)r.square_link_id)).ToList();
        await _fulfill.RetireLinksAsync(conn, pending, ct);
        int linksDeleted = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.checkout_orders WHERE square_order_id IN @ids AND link_deleted_at IS NOT NULL",
            new { ids = pending.Select(p => p.OrderId).ToArray() });

        // 3. Check the remaining open orders at Square, newest first.
        var open = (await conn.QueryAsync(@"
SELECT TOP (@cap) square_order_id, kind FROM dbo.checkout_orders
WHERE status = 'open' ORDER BY created_at DESC", new { cap = ReconcileMaxSquareCalls })).ToList();

        int healed = 0, canceledAtSquare = 0, stillOpen = 0;
        foreach (var o in open)
        {
            string oid = (string)o.square_order_id;
            using var order = await _square.RetrieveOrderAsync(oid, ct);
            if (order == null) { stillOpen++; continue; }
            var el = order.RootElement.GetProperty("order");
            var state = el.TryGetProperty("state", out var st) ? st.GetString() : null;

            if (state != "CANCELED" && SquareService.IsOrderPaid(el))
            {
                var tenderPayment = el.TryGetProperty("tenders", out var tenders) && tenders.GetArrayLength() > 0 &&
                                    tenders[0].TryGetProperty("payment_id", out var tp) ? tp.GetString() : null;
                long? amt = el.TryGetProperty("total_money", out var tm) && tm.TryGetProperty("amount", out var ta) ? ta.GetInt64() : null;
                var r = await _fulfill.FulfillOrderAsync(conn, oid, tenderPayment ?? $"reconciled-{oid}", amt, null, "reconcile", ct);
                if (r.Outcome == "fulfilled")
                {
                    healed++;
                    _log.LogWarning("SquareReconcile: healed missed webhook — order {OrderId}: {Sold} sold, {Unav} unavailable", oid, r.Sold, r.Unavailable);
                }
                else stillOpen++;   // duplicate payment row but order still open: leave for a human/log
            }
            else if (state == "CANCELED" && (string)o.kind == "link")
            {
                await conn.ExecuteAsync(
                    "UPDATE dbo.checkout_orders SET status = 'canceled', closed_at = SYSUTCDATETIME(), link_deleted_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                    new { oid });
                canceledAtSquare++;
            }
            else stillOpen++;
        }

        var flagged = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.payments WHERE needs_refund = 1");
        return new OkObjectResult(new { canceledByRule = ruleCanceled.Count, linksDeleted, healed, canceledAtSquare, stillOpen, needsRefund = flagged });
    }
```

- [ ] **Step 2: Update the staff toast**

In `staff/js/sales.js` `reconcile()`, replace the toast line with:
```javascript
    toast(`Reconcile: ${r.healed} healed, ${r.canceledByRule} link${r.canceledByRule === 1 ? '' : 's'} canceled, ${r.linksDeleted} deleted at Square, ${r.stillOpen} open, ${r.needsRefund} flagged`, 'ok', 5000);
```

- [ ] **Step 3: Build, commit**

Run: `dotnet build api/api.csproj` → `Build succeeded.` (this was the last consumer of the `checkout_*` columns in C#; `grep -n "checkout_link_id\|checkout_order_id\|checkout_url" api/Functions/*.cs api/Services/*.cs` must now return nothing).
```powershell
git add api/Functions/SquareFunction.cs staff/js/sales.js
git commit -m "feat(reconcile): set-based cancel + confirmed Square deletes + newest-first paid checks"
```

---

### Task 8: Refund endpoint (partial), payments list, sales summary, staff sales page

**Files:**
- Modify: `api/Functions/SquareFunction.cs` — `ListPayments`, `Refund`, `SalesSummary` (webRows query, loop, adminRows query, `SaleRow`)
- Modify: `staff/js/api.js:86`
- Modify: `staff/js/sales.js` — `loadSummary` sales table row, `loadPayments` both tables, `refund()`
- Modify: `staff/sales.html:67` (copy)

**Interfaces:**
- Consumes: `CheckoutFulfillment.RecordRefundAsync`, `SquareService.RefundPaymentAsync` (Tasks 2, 5).
- Produces:
  - `POST /api/square-refund` body `{ paymentId, reason?, amountCents? }` → `{ paymentId, refundId, refundStatus, amountCents }`; `409` when nothing left to refund.
  - `GET /api/square-payments` rows gain `boxes` (string like `#12, #14` or null), `refund_due_cents`, `refunded_cents`.
  - `GET /api/sales-summary` `sales[]` rows gain `boxes` (string) and `box_count` (int); `margin_cents` now = amount − refunded − cost.

- [ ] **Step 1: `ListPayments` query**

Replace the SQL with:
```sql
SELECT TOP 100 p.square_payment_id, p.square_order_id, p.manifest_id,
       p.amount_cents, p.refunded_cents, p.refund_due_cents, p.status, p.needs_refund, p.created_at,
       m.pallet_number, m.display_name,
       (SELECT STRING_AGG('#' + CAST(m2.pallet_number AS VARCHAR(10)), ', ') WITHIN GROUP (ORDER BY m2.pallet_number)
        FROM dbo.checkout_order_boxes b JOIN dbo.manifests m2 ON m2.id = b.manifest_id
        WHERE b.square_order_id = p.square_order_id AND b.outcome = 'sold') AS boxes
FROM dbo.payments p
LEFT JOIN dbo.manifests m ON m.id = p.manifest_id
ORDER BY p.needs_refund DESC, p.created_at DESC
```

- [ ] **Step 2: `Refund` — partial amounts, cumulative bookkeeping**

Replace the `RefundRequest` record and the `Refund` function with:
```csharp
    public sealed record RefundRequest(string? paymentId, string? reason, long? amountCents);

    /// <summary>
    /// POST /api/square-refund — refund from the admin. Default amount is what
    /// is owed (refund_due_cents for a partial-unavailable cart, else the
    /// remainder of the payment); an explicit amountCents ≤ remainder is
    /// allowed for staff-initiated partials. Bookkeeping goes through
    /// RecordRefundAsync so the refund.updated webhook cannot double count.
    /// </summary>
    [Function("SquareRefund")]
    public async Task<IActionResult> Refund(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-refund")] HttpRequest req,
        CancellationToken ct)
    {
        RefundRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<RefundRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException ex) { return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message }); }
        if (string.IsNullOrWhiteSpace(body?.paymentId))
            return new BadRequestObjectResult(new { error = "paymentId is required" });

        await using var conn = await _sql.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(
            "SELECT square_payment_id, amount_cents, refunded_cents, refund_due_cents, status FROM dbo.payments WHERE square_payment_id = @pid",
            new { pid = body.paymentId });
        if (row == null) return new NotFoundObjectResult(new { error = "Payment not found in our records." });
        long? total = (long?)row.amount_cents;
        if (total is null or <= 0)
            return new ConflictObjectResult(new { error = "No amount on record — refund this one in the Square Dashboard." });
        long refunded = (long)row.refunded_cents;
        long remaining = total.Value - refunded;
        if (remaining <= 0) return new ConflictObjectResult(new { error = "Already refunded in full." });

        long due = Math.Min((long?)row.refund_due_cents ?? total.Value, total.Value);
        long defaultCents = Math.Max(0, due - refunded);
        if (defaultCents == 0) defaultCents = remaining;
        long cents = body.amountCents is > 0 ? Math.Min(body.amountCents.Value, remaining) : defaultCents;

        using var result = await _square.RefundPaymentAsync(body.paymentId, cents, body.reason, ct);
        var refund = result.RootElement.GetProperty("refund");
        var refundId = refund.GetProperty("id").GetString()!;
        var refundStatus = refund.TryGetProperty("status", out var rs) ? rs.GetString() ?? "PENDING" : "PENDING";
        if (refundStatus == "COMPLETED")
            await CheckoutFulfillment.RecordRefundAsync(conn, refundId, body.paymentId, cents);
        else
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET status = 'REFUND_' + @rs, needs_refund = 0 WHERE square_payment_id = @pid AND status NOT IN ('REFUNDED')",
                new { rs = refundStatus, pid = body.paymentId });
        _log.LogInformation("SquareRefund: payment {PaymentId} refund {RefundId} {Amt}c -> {Status}", body.paymentId, refundId, cents, refundStatus);
        return new OkObjectResult(new { paymentId = body.paymentId, refundId, refundStatus, amountCents = cents });
    }
```

- [ ] **Step 3: `SalesSummary` — attribute web sales through `checkout_order_boxes`**

Replace the `webRows` query with:
```sql
SELECT p.square_payment_id,
       STRING_AGG('#' + CAST(m.pallet_number AS VARCHAR(10)), ', ') WITHIN GROUP (ORDER BY m.pallet_number) AS boxes,
       MIN(m.pallet_number) AS pallet_number, MIN(m.display_name) AS display_name, COUNT(*) AS box_count,
       SUM(COALESCE(v.total_cost, v.total_cost_units)) AS cost,
       SUM(CASE WHEN COALESCE(v.total_cost, v.total_cost_units) IS NULL THEN 1 ELSE 0 END) AS cost_missing
FROM dbo.payments p
JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id AND b.outcome = 'sold'
JOIN dbo.manifests m ON m.id = b.manifest_id
LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE p.created_at >= @begin
GROUP BY p.square_payment_id
```
In the Square-payments loop replace the cost/`sales.Add` block with:
```csharp
            decimal? cost = null;
            if (isWeb && (int)web!.cost_missing == 0) cost = (decimal?)web.cost;
            sales.Add(new SaleRow(
                payment_id: pid,
                created_at: created,
                amount_cents: amt,
                refunded_cents: refunded,
                channel: isWeb ? "web" : "floor",
                source: "square",
                pallet_number: isWeb ? (int?)web!.pallet_number : null,
                display_name: isWeb ? (string?)web!.display_name : null,
                boxes: isWeb ? (string?)web!.boxes : null,
                box_count: isWeb ? (int)web!.box_count : 0,
                cost: cost,
                margin_cents: isWeb && cost.HasValue ? (long?)(amt - refunded - (long)Math.Round(cost.Value * 100)) : null,
                note: null));
```
Replace the `adminRows` `NOT EXISTS` clause with:
```sql
  AND NOT EXISTS (SELECT 1 FROM dbo.payments p
                  JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id
                  WHERE b.manifest_id = m.id AND b.outcome = 'sold'
                    AND p.status <> 'REFUNDED')
```
In the admin-rows `sales.Add`, add `boxes: null, box_count: 1,` after `display_name:`. Change `SaleRow` to:
```csharp
    private sealed record SaleRow(
        string? payment_id, string? created_at, long amount_cents, long refunded_cents,
        string channel, string source, int? pallet_number, string? display_name,
        string? boxes, int box_count,
        decimal? cost, long? margin_cents, string? note);
```

- [ ] **Step 4: Staff API client and sales page**

`staff/js/api.js:86`:
```javascript
  squareRefund:     (paymentId, reason = null, amountCents = null) => api('POST', '/api/square-refund', { paymentId, reason, amountCents }),
```
`staff/js/sales.js`:
- sales table Box cell → `` <td>${x.boxes ? esc(x.boxes) + (x.box_count > 1 ? ` <span style="color:#888;font-size:11px;">(${x.box_count} boxes)</span>` : ` ${esc(x.display_name || '')}`) : x.pallet_number ? `#${x.pallet_number} ${esc(x.display_name || '')}` : '<span style="color:#999;">in-person sale</span>'}${x.note ? `<div style="font-size:11px;color:#888;">${esc(x.note)}</div>` : ''}</td> ``
- attention table: Box cell → `` <td>${r.boxes ? esc(r.boxes) : r.pallet_number ? `#${r.pallet_number} ${esc(r.display_name || '')}` : '<span class="flag">no box matched</span>'}</td> ``; Why cell → `` <td class="flag">${r.status === 'UNMATCHED' ? 'payment matched no box' : r.status.startsWith('PARTIAL') ? 'some boxes were already sold — partial refund due' : r.status.startsWith('REFUND_') && r.status !== 'REFUND_FLAGGED' ? 'refund ' + esc(r.status.slice(7).toLowerCase()) : 'box was already sold'}</td> ``; the button's `data-amt` → `${r.refund_due_cents ?? (r.amount_cents - (r.refunded_cents || 0))}`.
- payments table: Box cell → `` <td>${r.boxes ? esc(r.boxes) : r.pallet_number ? `#${r.pallet_number} ${esc(r.display_name || '')}` : '—'}</td> ``; Amount cell → `` <td class="money">${money(r.amount_cents)}${r.refunded_cents ? ` <span class="neg">(−${money(r.refunded_cents)})</span>` : ''}</td> ``.
- `refund()` confirm text → `` `Refund ${money(Number(btn.dataset.amt))} back to the buyer? This cannot be undone.` `` and call `apiClient.squareRefund(pid, null, Number(btn.dataset.amt))`; toast → `` `Refund ${r.refundStatus} — ${money(r.amountCents)}` ``.
- `staff/sales.html:67` copy → `Payments that landed on a box that was already sold (partial refunds for carts) or matched no box. Refund sends the amount owed back through Square.`

- [ ] **Step 5: Build, commit**

Run: `dotnet build api/api.csproj` → `Build succeeded.`
```powershell
git add api/Functions/SquareFunction.cs staff/js/api.js staff/js/sales.js staff/sales.html
git commit -m "feat(admin): partial refunds, multi-box payment rows, sales summary via checkout_order_boxes"
```

---

### Task 9: Config — per-environment webhook settings

**Files:**
- Modify: `api/Services/SquareService.cs` constructor (`:48-52`) and class summary

**Interfaces:** none new. App settings: `SQUARE_SANDBOX_WEBHOOK_SIGNATURE_KEY`, `SQUARE_PROD_WEBHOOK_SIGNATURE_KEY`, `SQUARE_SANDBOX_WEBHOOK_URL`, `SQUARE_PROD_WEBHOOK_URL` (each falls back to the unsuffixed name).

- [ ] **Step 1: Split the webhook settings by environment with fallback**

Replace the two `_webhookSignatureKey` / `_webhookUrl` assignments with:
```csharp
        string env = IsProduction ? "PROD" : "SANDBOX";
        _webhookSignatureKey = cfg[$"SQUARE_{env}_WEBHOOK_SIGNATURE_KEY"] ?? cfg["SQUARE_WEBHOOK_SIGNATURE_KEY"] ?? "";
        _webhookUrl          = cfg[$"SQUARE_{env}_WEBHOOK_URL"]           ?? cfg["SQUARE_WEBHOOK_URL"]           ?? "";
```
Add to the summary comment:
```
///   SQUARE_{SANDBOX|PROD}_WEBHOOK_SIGNATURE_KEY / _WEBHOOK_URL  per-environment
///                                 (fall back to the unsuffixed names)
///   SQUARE_PUBLIC_BASE_URL        site origin used in redirect URLs
///   SQUARE_SUPPORT_EMAIL          merchant_support_email on the hosted page
```

- [ ] **Step 2: Build, commit**

```powershell
dotnet build api/api.csproj
git add api/Services/SquareService.cs
git commit -m "chore(square): per-environment webhook settings with fallback"
```

---

### Task 10: Browser cart state + the card control (replaces "Buy now")

**Files:**
- Modify: `js/site.js` — header comment (`:4-6`), `fetchPublicPallets` (`:149-158`), `boxCardHtml` (`:193-196`), `bindCardClicks` (`:201-210`), the `checkout` section (`:276-292`), `showManifest` (`:353-367`, `:384`), `mountManifestModal` (`:323-331`), `initPage` (`:554-568`), the `window.NSL` export (`:589-598`)
- Modify: `css/site.css` — after `.view.buy:hover` (`:160`)

**Interfaces:**
- Consumes: `GET /api/public/checkout-status` (`{enabled, cartMax}`), `GET /api/public/pallets` rows (`manifest_id`, `pallet_number`, `display_name`, `publish_state`, `ask_price`, `photo_url`).
- Produces (inside the IIFE, exported on `NSL`): `cartIds()`, `cartHas(id)`, `cartAdd(id) → bool`, `cartRemove(id)`, `cartClear()`, `cartButtonHtml(id)`, `syncCartUi()`, `refreshPublicPallets()`, `onCartButton(id)`; a hook `let openCartHook = null` that Task 11 sets to `openCart`. `window.nslBuyBox(id)` stays as an alias (add + open).

- [ ] **Step 1: Header comment + fresh fetch**

In the header comment replace `window.nslCheckoutEnabled, window.nslBuyBox,` with `window.nslCheckoutEnabled, window.nslBuyBox (now: add to cart + open the cart),`. Replace `fetchPublicPallets` with:
```javascript
  let palletsPromise = null;
  let lastRows = null;                      // last successful feed — the cart bar totals from it
  function fetchPublicPallets() {
    if (!palletsPromise) {
      palletsPromise = fetch('/api/public/pallets', { credentials: 'omit' })
        .then(r => { if (!r.ok) throw new Error('http ' + r.status); return r.json(); })
        .then(rows => { lastRows = Array.isArray(rows) ? rows : []; return lastRows; })
        .catch(err => { palletsPromise = null; throw err; });
    }
    return palletsPromise;
  }
  // The cart drawer must never trust a feed cached for the page lifetime.
  function refreshPublicPallets() { palletsPromise = null; return fetchPublicPallets(); }
```

- [ ] **Step 2: Cart state (replace the whole `// ── checkout` section, lines 276-292)**

```javascript
  // ── cart state (spec §3) ─────────────────────────────────────────────────
  window.nslCheckoutEnabled = window.nslCheckoutEnabled || false;
  const CART_KEY = 'nsl.cart';
  let CART_MAX = 20;                        // overwritten by /api/public/checkout-status
  let cartMem = null;                       // in-memory fallback (private mode / storage blocked)
  let openCartHook = null;                  // set by the drawer module

  function cartIds() {
    if (cartMem) return cartMem.slice();
    try {
      const raw = JSON.parse(localStorage.getItem(CART_KEY) || '[]');
      return Array.isArray(raw) ? raw.filter(x => typeof x === 'string').map(x => x.toLowerCase()) : [];
    } catch { return []; }
  }
  function saveCart(ids) {
    const uniq = Array.from(new Set(ids.map(x => String(x).toLowerCase()))).slice(0, CART_MAX);
    try { localStorage.setItem(CART_KEY, JSON.stringify(uniq)); cartMem = null; }
    catch { cartMem = uniq; }
    syncCartUi();
    return uniq;
  }
  function cartHas(id) { return cartIds().includes(String(id).toLowerCase()); }
  function cartAdd(id) {
    const ids = cartIds();
    const key = String(id).toLowerCase();
    if (ids.includes(key)) return true;
    if (ids.length >= CART_MAX) {
      if (openCartHook) openCartHook(`Your cart is full (${CART_MAX} boxes). Remove one to add another.`);
      return false;
    }
    ids.push(key);
    saveCart(ids);
    return true;
  }
  function cartRemove(id) { saveCart(cartIds().filter(x => x !== String(id).toLowerCase())); }
  function cartClear() { saveCart([]); }

  function cartButtonHtml(id) {
    const on = cartHas(id);
    return `<button type="button" class="cart-btn" data-cart="${esc(id)}" aria-pressed="${on}">${on ? '✓ In cart' : '+ Add to cart'}</button>`;
  }

  // One tap adds; tapping "✓ In cart" opens the drawer (removal lives there,
  // so a stray second tap can't silently drop a box).
  function onCartButton(id) {
    if (cartHas(id)) { if (openCartHook) openCartHook(); return; }
    cartAdd(id);
  }

  function cartTotalCents(ids) {
    if (!lastRows) return null;
    const byId = new Map(lastRows.map(r => [String(r.manifest_id).toLowerCase(), r]));
    let cents = 0;
    for (const id of ids) { const r = byId.get(id); if (r && isLive(r)) cents += Math.round(Number(r.ask_price || 0) * 100); }
    return cents;
  }
  const dollars = cents => '$' + (cents / 100).toLocaleString('en-US', { minimumFractionDigits: cents % 100 ? 2 : 0, maximumFractionDigits: 2 });

  function syncCartUi() {
    const ids = cartIds();
    const n = ids.length;
    const on = !!window.nslCheckoutEnabled;
    document.querySelectorAll('button[data-cart]').forEach(b => {
      const inCart = ids.includes(String(b.getAttribute('data-cart')).toLowerCase());
      b.setAttribute('aria-pressed', String(inCart));
      b.textContent = inCart ? '✓ In cart' : '+ Add to cart';
    });
    document.querySelectorAll('.box-card[data-id]').forEach(c =>
      c.toggleAttribute('data-in-cart', ids.includes(String(c.getAttribute('data-id')).toLowerCase())));
    document.querySelectorAll('.cart-count').forEach(el => { el.textContent = String(n); });
    document.querySelectorAll('.cart-head-btn').forEach(el => {
      el.hidden = !on;
      el.setAttribute('aria-label', `Cart, ${n} box${n === 1 ? '' : 'es'}`);
    });
    const bar = document.getElementById('cart-bar');
    if (bar) {
      bar.hidden = !(on && n > 0);
      const cents = cartTotalCents(ids);
      bar.querySelector('.cart-bar-text').textContent =
        `${n} box${n === 1 ? '' : 'es'} in your cart` + (cents != null ? ` · ${dollars(cents)}` : '');
    }
  }

  // Legacy global (index.html once called it): add the box and open the cart.
  window.nslBuyBox = id => { if (cartAdd(id) && openCartHook) openCartHook(); };
```

- [ ] **Step 3: Card + delegation**

In `boxCardHtml` replace the `data-buy` line with:
```javascript
      ${window.nslCheckoutEnabled && live ? cartButtonHtml(p.manifest_id) : ''}
```
Replace `bindCardClicks` with:
```javascript
  function bindCardClicks(mount) {
    if (mount.dataset.nslBound) return;
    mount.dataset.nslBound = '1';
    mount.addEventListener('click', e => {
      const cartBtn = e.target.closest('button[data-cart]');
      if (cartBtn && mount.contains(cartBtn)) { onCartButton(cartBtn.getAttribute('data-cart')); return; }
      const view = e.target.closest('a.view[data-id]');
      if (view && mount.contains(view)) { e.preventDefault(); showManifest(view.getAttribute('data-id'), view.getAttribute('data-name')); }
    });
  }
```

- [ ] **Step 4: Manifest modal uses the same control**

In `mountManifestModal`, after `const bodyEl = overlay.querySelector('#mf-body');` add:
```javascript
    bodyEl.addEventListener('click', e => {
      const b = e.target.closest('button[data-cart]');
      if (b) onCartButton(b.getAttribute('data-cart'));
    });
```
In `showManifest` replace the `data-buy-modal` anchor line with:
```javascript
        ? `<p style="margin:0 0 16px;">${cartButtonHtml(p.manifest_id)} <span style="font-family:'Anton',sans-serif;font-size:20px;color:var(--nc-red);margin-left:10px;">${money(p.ask_price)}</span></p>`
```
Delete the `const bindBuy = …` block and both `bindBuy();` calls.

- [ ] **Step 5: `initPage` + export**

Replace `initPage` and `checkoutReady` with:
```javascript
  let inited = false;
  function initPage(opts) {
    const o = opts || {};
    if (!inited) {
      inited = true;
      mountJoinModal();
      if (typeof mountCart === 'function') mountCart();          // Task 11 defines it
      // Cart controls render only when the backend says Square checkout is
      // enabled (SQUARE_CHECKOUT_ENABLED app setting — the kill switch).
      checkoutReady().then(syncCartUi);
    }
    if (o.joinTrigger) document.querySelectorAll(o.joinTrigger).forEach(bindJoinTrigger);
    joinTriggers().forEach(bindJoinTrigger);
    relabelTriggers();
    return checkoutReady();
  }

  let checkoutProbe = null;
  function checkoutReady() {
    if (!checkoutProbe) {
      checkoutProbe = fetch('/api/public/checkout-status', { credentials: 'omit' })
        .then(x => x.json())
        .then(cs => {
          window.nslCheckoutEnabled = !!cs.enabled;
          if (Number(cs.cartMax) > 0) CART_MAX = Number(cs.cartMax);
          return window.nslCheckoutEnabled;
        })
        .catch(() => false);
    }
    return checkoutProbe;
  }
```
In the export replace `showManifest, buyBox, initPage, openJoin, checkoutReady,` with:
```javascript
    showManifest, initPage, openJoin, checkoutReady,
    // cart
    cartIds, cartHas, cartAdd, cartRemove, cartClear, cartButtonHtml, syncCartUi, refreshPublicPallets,
```

- [ ] **Step 6: CSS for the control**

After `.view.buy:hover { … }` in `css/site.css` add:
```css
/* Cart control on cards + manifest modal (spec §3) */
.cart-btn { background: var(--nc-navy); color: #fff; border: 0; cursor: pointer; font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase; padding: 9px 12px; min-height: 40px; }
.cart-btn:hover { background: #001a45; }
.cart-btn[aria-pressed="true"] { background: var(--warehouse-yellow); color: var(--ink); }
.box-card[data-in-cart] { outline: 3px solid var(--warehouse-yellow); outline-offset: -3px; }
```

- [ ] **Step 7: Manual check, commit**

Open `shop.html?view=all` via the local static server (`npx serve .` or the SWA CLI `swa start . --api-location api`); with the kill switch off there is no button; set `window.nslCheckoutEnabled = true` in the console and re-render (`NSL.renderBoxCards(await NSL.fetchPublicPallets(), '#shop-grid')`) → cards show "+ Add to cart"; click → "✓ In cart", card outlined yellow; reload → state persists (`localStorage['nsl.cart']`).
```powershell
git add js/site.js css/site.css
git commit -m "feat(cart): localStorage cart state + Add-to-cart control replaces Buy now"
```

---

### Task 11: Cart drawer, phone bottom bar, checkout call, bfcache/multi-tab handling

**Files:**
- Modify: `js/site.js` — add a `// ── cart drawer` section between the cart-state section and `// ── manifest modal`; the manifest and join modals switch to the shared overlay helpers
- Modify: `css/site.css` — after the cart control CSS from Task 10

**Interfaces:**
- Consumes: Task 10 state functions; `POST /api/public/checkout` (Task 4).
- Produces: `mountCart()`, `openCart(noticeText?)`, `closeCart()`, `renderCart()`, `checkoutCart()`, `openOverlay(overlay, firstFocus)`, `closeOverlay(overlay)`; sets `openCartHook = openCart`. Exported: `openCart`.

- [ ] **Step 1: Shared overlay helpers (put right before `// ── manifest modal`)**

```javascript
  // ── shared overlay behaviour (focus restore, Tab wrap, Escape, scroll lock)
  const OVERLAY_FOCUSABLE = 'button:not([disabled]), a[href], input:not([disabled]), [tabindex]:not([tabindex="-1"])';
  const overlayStack = [];
  function openOverlay(overlay, firstFocus) {
    overlayStack.push({ overlay, lastFocus: document.activeElement });
    overlay.hidden = false;
    document.body.style.overflow = 'hidden';
    (firstFocus || overlay.querySelector('.mf-close') || overlay).focus();
  }
  function closeOverlay(overlay) {
    const i = overlayStack.findIndex(s => s.overlay === overlay);
    const entry = i >= 0 ? overlayStack.splice(i, 1)[0] : null;
    overlay.hidden = true;
    if (overlayStack.length === 0) document.body.style.overflow = '';
    if (entry && entry.lastFocus && entry.lastFocus.focus) entry.lastFocus.focus();
  }
  document.addEventListener('keydown', e => {
    const top = overlayStack[overlayStack.length - 1];
    if (!top) return;
    if (e.key === 'Escape') { closeOverlay(top.overlay); return; }
    if (e.key !== 'Tab') return;
    const f = top.overlay.querySelectorAll(OVERLAY_FOCUSABLE);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  });
```
Then in `mountManifestModal`: replace `const close = () => { overlay.hidden = true; document.body.style.overflow = ''; };` with `const close = () => closeOverlay(overlay);` and delete its `document.addEventListener('keydown', …)` line; in `showManifest` replace `m.overlay.hidden = false; document.body.style.overflow = 'hidden';` with `openOverlay(m.overlay);`. In `mountJoinModal`: `const close = () => { closeOverlay(overlay); };` (drop the manual `lastFocus` handling and the `keydown` listener), and in `open` replace `overlay.hidden = false; document.body.style.overflow = 'hidden'; const first = …; if (first) first.focus();` with `openOverlay(overlay, n ? null : overlay.querySelector('#join-first'));`.

- [ ] **Step 2: The drawer module (insert after the cart-state section)**

```javascript
  // ── cart drawer + phone bar (spec §3) ────────────────────────────────────
  const CART_NOTE = "Pickup in Wake Forest — we'll reach out after payment to arrange it. Free delivery to the Raleigh Flea Market on Fridays.";
  let cart = null;

  function mountCart() {
    if (cart) return cart;
    const actions = document.querySelector('.site-nav .actions, .nav .actions');
    if (actions && !actions.querySelector('.cart-head-btn')) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'cart-head-btn';
      btn.hidden = true;
      btn.innerHTML = `🛒 Cart <span class="cart-count">0</span>`;
      btn.addEventListener('click', () => openCart());
      actions.appendChild(btn);
    }
    let bar = document.getElementById('cart-bar');
    if (!bar) {
      bar = document.createElement('div');
      bar.id = 'cart-bar';
      bar.className = 'cart-bar';
      bar.hidden = true;
      bar.innerHTML = `<span class="cart-bar-text"></span><button type="button" class="cart-bar-open">View cart →</button>`;
      bar.querySelector('.cart-bar-open').addEventListener('click', () => openCart());
      document.body.appendChild(bar);
    }
    let overlay = document.getElementById('cart-drawer');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'cart-drawer';
      overlay.className = 'mf-overlay cart-overlay';
      overlay.hidden = true;
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');
      overlay.setAttribute('aria-labelledby', 'cart-title');
      overlay.innerHTML = `
  <div class="mf-box cart-box">
    <button class="mf-close" type="button" aria-label="Close">&times;</button>
    <div class="mf-head"><h3 id="cart-title">Your cart</h3><p class="mf-sub" id="cart-sub"></p></div>
    <div class="mf-body" id="cart-body"></div>
    <div class="mf-foot cart-foot">
      <p class="cart-notice" id="cart-notice" aria-live="polite" hidden></p>
      <div class="cart-total"><span>Total</span><strong id="cart-total">$0</strong></div>
      <p class="note">${esc(CART_NOTE)}</p>
      <button type="button" class="btn btn-primary cart-checkout" id="cart-checkout">Checkout with Square →</button>
    </div>
  </div>`;
      document.body.appendChild(overlay);
    }
    const body = overlay.querySelector('#cart-body');
    const sub = overlay.querySelector('#cart-sub');
    const notice = overlay.querySelector('#cart-notice');
    const total = overlay.querySelector('#cart-total');
    const checkout = overlay.querySelector('#cart-checkout');
    overlay.addEventListener('click', e => { if (e.target === overlay) closeCart(); });
    overlay.querySelector('.mf-close').addEventListener('click', closeCart);
    body.addEventListener('click', e => {
      const rm = e.target.closest('button[data-remove]');
      if (!rm) return;
      cartRemove(rm.getAttribute('data-remove'));
      renderCart();
    });
    checkout.addEventListener('click', checkoutCart);
    // Back from Square's hosted page restores this page from bfcache with the
    // button still reading "One sec…" and possibly a box that just sold.
    window.addEventListener('pageshow', e => {
      if (!e.persisted) return;
      resetCheckoutBtn();
      syncCartUi();
      if (!overlay.hidden) renderCart();
    });
    // Another tab changed the cart.
    window.addEventListener('storage', e => {
      if (e.key !== CART_KEY) return;
      cartMem = null;
      syncCartUi();
      if (!overlay.hidden) renderCart();
    });
    cart = { overlay, body, sub, notice, total, checkout };
    openCartHook = openCart;
    return cart;
  }

  function cartNotice(msg) {
    const c = mountCart();
    c.notice.textContent = msg;
    c.notice.hidden = !msg;
  }

  function openCart(noticeText) {
    const c = mountCart();
    if (mf && !mf.overlay.hidden) closeOverlay(mf.overlay);   // drawer replaces the manifest modal
    if (c.overlay.hidden) openOverlay(c.overlay);
    renderCart().then(() => { if (noticeText) cartNotice(noticeText); });
  }
  function closeCart() { if (cart) closeOverlay(cart.overlay); }

  function validateCart(rows) {
    const byId = new Map((rows || []).map(r => [String(r.manifest_id).toLowerCase(), r]));
    const kept = [], removed = [];
    cartIds().forEach(id => {
      const r = byId.get(id);
      if (r && isLive(r) && Number(r.ask_price) > 0) kept.push(r);
      else removed.push(r ? r.pallet_number : null);
    });
    return { kept, removed };
  }

  function removedNotice(removed) {
    const known = removed.filter(n => n != null).map(n => '#' + n);
    if (known.length === removed.length && known.length === 1) return `BOX ${known[0]} was just sold — removed from your cart.`;
    if (known.length === removed.length) return `Boxes ${known.join(', ')} were just sold — removed from your cart.`;
    return 'Some boxes in your cart are no longer available — removed.';
  }

  async function renderCart() {
    const c = mountCart();
    const ids = cartIds();
    cartNotice('');
    c.sub.textContent = '';
    c.total.textContent = '$0';
    c.checkout.disabled = true;
    if (!ids.length) {
      c.body.innerHTML = `<p class="cart-empty">Your cart is empty. <a class="view" href="shop.html?view=all">Shop what's on the floor →</a></p>`;
      return;
    }
    c.body.innerHTML = '<p class="mf-loading">Checking your boxes…</p>';
    let rows;
    try { rows = await refreshPublicPallets(); }
    catch {
      // Never prune on a failed fetch — the ids are all we have.
      c.body.innerHTML = `<p class="mf-empty">Couldn't check your cart right now — try again in a moment or call ${PHONE}.</p>`;
      return;
    }
    const { kept, removed } = validateCart(rows);
    if (removed.length) {
      saveCart(kept.map(r => r.manifest_id));
      cartNotice(removedNotice(removed));
    }
    if (!kept.length) {
      c.body.innerHTML = `<p class="cart-empty">Everything in your cart just sold. <a class="view" href="shop.html?view=new">See what just dropped →</a></p>`;
      return;
    }
    let cents = 0;
    c.body.innerHTML = `<ul class="cart-list">` + kept.map(p => {
      cents += Math.round(Number(p.ask_price) * 100);
      return `
      <li class="cart-row">
        <span class="cart-thumb"${p.photo_url ? ` style="background-image:url('${esc(p.photo_url)}')"` : ''}></span>
        <span class="cart-info"><span class="box-no">BOX #${esc(p.pallet_number)}</span><span class="cart-name">${esc(p.display_name || ('Box #' + p.pallet_number))}</span></span>
        <span class="cart-price">${money(p.ask_price)}</span>
        <button type="button" class="cart-remove" data-remove="${esc(p.manifest_id)}" aria-label="Remove BOX #${esc(p.pallet_number)}">&times;</button>
      </li>`;
    }).join('') + `</ul>`;
    c.sub.textContent = `${kept.length} box${kept.length === 1 ? '' : 'es'} · pickup in Wake Forest`;
    c.total.textContent = dollars(cents);
    c.checkout.disabled = false;
  }

  function resetCheckoutBtn() {
    if (!cart) return;
    cart.checkout.textContent = 'Checkout with Square →';
    cart.checkout.disabled = cartIds().length === 0;
  }

  async function checkoutCart() {
    const c = mountCart();
    const ids = cartIds();
    if (!ids.length) return;
    c.checkout.disabled = true;
    c.checkout.textContent = 'One sec…';
    try {
      const r = await fetch('/api/public/checkout', {
        method: 'POST', credentials: 'omit',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids }),
      });
      let j = {};
      try { j = await r.json(); } catch { /* non-JSON body */ }
      if (r.ok && j.url) { window.location.href = j.url; return; }   // Square-hosted checkout
      if (r.status === 409) {
        const gone = Array.isArray(j.unavailable) ? j.unavailable.map(x => String(x).toLowerCase()) : null;
        if (gone && gone.length) saveCart(ids.filter(id => !gone.includes(id)));
        await renderCart();                                            // fresh fetch prunes the rest
        cartNotice(j.error || 'Some boxes in your cart are no longer available.');
        return;
      }
      if (r.status === 503) { cartNotice(`Online checkout is paused right now — call us at ${PHONE} and we'll take care of you.`); return; }
      cartNotice(j.error || `Couldn't start checkout — call us at ${PHONE} and we'll take care of you.`);
    } catch {
      cartNotice(`Couldn't start checkout — call us at ${PHONE} and we'll take care of you.`);
    } finally {
      resetCheckoutBtn();
    }
  }
```
Add `openCart,` to the `// cart` line of the export.

- [ ] **Step 3: CSS**

Append after the Task 10 cart CSS:
```css
/* Header cart button (desktop) */
.cart-head-btn { background: var(--nc-navy); color: #fff; border: 0; cursor: pointer; font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase; padding: 8px 12px; white-space: nowrap; }
.cart-head-btn[hidden] { display: none; }
.cart-head-btn .cart-count { display: inline-block; min-width: 20px; margin-left: 4px; padding: 1px 6px; border-radius: 10px; background: var(--warehouse-yellow); color: var(--ink); text-align: center; }
/* Fixed bottom bar — the header is not sticky on phones (shop/faq), so this is the always-visible path to checkout */
.cart-bar { position: fixed; left: 0; right: 0; bottom: 0; z-index: 900; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 16px; background: var(--nc-navy); color: #fff; box-shadow: 0 -6px 20px rgba(0,0,0,0.25); font-family: 'JetBrains Mono', monospace; font-size: 13px; }
.cart-bar[hidden] { display: none; }
.cart-bar-open { background: var(--warehouse-yellow); color: var(--ink); border: 0; cursor: pointer; font-family: 'Anton', sans-serif; font-size: 14px; letter-spacing: 0.04em; text-transform: uppercase; padding: 10px 16px; white-space: nowrap; }
@media (min-width: 901px) { .cart-bar { display: none; } }
body:has(.cart-bar:not([hidden])) { padding-bottom: 64px; }
/* Drawer — reuses .mf-* but slides in from the right */
.cart-overlay { justify-content: flex-end; padding: 0; }
.cart-box { max-width: 420px; height: 100%; max-height: none; border-radius: 0; }
@media (max-width: 560px) { .cart-box { max-width: 100%; } }
.cart-list { list-style: none; margin: 0; padding: 0; }
.cart-row { display: grid; grid-template-columns: 56px 1fr auto 32px; gap: 10px; align-items: center; padding: 10px 0; border-top: 1px solid #F0EAD9; }
.cart-row:first-child { border-top: 0; }
.cart-thumb { width: 56px; height: 42px; border-radius: 3px; background: #EFE9D8 center/cover no-repeat; }
.cart-info { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.cart-info .box-no { font-family: 'JetBrains Mono', monospace; font-size: 11px; }
.cart-name { font-size: 14px; font-weight: 600; line-height: 1.25; overflow: hidden; text-overflow: ellipsis; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.cart-price { font-family: 'Anton', sans-serif; font-size: 18px; color: var(--nc-red); white-space: nowrap; }
.cart-remove { background: none; border: 0; font-size: 26px; line-height: 1; color: #999; cursor: pointer; width: 32px; height: 40px; }
.cart-remove:hover { color: var(--nc-red); }
.cart-foot { flex-direction: column; align-items: stretch; }
.cart-total { display: flex; justify-content: space-between; align-items: baseline; font-family: 'JetBrains Mono', monospace; font-size: 12px; text-transform: uppercase; letter-spacing: 0.05em; color: #555; }
.cart-total strong { font-family: 'Anton', sans-serif; font-size: 26px; color: var(--nc-navy); letter-spacing: 0; }
.cart-checkout { width: 100%; text-align: center; min-height: 48px; }
.cart-checkout[disabled] { opacity: 0.55; cursor: not-allowed; }
.cart-notice { margin: 0; padding: 10px 12px; background: #FFF3C4; color: #7a5a00; font-size: 14px; border-left: 3px solid var(--warehouse-yellow); }
.cart-notice[hidden] { display: none; }
.cart-empty { color: #555; font-size: 15px; padding: 24px 0; text-align: center; line-height: 1.6; }
```

- [ ] **Step 4: Manual check (Playwright MCP or a browser) against the local SWA CLI with sandbox settings, then commit**

- Add two boxes → header badge "2" (desktop) / bottom bar "2 boxes · $410" at 400px width.
- Open drawer → both rows, total, Checkout enabled; remove one → row gone, total updates, card control flips back.
- In admin, archive a box that is in the cart → reopen drawer → notice "BOX #N was just sold — removed", row gone.
- Checkout → Square sandbox page loads; browser Back → button reads "Checkout with Square →" again (pageshow).
- Second tab: add a box → first tab badge updates (storage event).
- Keyboard: Tab cycles inside the drawer, Escape closes, focus returns to the header button.
```powershell
git add js/site.js css/site.css
git commit -m "feat(cart): drawer, phone bottom bar, combined checkout call, bfcache + multi-tab handling"
```

---

### Task 12: Thanks page for N boxes

**Files:**
- Modify: `thanks.html:32-38`, `:45-52`

- [ ] **Step 1: Copy + back link**

Replace `<a class="home" href="/">← Back to the inventory</a>` with `<a class="home" href="/shop.html?view=all">← Back to the inventory</a>`.

- [ ] **Step 2: Script**

Replace the `<script>` block with:
```html
  <script>
    // Square appends orderId/transactionId in production (nothing in sandbox);
    // we only read our own boxes= (legacy box=) and never decide sold-ness here.
    const q = new URLSearchParams(location.search);
    const raw = q.get('boxes') || q.get('box') || '';
    if (/^\d+(,\d+)*$/.test(raw)) {
      const nums = raw.split(',');
      const el = document.getElementById('boxno');
      el.textContent = nums.length === 1 ? 'BOX #' + nums[0] : nums.map(n => 'BOX #' + n).join('  ·  ');
      el.hidden = false;
      if (nums.length > 1) document.querySelector('h1').textContent = "They're yours!";
    }
    try { localStorage.removeItem('nsl.cart'); } catch { /* private mode */ }
  </script>
```

- [ ] **Step 3: Check, commit**

Open `thanks.html?boxes=12,14` → "They're yours!" with both numbers; `thanks.html?box=12` → "It's yours!" with BOX #12; `localStorage['nsl.cart']` is gone afterwards.
```powershell
git add thanks.html
git commit -m "feat(thanks): list every box on a cart order; clear the cart"
```

---

### Task 13: Reconcile on a schedule (GitHub Actions cron → keyed public route)

**Files:**
- Modify: `api/Functions/SquareFunction.cs` — split `Reconcile` into `ReconcileCoreAsync` + two triggers
- Create: `.github/workflows/square-reconcile.yml`

**Interfaces:**
- Consumes: Task 7's Reconcile body.
- Produces: `POST /api/public/reconcile-tick` with header `x-nsl-cron-key: <RECONCILE_CRON_KEY>` → same JSON as `/api/square-reconcile`; `401` without a matching key; `503` when the key is not configured. App setting `RECONCILE_CRON_KEY` (SWA) + repo secret `NSL_RECONCILE_KEY` (same value).

- [ ] **Step 1: Refactor**

Rename the body of `Reconcile` into `private async Task<object> ReconcileCoreAsync(CancellationToken ct)` returning the anonymous result object (throw `InvalidOperationException("Square is not configured.")` instead of returning 503). Then:
```csharp
    [Function("SquareReconcile")]
    public async Task<IActionResult> Reconcile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "square-reconcile")] HttpRequest req,
        CancellationToken ct)
    {
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };
        return new OkObjectResult(await ReconcileCoreAsync(ct));
    }

    /// <summary>
    /// Same sweep, callable by the GitHub Actions cron. /api/public/* is
    /// anonymous at the SWA layer, so a shared secret header is the auth.
    /// </summary>
    [Function("ReconcileTick")]
    public async Task<IActionResult> ReconcileTick(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/reconcile-tick")] HttpRequest req,
        CancellationToken ct)
    {
        var expected = _config["RECONCILE_CRON_KEY"];
        if (string.IsNullOrEmpty(expected))
            return new ObjectResult(new { error = "Cron key not configured." }) { StatusCode = 503 };
        var given = req.Headers["x-nsl-cron-key"].FirstOrDefault() ?? "";
        var a = System.Text.Encoding.UTF8.GetBytes(given);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b))
            return new UnauthorizedResult();
        if (!_square.Configured)
            return new ObjectResult(new { error = "Square is not configured." }) { StatusCode = 503 };
        var result = await ReconcileCoreAsync(ct);
        _log.LogInformation("ReconcileTick: {@Result}", result);
        return new OkObjectResult(result);
    }
```
Inject `IConfiguration config` into `SquareFunction`'s constructor (`private readonly IConfiguration _config;`), the same way `PalletsFunction` does.

- [ ] **Step 2: Workflow**

`.github/workflows/square-reconcile.yml`:
```yaml
# Runs the Square reconciliation sweep every 15 minutes (SWA-managed Functions
# have no timers). Needs repo secret NSL_RECONCILE_KEY = SWA app setting
# RECONCILE_CRON_KEY. Manual runs: Actions → Square reconcile → Run workflow.
name: Square reconcile
on:
  schedule:
    - cron: '*/15 * * * *'
  workflow_dispatch:
jobs:
  tick:
    runs-on: ubuntu-latest
    steps:
      - name: Call reconcile-tick
        run: |
          curl -fsS -X POST \
            -H "x-nsl-cron-key: ${{ secrets.NSL_RECONCILE_KEY }}" \
            https://northstateliquidators.com/api/public/reconcile-tick
```

- [ ] **Step 3: Build, commit; note the two settings for rollout**

```powershell
dotnet build api/api.csproj
git add api/Functions/SquareFunction.cs .github/workflows/square-reconcile.yml
git commit -m "feat(reconcile): keyed public tick route + 15-minute GitHub Actions cron"
```
Rollout (Task 14) sets `RECONCILE_CRON_KEY` on the SWA and `NSL_RECONCILE_KEY` in GitHub (generate with `[Convert]::ToHexString((1..32 | % { Get-Random -Max 256 }) -as [byte[]])`).

---

### Task 14: Sandbox end-to-end, PR, rollout, docs

**Files:**
- Modify: `SQUARE-INTEGRATION.md` (architecture + invariants), `docs/superpowers/specs/2026-09-13-cart-checkout-design.md` (status line)

- [ ] **Step 1: Local sandbox run**

Create `api/local.settings.json` (gitignored — confirm with `git check-ignore api/local.settings.json`; if not ignored, add it to `.gitignore` first) with `SqlConnectionString` (prod, read from the SWA settings — the additive tables are already there), `SQUARE_ENVIRONMENT=sandbox`, the sandbox token/location from the Square Developer Console, `SQUARE_CHECKOUT_ENABLED=true`, `SQUARE_PUBLIC_BASE_URL=http://localhost:4280`. Run `swa start . --api-location api` (SWA CLI) and open `http://localhost:4280/shop.html?view=all`. For webhooks, expose the local API with a tunnel (`ngrok http 4280` or VS Code port forwarding) and register `https://<tunnel>/api/square/webhook` as a sandbox subscription; put its signature key + exact URL in `SQUARE_SANDBOX_WEBHOOK_SIGNATURE_KEY` / `SQUARE_SANDBOX_WEBHOOK_URL`.

- [ ] **Step 2: Scenarios (record the outcome of each in the PR body)**

1. **Two-box cart pays.** Add boxes A and B → Checkout → sandbox card `4111 1111 1111 1111`, CVV 111, any future expiry → webhook → both boxes `sold`; `SELECT status FROM dbo.checkout_orders WHERE square_order_id=…` = `paid`; one `payments` row, `status='COMPLETED'`, `manifest_id IS NULL`; two `checkout_order_boxes` rows with `outcome='sold'`; thanks page shows both numbers and the cart is empty.
2. **Overlap, first pays.** Cart A `{1,2}` (tab 1) and cart B `{2,3}` (tab 2), both click Checkout (two links). Pay A → order B `status='canceled'`, `link_deleted_at` set (or `NULL` + a warning log if Square answered without `cancelled_order_id` — then run Reconcile and confirm it becomes set). Tab 2's Square page refuses to complete.
3. **Partial refund path.** Cart C `{4,5}` → get to the Square page but do not pay; in admin mark box 4 SOLD (PATCH publishState=sold) → the link is canceled in the DB. Simulate the delete race: temporarily set order C back to `open` with SQL, then pay it → box 5 `sold`, box 4 `outcome='unavailable'`, `payments.status='PARTIAL_REFUND_FLAGGED'`, `refund_due_cents` = box 4's price. In `staff/sales.html` the attention row shows "some boxes were already sold — partial refund due" with that amount; click Refund → Square refund for exactly that amount; the `refund.updated` webhook sets `refunded_cents`, `status='PARTIAL_REFUNDED'`, `needs_refund=0`.
4. **Missed webhook.** Delete the sandbox subscription, pay a one-box cart, `POST /api/square-reconcile` from the staff page → `healed: 1`, box sold, `payments.status='COMPLETED'`. Re-create the subscription.
5. **Price change cancels.** Add box D to a cart, click Checkout (link minted, don't pay); PATCH `listPrice` → `checkout_orders` row canceled; re-open the drawer → still there at the new price; Checkout → a NEW link (different `square_order_id`).
6. **Floor sale ignored.** In the sandbox Dashboard/Point of Sale simulator create a cash payment → webhook 200 `ignored: floor`, no `payments` row.
7. **Kill switch.** Set `SQUARE_CHECKOUT_ENABLED=false` → cards have no control, header button hidden, bottom bar hidden, `localStorage['nsl.cart']` untouched, `POST /api/public/checkout` → 503 and the drawer shows the "paused" notice.

- [ ] **Step 3: Browser pass at phone width**

With Playwright (MCP) at 400×800: bottom bar visible with a non-empty cart; drawer is full-width; rows readable; Checkout button ≥ 48px tall; no horizontal scroll.

- [ ] **Step 4: Update the docs**

In `SQUARE-INTEGRATION.md` replace the "One link per box, links are single-use." bullet and the "New pieces" table rows for `CheckoutFunction` / `manifests columns` with a short "Cart model (2026-09)" paragraph: one link per checkout attempt in `checkout_orders` (+ `checkout_order_boxes`, per-box `outcome`), fulfilment transactional in `CheckoutFulfillment`, competing links canceled DB-first, partial refunds via `refund_due_cents`, Reconcile ages links out at 7 days and runs from the GitHub cron. Point to the spec for detail. Change the spec's **Status** line to `APPROVED 2026-09-13 — implemented in PR #<n>`.
```powershell
git add SQUARE-INTEGRATION.md docs/superpowers/specs/2026-09-13-cart-checkout-design.md
git commit -m "docs: cart model in SQUARE-INTEGRATION.md; spec marked approved"
```

- [ ] **Step 5: PR**

```powershell
git push -u origin feature/cart-checkout
gh pr create --title "Cart + combined checkout (multi-box Square orders)" --body-file - <<'EOF'
## Why
Norm: "we need an add to cart button so people can buy multiple items." Spec: docs/superpowers/specs/2026-09-13-cart-checkout-design.md (reviewed by 4 agents + Square docs check).

## What
- DB: checkout_orders / checkout_order_boxes / payment_refunds (additive; db/cart-checkout.sql applied to prod before merge and re-run after)
- API: POST /api/public/checkout {ids}; one Square `order` link with N ad-hoc lines (coupons/tips off); transactional CheckoutFulfillment with per-box outcome + partial refund flag; DB-first cancel of competing links on sale / price change / state change / invoice / fake-sale / delete; Reconcile set-based + confirmed deletes + 7-day age-out + cron tick route; partial refunds; sales summary via checkout_order_boxes
- Site: Add-to-cart control (replaces Buy now), header badge, phone bottom bar, drawer with fresh re-validation, thanks page for N boxes
- Tests: api.Tests (SquarePayloads, Availability)

## Sandbox scenarios run
(paste results from Task 14 Step 2, 1–7)

## Rollout
1. SWA settings: SQUARE_PUBLIC_BASE_URL, SQUARE_SUPPORT_EMAIL, RECONCILE_CRON_KEY (+ GitHub secret NSL_RECONCILE_KEY), per-env webhook keys
2. Merge → deploy → re-run db/cart-checkout.sql
3. $1 live test: two $0.50 boxes, refund, confirm fee rate
4. Days later: db/cart-checkout-drop.sql

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01SBzv2wxVMJKqKEsddmWY2Q
EOF
gh pr checks --watch
```
Expected: `Build and Deploy pass`. Merge is Jeff's call.

- [ ] **Step 6: Production rollout (after merge)**

1. Set the SWA application settings listed in the PR body (Azure Portal → `stapp-nsl-website` → Environment variables). Production webhook subscription must include `payment.updated`, `payment.created`, `refund.updated`, `refund.created`.
2. Re-run `db/cart-checkout.sql` (Task 1 Step 3 command).
3. Live test: two boxes priced $0.50 each (or one $1 box) → pay with a real card → both flip SOLD → refund from admin → `refunded_cents` matches → confirm the 2.9% + 30¢ fee on the Square Dashboard.
4. Run the "Square reconcile" workflow manually once → 200 with counts.
5. Tell Rob: web orders stay OPEN in the Square Dashboard's Order Manager forever (Square can't close payment-link fulfillments via API) — that is normal.

---

### Task 15: Drop the legacy columns (days later)

**Files:**
- Create: `db/cart-checkout-drop.sql`

- [ ] **Step 1: Write the script**

```sql
-- ----------------------------------------------------------------------------
-- Cart + combined checkout — phase 2. Run ONLY after the cart code has been in
-- production for several days and `SELECT COUNT(*) FROM dbo.manifests WHERE
-- checkout_order_id IS NOT NULL AND checkout_order_id NOT IN (SELECT
-- square_order_id FROM dbo.checkout_orders)` returns 0.
-- ----------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM dbo.manifests m WHERE m.checkout_order_id IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM dbo.checkout_orders o WHERE o.square_order_id = m.checkout_order_id))
BEGIN
    RAISERROR('cart-checkout-drop: manifests still holds order ids not copied to checkout_orders — re-run db/cart-checkout.sql first.', 16, 1);
    RETURN;
END;

IF COL_LENGTH('dbo.manifests', 'checkout_link_id')    IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_link_id;
IF COL_LENGTH('dbo.manifests', 'checkout_order_id')   IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_order_id;
IF COL_LENGTH('dbo.manifests', 'checkout_url')        IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_url;
IF COL_LENGTH('dbo.manifests', 'checkout_created_at') IS NOT NULL ALTER TABLE dbo.manifests DROP COLUMN checkout_created_at;
GO
PRINT 'cart-checkout-drop: legacy manifests.checkout_* columns removed.';
```
Before running, `grep -rn "checkout_link_id\|checkout_order_id\|checkout_url\|checkout_created_at" api db --include=*.cs --include=*.sql` must show only `db/square-payments.sql` (superseded), `db/cart-checkout.sql` (guarded by `COL_LENGTH`), `db/wishlist4.sql` (a comment) and this file. `v_pallets` does not select these columns (verified 2026-09-13).

- [ ] **Step 2: Apply and commit**

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -InputFile db/cart-checkout-drop.sql -Verbose
git add db/cart-checkout-drop.sql
git commit -m "db: drop legacy manifests.checkout_* columns (cart phase 2)"
```
