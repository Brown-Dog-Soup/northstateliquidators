# Cart + Combined Checkout (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shoppers add any mix of live boxes to a cart, choose how they want them (warehouse pickup, $10 local delivery, or the free Friday flea-market drop), and pay for all of them — plus 7.25% NC sales tax — in one Square hosted checkout; every box on a paid order flips SOLD, with overlap between shoppers resolved by DB-first link cancellation and partial-refund flagging.

**Architecture:** Two new tables (`checkout_orders`, `checkout_order_boxes`) replace the per-box `manifests.checkout_*` columns as the correlation between a Square order and N boxes. A new `CheckoutFulfillment` service owns the one transactional routine that sells every box on a paid order, records per-box outcomes, and cancels competing links; the webhook and Reconcile both call it. The browser keeps an ids-only cart in localStorage and re-prices it from the public pallets feed. Square's hosted page is unchanged; the create call switches from `quick_pay` to a full `order` — which is also what lets us attach `order.taxes[]` (one 7.25% ADDITIVE LINE_ITEM-scope tax, applied to every box line) and, for delivery orders only, one `order.service_charges[]` entry carrying that same tax. The delivery choice is collected in **our** cart drawer, never by Square (`ask_for_shipping_address` stays off), and the 20-mile radius is a zip allow-list in `dbo.delivery_zips`.

**2026-09-14 revision:** this plan was rewritten alongside spec v3 to absorb Rob's tax / delivery / member-address requests. Task numbering is unchanged; Task 16 is appended. No card surcharge is built — spec §7.1 records why.

**Tech Stack:** .NET 8 isolated Azure Functions, Dapper + Microsoft.Data.SqlClient (explicit `SqlTransaction`), System.Text.Json, xUnit (`api.Tests`, created in the Phase 0 plan), vanilla JS/CSS (`js/site.js`, `css/site.css`), T-SQL, Square Checkout/Orders/Refunds APIs, GitHub Actions cron.

**Spec:** `docs/superpowers/specs/2026-09-13-cart-checkout-design.md` (§1–§8; tax and delivery are **§8**). The Phase 0 hotfix plan (`docs/superpowers/plans/2026-09-13-square-hotfix.md`) must be merged first — this plan assumes `SquareEvents` and `api.Tests` exist.

**The street-address work already shipped** (`355a6f5`, `db/member-address.sql`): `dbo.members.address1`/`address2`, the two join-modal inputs (`join-address1`/`join-address2`), and `sp_RegisterMember`'s two optional parameters are all in `main` and merged here. This plan consumes that result (spec §8.5) and must still not edit the join modal's form markup or `sp_RegisterMember` — Task 16 touches only the *success handler* in `js/site.js`.

## Global Constraints

- Branch: `feature/cart-checkout` (already exists with the spec committed). Rebase on `main` after the hotfix PR merges.
- Cart cap: **20** boxes. Kill switch `SQUARE_CHECKOUT_ENABLED` gates every cart control and the checkout endpoint (503).
- localStorage key: `nsl.cart` (JSON array of manifest_id strings). Thanks page query: `?boxes=12,14` (legacy `?box=N` still accepted).
- Square: `checkout_options.enable_coupon=false`, `allow_tipping=false`, `ask_for_shipping_address` omitted, **`shipping_fee` never sent**; line item `uid` = manifest_id (max 60), `name` ≤ 512, `payment_note` ≤ 500; refund `idempotency_key` ≤ **45 chars**; `DeletePaymentLink` only counts as confirmed when the response has `cancelled_order_id`.
- **Tax (AMENDED 2026-09-15 — read the amendment in Task 2 before writing the payload):** the live Square account already carries a catalog tax **"NC & Wake County Sales Tax"** (`catalog_object_id` `NJMJVQ3TQDEYCNQJJ5MGTCXT`, 7.25%, ADDITIVE, enabled, Wake Forest location). When the app setting `SQUARE_TAX_CATALOG_ID` is present the order references **that object** so web and floor sales reconcile under one named tax in Rob's reporting. (A rate change is **not** only an edit in Square: the rate we *quote* is a literal in four places — `taxPercent` in checkout-status, `TAX_PCT` in `js/site.js`, `CheckoutFulfillment.TaxRate`, `SquarePayloads.TaxPercent` — so it is an edit in Square **plus** a deploy. See the amended spec §8.1.) When it is absent the code falls back to the ad-hoc entry described next — a differently-named line in reporting is bad, but refusing to sell is worse, and the buyer is charged the right 7.25% either way.
- **Tax (ad-hoc fallback shape):** one `order.taxes[]` entry, `uid = "NC-SALES-725"`, `name = "NC sales tax (7.25%)"`, `percentage = "7.25"` (a *string*), `type = "ADDITIVE"`, `scope = "LINE_ITEM"`. Every line item gets `applied_taxes: [{ tax_uid: "NC-SALES-725" }]`. LINE_ITEM scope — not ORDER — because an ORDER-scope tax does not reach a service charge and NC taxes the delivery fee (spec §8.1).
- **Delivery:** `DELIVERY_CENTS = 1000`. For `delivery_method='delivery'` only, one `order.service_charges[]` entry `uid = "NSL-DELIVERY"`, `calculation_phase = "SUBTOTAL_PHASE"`, `scope = "ORDER"`, `treatment_type = "LINE_ITEM_TREATMENT"`, `taxable = true`, `applied_taxes: [{ tax_uid: "NC-SALES-725" }]`. Omit the array entirely for `pickup` and `flea`.
- **Never compute the authoritative tax.** `total_cents` / `tax_cents` / `delivery_cents` and every per-box `tax_cents` come out of the create response's `related_resources.orders[0]` (`total_money`, `total_tax_money`, `total_service_charge_money`, `line_items[].total_tax_money` matched on `uid`). Browser-side tax is display only.
- **Amount checks compare to `checkout_orders.total_cents`** (Square's `total_money`, tax and delivery included), never to the sum of box prices.
- `delivery_method` ∈ `pickup` | `delivery` | `flea`, default `pickup`. A `delivery` order must carry a zip that is `active` in `dbo.delivery_zips` and a non-empty address; validated server-side on every checkout, regardless of what the browser sent.
- **Revenue is goods only.** Tax and the delivery fee never enter `margin_cents` or the sales-summary amount.
- Availability for a `kind='link'` order: `publish_state='live' AND archived_at IS NULL AND is_ghost=0 AND invoice_id IS NULL`. For `kind='invoice'`: `publish_state <> 'sold'`.
- Reconcile ages out open link orders after **7 days**.
- SOLD only via `EXEC dbo.sp_SetPublishState`; history rows via `PalletsFunction.InsertHistoryAsync`, `changed_by` = the fulfilment source: `'square'` from the webhook, `'reconcile'` from the sweep.
- DB migrations are hand-applied to prod with Invoke-Sqlcmd (server `sql-nsl-prod-nc5h2y.database.windows.net`, db `sqldb-nsl-prod`, Entra token). `db/cart-checkout.sql` is additive and idempotent; `db/cart-checkout-drop.sql` runs days after deploy.
- `dotnet build api/api.csproj` and `dotnet test api.Tests/api.Tests.csproj` must pass locally; the PR preview build is the deploy-shaped check.
- **No Square sandbox exists for this account** (Jeff, 2026-09-15). All end-to-end verification runs against production with disposable test boxes and immediate refunds — see Task 14 Step 0 for the ground rules, including: never delete the live webhook subscription, and never run the API locally against production Square.
- Never render cost/margin on any `/api/public/*` route or in `js/site.js`.
- Commit messages end with the two attribution lines given in the session.

---

### Task 1: Additive DB migration `db/cart-checkout.sql`

**Files:**
- Create: `db/cart-checkout.sql`
- Modify: `db/square-payments.sql:1-8` (header note only)

**Interfaces:**
- Produces tables `dbo.checkout_orders`, `dbo.checkout_order_boxes`, `dbo.delivery_zips`; columns `dbo.payments.refund_due_cents BIGINT NULL`, `dbo.payments.refunded_cents BIGINT NOT NULL DEFAULT 0`. All later tasks' SQL depends on these exact names.
- `checkout_orders` splits the money: `subtotal_cents` (boxes) + `tax_cents` + `delivery_cents` = `total_cents`. `checkout_order_boxes.tax_cents` holds the per-box tax Square computed, so a per-box refund can return it (spec §8.6, §8.8).

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
        subtotal_cents   BIGINT        NOT NULL CONSTRAINT DF_co_subtotal DEFAULT 0,   -- boxes only
        tax_cents        BIGINT        NOT NULL CONSTRAINT DF_co_tax      DEFAULT 0,   -- Square total_tax_money
        delivery_cents   BIGINT        NOT NULL CONSTRAINT DF_co_delivery DEFAULT 0,   -- Square total_service_charge_money
        total_cents      BIGINT        NOT NULL,                  -- Square total_money (= the three above)
        delivery_method  VARCHAR(16)   NOT NULL CONSTRAINT DF_co_method   DEFAULT 'pickup',  -- pickup | delivery | flea
        delivery_zip     VARCHAR(10)   NULL,
        delivery_address NVARCHAR(300) NULL,
        member_number    CHAR(7)       NULL,
        created_at       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        closed_at        DATETIME2     NULL,
        link_deleted_at  DATETIME2     NULL,                      -- Square confirmed (cancelled_order_id present)
        CONSTRAINT CK_checkout_orders_kind   CHECK (kind IN ('link','invoice')),
        CONSTRAINT CK_checkout_orders_status CHECK (status IN ('open','paid','canceled')),
        CONSTRAINT CK_checkout_orders_method CHECK (delivery_method IN ('pickup','delivery','flea'))
    );
    CREATE INDEX IX_co_status ON dbo.checkout_orders (status, kind, created_at);
END;
GO

-- Re-runnable column adds, for a database that already has the table from an
-- earlier apply of this script (spec §8 landed after the first version).
IF COL_LENGTH('dbo.checkout_orders', 'subtotal_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD subtotal_cents BIGINT NOT NULL CONSTRAINT DF_co_subtotal DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'tax_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD tax_cents BIGINT NOT NULL CONSTRAINT DF_co_tax DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_cents') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_cents BIGINT NOT NULL CONSTRAINT DF_co_delivery DEFAULT 0;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_method') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_method VARCHAR(16) NOT NULL CONSTRAINT DF_co_method DEFAULT 'pickup';
IF COL_LENGTH('dbo.checkout_orders', 'delivery_zip') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_zip VARCHAR(10) NULL;
IF COL_LENGTH('dbo.checkout_orders', 'delivery_address') IS NULL
    ALTER TABLE dbo.checkout_orders ADD delivery_address NVARCHAR(300) NULL;
IF COL_LENGTH('dbo.checkout_orders', 'member_number') IS NULL
    ALTER TABLE dbo.checkout_orders ADD member_number CHAR(7) NULL;
GO

IF OBJECT_ID('dbo.checkout_order_boxes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.checkout_order_boxes (
        square_order_id  VARCHAR(64)      NOT NULL
            CONSTRAINT FK_cob_order REFERENCES dbo.checkout_orders (square_order_id),
        manifest_id      UNIQUEIDENTIFIER NOT NULL,   -- no FK on purpose: DeletePallet cleans up explicitly
        amount_cents     BIGINT           NOT NULL,   -- price at link time, EX tax
        tax_cents        BIGINT           NOT NULL CONSTRAINT DF_cob_tax DEFAULT 0,  -- this line's total_tax_money, from Square
        outcome          VARCHAR(16)      NULL,       -- NULL | 'sold' | 'unavailable'
        fulfilled_at     DATETIME2        NULL,
        CONSTRAINT PK_cob PRIMARY KEY (square_order_id, manifest_id),
        CONSTRAINT CK_cob_outcome CHECK (outcome IS NULL OR outcome IN ('sold','unavailable'))
    );
    CREATE INDEX IX_cob_manifest ON dbo.checkout_order_boxes (manifest_id);
END;
GO

IF COL_LENGTH('dbo.checkout_order_boxes', 'tax_cents') IS NULL
    ALTER TABLE dbo.checkout_order_boxes ADD tax_cents BIGINT NOT NULL CONSTRAINT DF_cob_tax DEFAULT 0;
GO

-- ----------------------------------------------------------------------------
-- Delivery radius as a list Rob can edit without a deploy (spec §8.4). A
-- curated allow-list, not a geocoder: one warehouse, one answer per zip, and
-- the call on a borderline zip is Rob's judgement, not a haversine.
-- The 'borderline' rows are seeded active = 0 until he confirms them.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('dbo.delivery_zips', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.delivery_zips (
        zip        VARCHAR(10)   NOT NULL PRIMARY KEY,
        fee_cents  INT           NOT NULL CONSTRAINT DF_dz_fee     DEFAULT 1000,
        active     BIT           NOT NULL CONSTRAINT DF_dz_active  DEFAULT 1,
        note       NVARCHAR(200) NULL,
        updated_at DATETIME2     NOT NULL CONSTRAINT DF_dz_updated DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- Seed only if empty, so re-running never undoes Rob's edits.
IF NOT EXISTS (SELECT 1 FROM dbo.delivery_zips)
BEGIN
    INSERT INTO dbo.delivery_zips (zip, active, note) VALUES
        ('27587', 1, 'Wake Forest'),        ('27588', 1, 'Wake Forest PO boxes'),
        ('27571', 1, 'Rolesville'),         ('27596', 1, 'Youngsville'),
        ('27525', 1, 'Franklinton'),        ('27522', 1, 'Creedmoor'),
        ('27614', 1, 'N Raleigh'),          ('27616', 1, 'N Raleigh'),
        ('27615', 1, 'N Raleigh'),          ('27613', 1, 'N Raleigh'),
        ('27617', 1, 'N Raleigh'),          ('27609', 1, 'N Raleigh'),
        ('27604', 1, 'NE Raleigh'),         ('27545', 1, 'Knightdale'),
        ('27591', 1, 'Wendell'),
        -- Borderline: inactive until Rob says yes (spec §8.4)
        ('27601', 0, 'Downtown Raleigh - confirm with Rob'),
        ('27605', 0, 'Raleigh - confirm with Rob'),
        ('27606', 0, 'Raleigh - confirm with Rob'),
        ('27607', 0, 'Raleigh - confirm with Rob'),
        ('27608', 0, 'Raleigh - confirm with Rob'),
        ('27610', 0, 'Raleigh - confirm with Rob'),
        ('27612', 0, 'Raleigh - confirm with Rob'),
        ('27597', 0, 'Zebulon - confirm with Rob'),
        ('27549', 0, 'Louisburg - confirm with Rob'),
        ('27560', 0, 'Morrisville - confirm with Rob'),
        ('27703', 0, 'Durham - confirm with Rob'),
        ('27529', 0, 'Garner - confirm with Rob');
END;
GO

IF COL_LENGTH('dbo.payments', 'refund_due_cents') IS NULL
    ALTER TABLE dbo.payments ADD refund_due_cents BIGINT NULL;
IF COL_LENGTH('dbo.payments', 'refunded_cents') IS NULL
    ALTER TABLE dbo.payments ADD refunded_cents BIGINT NOT NULL CONSTRAINT DF_payments_refunded DEFAULT 0;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_orders       TO nsl_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.checkout_order_boxes  TO nsl_api;
GRANT SELECT                          ON dbo.delivery_zips        TO nsl_api;
GO

-- Copy existing per-box links / invoices (old model) into the new tables.
-- Amount = current ask price (the old code never stored the link amount).
IF COL_LENGTH('dbo.manifests', 'checkout_order_id') IS NOT NULL
BEGIN
    -- Legacy links were minted before tax existed: subtotal = total, tax = 0,
    -- delivery = 0, method = pickup. Their buyers were not charged tax and we
    -- must not invent it retrospectively.
    INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status, subtotal_cents, total_cents, created_at)
    SELECT m.checkout_order_id,
           CASE WHEN m.invoice_id IS NOT NULL THEN 'invoice' ELSE 'link' END,
           m.checkout_link_id,
           COALESCE(m.checkout_url, m.invoice_url),
           CASE WHEN m.publish_state = 'sold' AND m.sold_to_inventory_at IS NULL THEN 'paid' ELSE 'open' END,
           CAST(ROUND(COALESCE(m.sale_price, m.list_price, 0) * 100, 0) AS BIGINT),
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

PRINT 'cart-checkout: checkout_orders + checkout_order_boxes + delivery_zips + payments.refund columns ready.';
```

- [ ] **Step 2: Mark the old script superseded**

At the top of `db/square-payments.sql`, after line 7 (`-- UNIQUE square_payment_id ...`), insert:
```sql
-- SUPERSEDED 2026-09: the manifests.checkout_* columns below are replaced by
-- dbo.checkout_orders / checkout_order_boxes (db/cart-checkout.sql) and dropped
-- by db/cart-checkout-drop.sql. Do NOT re-apply this file on prod.
```

- [ ] **Step 3: Apply the script to prod (it is additive; the new tables are unused until deploy)**

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -InputFile db/cart-checkout.sql -Verbose
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -Query "SELECT kind, status, COUNT(*) n FROM dbo.checkout_orders GROUP BY kind, status; SELECT COUNT(*) boxes FROM dbo.checkout_order_boxes; SELECT active, COUNT(*) n FROM dbo.delivery_zips GROUP BY active"
```
Expected: the PRINT line; then rows matching the current `manifests` state (as of 2026-09-13: 2 open links + 1 paid, 3 box rows); `delivery_zips` = 15 active + 12 inactive. Run the script a second time → no errors, same counts, and **the zip seed does not re-run** (idempotent).

> **Do not activate the borderline zips yourself.** They stay `active = 0` until Rob answers spec §8.9 Q2. Flipping one is a one-line `UPDATE`, which is the whole point of putting them in a table.

- [ ] **Step 4: Commit**

```powershell
git add db/cart-checkout.sql db/square-payments.sql
git commit -m "db: checkout_orders + checkout_order_boxes (additive cart migration)"
```

---

### Task 2: Square payloads (pure) + SquareService cart/refund/delete changes

> **AMENDMENT 2026-09-15 (supersedes the tax parts of the code and tests below where they differ).**
> Verified against the live Square account: it already carries a catalog tax
> `NJMJVQ3TQDEYCNQJJ5MGTCXT` — "NC & Wake County Sales Tax", 7.25%, ADDITIVE,
> enabled, Wake Forest location, `present_at_all_locations: false`. Build the tax
> entry **two ways**, selected by one optional app setting:
>
> - `SquareService` reads `SQUARE_TAX_CATALOG_ID` (optional) and exposes it as
>   `public string? TaxCatalogId { get; }`. Pass it into the payload builder.
> - `SquarePayloads.CartLink(...)` gains a `string? taxCatalogId` parameter.
>   When it is non-empty, the single `order.taxes[]` entry is
>   `{ uid = TaxUid, catalog_object_id = taxCatalogId, scope = "LINE_ITEM" }` —
>   no `name`, `percentage` or `type`, which come from the catalog object.
>   When it is null/empty, emit the ad-hoc entry exactly as written below.
> - **Everything else is identical in both shapes**: the same `uid` (`TaxUid`),
>   so every line item and the delivery service charge keep referencing it via
>   `applied_taxes: [{ tax_uid: TaxUid }]`, and the scope stays `LINE_ITEM` so
>   the tax reaches the delivery service charge (an ORDER-scope tax does not).
> - Tests: keep every existing assertion for the ad-hoc shape (call with
>   `taxCatalogId: null`), and add two more — one asserting that with a catalog
>   id the entry carries `catalog_object_id` and **no** `percentage`, and one
>   asserting the `tax_uid` wiring on line items and the service charge is the
>   same in both shapes.
> - Rollout note for Task 14: set `SQUARE_TAX_CATALOG_ID=NJMJVQ3TQDEYCNQJJ5MGTCXT`
>   on the SWA, and on the first live order confirm the Square page shows a single
>   tax line named "NC & Wake County Sales Tax" (not two lines, and not the
>   ad-hoc name) — that also proves the object is present at this location.


**Files:**
- Create: `api/Services/SquarePayloads.cs`
- Create: `api.Tests/SquarePayloadsTests.cs`
- Modify: `api/Services/SquareService.cs:66-102` (replace `CreatePaymentLinkAsync`), `:135-146` (`DeletePaymentLinkAsync`), `:288-311` (`RefundPaymentAsync`), plus a new `OrderLineUidsAsync`

**Interfaces:**
- Produces:
  - `record CartLine(Guid ManifestId, string Name, long AmountCents)`
  - `enum DeliveryMethod { Pickup, Delivery, Flea }` with `static string DeliveryMethods.ToDb(DeliveryMethod)` / `TryParse(string?, out DeliveryMethod)` → `pickup` | `delivery` | `flea`
  - `static object SquarePayloads.CartLink(IReadOnlyList<CartLine> lines, string locationId, string redirectUrl, string idempotencyKey, string referenceId, string paymentNote, string supportEmail, DeliveryMethod delivery, string? taxCatalogId)` — `taxCatalogId` is the **last** parameter and has **no default**, so both call sites state it explicitly.
  - `static string SquarePayloads.LineNote(DeliveryMethod)` — the buyer-visible per-line note
  - consts `SquarePayloads.TaxUid = "NC-SALES-725"`, `TaxName = "NC sales tax (7.25%)"`, `TaxPercent = "7.25"`, `DeliveryUid = "NSL-DELIVERY"`, `DeliveryName = "Local delivery (within 20 miles)"`, `DeliveryCents = 1000`
  - `static string SquarePayloads.RefundKey(string paymentId, long amountCents)` — always 45 chars
  - `static string SquarePayloads.BoxLineName(int palletNumber, string? displayName)`
  - `static string SquarePayloads.PaymentNote(IEnumerable<int> palletNumbers)`
  - `record CartLink(string Id, string OrderId, string Url, long TotalCents, long TaxCents, long DeliveryCents, IReadOnlyDictionary<Guid, long> LineTaxCents)`
  - `Task<CartLink> SquareService.CreateCartPaymentLinkAsync(IReadOnlyList<CartLine> lines, string redirectUrl, string idempotencyKey, string referenceId, string paymentNote, DeliveryMethod delivery, CancellationToken ct)` — `taxCatalogId` is **not** a parameter here; the service reads it from its own `TaxCatalogId` property and passes it to the builder.
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

    private static JsonElement Build(params CartLine[] lines) => Build(DeliveryMethod.Pickup, lines);

    private static JsonElement Build(DeliveryMethod delivery, params CartLine[] lines)
    {
        var payload = SquarePayloads.CartLink(lines, "LOC1", "https://x/thanks.html?boxes=1,2",
            "nsl-cart-abc", "NSL #1, #2", "NSL boxes #1, #2", "hello@example.com", delivery, null);
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

    // ── tax + delivery (spec §8.1, §8.2) ──────────────────────────────────

    [Fact]
    public void Order_carries_one_additive_line_item_scope_tax_at_725()
    {
        var order = Build(new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000)).GetProperty("order");
        var taxes = order.GetProperty("taxes");
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal("NC-SALES-725", taxes[0].GetProperty("uid").GetString());
        Assert.Equal("NC sales tax (7.25%)", taxes[0].GetProperty("name").GetString());
        Assert.Equal("7.25", taxes[0].GetProperty("percentage").GetString());   // string, not number
        Assert.Equal("ADDITIVE", taxes[0].GetProperty("type").GetString());
        // LINE_ITEM, not ORDER: an ORDER-scope tax never reaches a service charge.
        Assert.Equal("LINE_ITEM", taxes[0].GetProperty("scope").GetString());
    }

    [Fact]
    public void Every_box_line_applies_the_tax()
    {
        var items = Build(new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000))
            .GetProperty("order").GetProperty("line_items");
        foreach (var li in items.EnumerateArray())
        {
            var at = li.GetProperty("applied_taxes");
            Assert.Equal(1, at.GetArrayLength());
            Assert.Equal("NC-SALES-725", at[0].GetProperty("tax_uid").GetString());
        }
    }

    [Theory]
    [InlineData(DeliveryMethod.Pickup)]
    [InlineData(DeliveryMethod.Flea)]
    public void No_service_charge_when_there_is_no_delivery(DeliveryMethod m)
    {
        var order = Build(m, new CartLine(A, "BOX #1", 18000)).GetProperty("order");
        Assert.False(order.TryGetProperty("service_charges", out _));
    }

    [Fact]
    public void Delivery_adds_one_taxed_subtotal_phase_service_charge()
    {
        var order = Build(DeliveryMethod.Delivery, new CartLine(A, "BOX #1", 18000)).GetProperty("order");
        var sc = order.GetProperty("service_charges");
        Assert.Equal(1, sc.GetArrayLength());
        Assert.Equal("NSL-DELIVERY", sc[0].GetProperty("uid").GetString());
        Assert.Equal(1000, sc[0].GetProperty("amount_money").GetProperty("amount").GetInt64());
        Assert.Equal("USD", sc[0].GetProperty("amount_money").GetProperty("currency").GetString());
        // SUBTOTAL_PHASE = before tax, so NC's tax on the delivery charge lands.
        Assert.Equal("SUBTOTAL_PHASE", sc[0].GetProperty("calculation_phase").GetString());
        Assert.Equal("ORDER", sc[0].GetProperty("scope").GetString());
        Assert.Equal("LINE_ITEM_TREATMENT", sc[0].GetProperty("treatment_type").GetString());
        Assert.True(sc[0].GetProperty("taxable").GetBoolean());
        // taxable alone does nothing — applied_taxes is what charges the tax.
        Assert.Equal("NC-SALES-725", sc[0].GetProperty("applied_taxes")[0].GetProperty("tax_uid").GetString());
    }

    [Fact]
    public void Delivery_order_carries_the_tax_and_every_line_references_it()
    {
        // This test used to assert the ABSENCE of shipping_fee /
        // ask_for_shipping_address on a checkout_options literal that never had
        // those keys, so it could not fail. These assertions break if the tax
        // wiring is removed, which is what we actually need guarded.
        var order = Build(DeliveryMethod.Delivery, new CartLine(A, "BOX #1", 18000), new CartLine(B, "BOX #2", 25000))
            .GetProperty("order");
        var taxes = order.GetProperty("taxes");
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal(SquarePayloads.TaxUid, taxes[0].GetProperty("uid").GetString());
        foreach (var li in order.GetProperty("line_items").EnumerateArray())
            Assert.Equal(SquarePayloads.TaxUid, li.GetProperty("applied_taxes")[0].GetProperty("tax_uid").GetString());
    }

    [Theory]
    [InlineData(DeliveryMethod.Pickup, "Pickup in Wake Forest, NC")]
    [InlineData(DeliveryMethod.Delivery, "Local delivery — we'll call to schedule")]
    [InlineData(DeliveryMethod.Flea, "Friday pickup at the Raleigh Flea Market")]
    public void Line_note_names_the_chosen_handover(DeliveryMethod m, string expected)
    {
        var items = Build(m, new CartLine(A, "BOX #1", 18000)).GetProperty("order").GetProperty("line_items");
        Assert.Equal(expected, items[0].GetProperty("note").GetString());
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
        var k1 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 25000);
        var k2 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 25000);
        var k3 = SquarePayloads.RefundKey("FAKE-PAYMENT-ID-FOR-TESTS", 100);
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

/// <summary>How the buyer gets the boxes (spec §8.3). Collected in our cart
/// drawer, not by Square — the hosted page has no three-way choice.</summary>
public enum DeliveryMethod { Pickup, Delivery, Flea }

public static class DeliveryMethods
{
    public static string ToDb(DeliveryMethod m) => m switch
    {
        DeliveryMethod.Delivery => "delivery",
        DeliveryMethod.Flea => "flea",
        _ => "pickup"
    };

    /// <summary>Anything unrecognised (including null) falls back to pickup — the free, safe default.</summary>
    public static bool TryParse(string? s, out DeliveryMethod m)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "": case "pickup": m = DeliveryMethod.Pickup; return true;
            case "delivery":        m = DeliveryMethod.Delivery; return true;
            case "flea":            m = DeliveryMethod.Flea; return true;
            default:                m = DeliveryMethod.Pickup; return false;
        }
    }
}

/// <summary>
/// Pure builders for the Square request bodies the cart needs. Kept free of
/// I/O so they are unit-testable; SquareService serializes and posts them.
/// Limits (Square docs, verified 2026-09-13): line name ≤512, line uid ≤60,
/// payment_note ≤500, redirect_url ≤2048, refund idempotency_key ≤45.
/// </summary>
public static class SquarePayloads
{
    public const string PickupFieldTitle = "Name & phone for pickup";

    // Tax (spec §8.1). 7.25% = 4.75% NC + 2.00% Wake County + 0.50% transit.
    public const string TaxUid     = "NC-SALES-725";
    public const string TaxName    = "NC sales tax (7.25%)";
    public const string TaxPercent = "7.25";               // Square wants a STRING

    // Delivery (spec §8.2). Sent as an order service charge, never as
    // checkout_options.shipping_fee — Square materialises that one with
    // "taxable": false and no applied_taxes, and NC taxes a delivery charge.
    public const string DeliveryUid  = "NSL-DELIVERY";
    public const string DeliveryName = "Local delivery (within 20 miles)";
    public const long   DeliveryCents = 1000;

    /// <summary>Buyer-visible note on each box line — names the handover they picked.</summary>
    public static string LineNote(DeliveryMethod d) => d switch
    {
        DeliveryMethod.Delivery => "Local delivery — we'll call to schedule",
        DeliveryMethod.Flea     => "Friday pickup at the Raleigh Flea Market",
        _                       => "Pickup in Wake Forest, NC"
    };

    public static object CartLink(IReadOnlyList<CartLine> lines, string locationId, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, string supportEmail,
        DeliveryMethod delivery, string? taxCatalogId)
    {
        var note = LineNote(delivery);
        var order = new Dictionary<string, object?>
        {
            ["location_id"] = locationId,
            ["reference_id"] = Cap(referenceId, 40),   // backstop only — see the note below
            ["line_items"] = lines.Select(l => new
            {
                uid = l.ManifestId.ToString(),
                name = Cap(l.Name, 512),
                quantity = "1",
                base_price_money = new { amount = l.AmountCents, currency = "USD" },
                applied_taxes = new[] { new { tax_uid = TaxUid } },
                note
            }).ToArray(),
            // ONE tax object → the buyer sees one "NC sales tax (7.25%)" line,
            // not one per box. LINE_ITEM scope (not ORDER) is what lets the
            // delivery service charge reference it: an ORDER-scope tax is only
            // spread across line items.
            ["taxes"] = new[]
            {
                // AMENDED 2026-09-15 — pick ONE of these two shapes (see the
                // amendment block at the head of this task). Same uid either
                // way, so every line item's applied_taxes and the delivery
                // service charge keep referencing it unchanged:
                //   catalog:  new { uid = TaxUid, catalog_object_id = taxCatalogId, scope = "LINE_ITEM" }
                //   ad-hoc:   new { uid = TaxUid, name = TaxName, percentage = TaxPercent, type = "ADDITIVE", scope = "LINE_ITEM" }
                string.IsNullOrEmpty(taxCatalogId)
                    ? (object)new { uid = TaxUid, name = TaxName, percentage = TaxPercent, type = "ADDITIVE", scope = "LINE_ITEM" }
                    : (object)new { uid = TaxUid, catalog_object_id = taxCatalogId, scope = "LINE_ITEM" }
            }
        };

        if (delivery == DeliveryMethod.Delivery)
            order["service_charges"] = new[]
            {
                new
                {
                    uid = DeliveryUid,
                    name = DeliveryName,
                    amount_money = new { amount = DeliveryCents, currency = "USD" },
                    calculation_phase = "SUBTOTAL_PHASE",   // before tax, so NC's tax lands on it
                    scope = "ORDER",
                    treatment_type = "LINE_ITEM_TREATMENT",
                    taxable = true,                          // documentation-only; applied_taxes does the work
                    applied_taxes = new[] { new { tax_uid = TaxUid } }
                }
            };

        return new
        {
            idempotency_key = idempotencyKey,
            order,
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
    }

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

> **`Cap(referenceId, 40)` is a backstop, not the primary guard.** Square caps
> `reference_id` at 40 characters, and a full 20-box list is ~144 — truncating
> here would silently cut the list mid-number. The caller (Task 4) is what must
> keep it inside 40: box list while it fits, otherwise `"NSL {count} boxes"`.
> This `Cap` only exists so a caller bug can never make Square reject the order.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test api.Tests/api.Tests.csproj`
Expected: all SquarePayloadsTests pass (plus the Phase 0 tests).

- [ ] **Step 5: Change SquareService**

(a) Add the support email property. In the constructor (after `_webhookUrl = ...`):
```csharp
        SupportEmail = cfg["SQUARE_SUPPORT_EMAIL"] ?? "hello@northstateliquidators.com";
        TaxCatalogId = cfg["SQUARE_TAX_CATALOG_ID"];
```
and next to the other properties:
```csharp
    public string SupportEmail { get; }
    public string? TaxCatalogId { get; }
```

(b) Replace the whole `CreatePaymentLinkAsync` method (from its `/// <summary>` at line 66 through the closing brace at line 102) and the `PaymentLink` record with:
```csharp
    /// <summary>Per-box tax as Square computed it, keyed by the line uid (= manifest id).</summary>
    public sealed record CartLink(string Id, string OrderId, string Url,
        long TotalCents, long TaxCents, long DeliveryCents, IReadOnlyDictionary<Guid, long> LineTaxCents);

    /// <summary>
    /// One payment link for N boxes: a full `order` with one ad-hoc line item
    /// per box (uid = manifest_id) instead of quick_pay, plus the 7.25% NC tax
    /// and — for delivery orders — the $10 service charge. Idempotency key is
    /// per attempt (nsl-cart-{guid}); reuse is decided by our DB, not Square.
    /// Every money figure comes back out of Square's own order totals so our
    /// arithmetic can never disagree with what the buyer is charged, and the
    /// per-line tax means a partial refund can return that box's tax exactly.
    /// </summary>
    public async Task<CartLink> CreateCartPaymentLinkAsync(IReadOnlyList<CartLine> lines, string redirectUrl,
        string idempotencyKey, string referenceId, string paymentNote, DeliveryMethod delivery, CancellationToken ct)
    {
        var payload = SquarePayloads.CartLink(lines, LocationId, redirectUrl, idempotencyKey, referenceId, paymentNote, SupportEmail, delivery, TaxCatalogId);
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

        long total = lines.Sum(l => l.AmountCents);   // only a fallback; Square's number wins
        long tax = 0, deliveryCents = 0;
        var lineTax = new Dictionary<Guid, long>();
        if (doc.RootElement.TryGetProperty("related_resources", out var rr) &&
            rr.TryGetProperty("orders", out var orders) && orders.GetArrayLength() > 0)
        {
            var o = orders[0];
            total         = Money(o, "total_money") ?? total;
            tax           = Money(o, "total_tax_money") ?? 0;
            deliveryCents = Money(o, "total_service_charge_money") ?? 0;
            if (o.TryGetProperty("line_items", out var items))
                foreach (var li in items.EnumerateArray())
                    if (li.TryGetProperty("uid", out var uid) && Guid.TryParse(uid.GetString(), out var g))
                        lineTax[g] = Money(li, "total_tax_money") ?? 0;
        }
        else
        {
            _log.LogWarning("Square CreatePaymentLink {LinkId}: no related_resources.orders — tax/delivery recorded as 0",
                link.GetProperty("id").GetString());
        }

        return new CartLink(
            link.GetProperty("id").GetString()!,
            link.GetProperty("order_id").GetString()!,
            link.GetProperty("url").GetString()!,
            total, tax, deliveryCents, lineTax);

        static long? Money(JsonElement el, string name)
            => el.TryGetProperty(name, out var m) && m.TryGetProperty("amount", out var a) ? a.GetInt64() : null;
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
        var l = await CreateCartPaymentLinkAsync(new[] { new CartLine(Guid.Empty, name, amountCents) }, redirectUrl, idempotencyKey, name, note ?? name, DeliveryMethod.Pickup, ct);
        return new PaymentLink(l.Id, l.OrderId, l.Url);
    }
```
Re-run the build. Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```powershell
git add api/Services/SquarePayloads.cs api/Services/SquareService.cs api.Tests/SquarePayloadsTests.cs
git commit -m "feat(square): order-based cart payment links with NC sales tax + delivery charge, confirmed deletes, 45-char refund keys"
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
                    // Recovery path: we never saw the create response, so the
                    // tax/delivery split is unknown. Record the paid amount as
                    // the total and DERIVE the tax from the same 7.25% rate the
                    // payload uses, so that sum(box.tax_cents) == order.tax_cents
                    // still holds on a recovered order — Task 14 scenario 10
                    // asserts exactly that invariant, and a later partial refund
                    // reads the box rows (spec §8.8). Delivery stays 0: a
                    // recovered order cannot tell a $10 fee from $10 of goods.
                    // The row is flagged by the "recovered order" warning below
                    // for a human to reconcile against the Square dashboard.
                    // NOTE: derive the order's tax_cents as the SUM of the
                    // per-box derived values inserted just below — do not
                    // round the order total separately, or the two disagree by
                    // a cent or two and the invariant fails.
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, status, total_cents) VALUES (@oid, 'link', 'open', @total)",
                        new { oid = orderId, total = amountCents ?? 0 }, transaction: tx);
                    // ... and after the per-box INSERT below, bring the order row
                    // into agreement with its boxes:
                    //   UPDATE dbo.checkout_orders
                    //   SET tax_cents = (SELECT SUM(tax_cents) FROM dbo.checkout_order_boxes
                    //                    WHERE square_order_id = @oid),
                    //       subtotal_cents = (SELECT SUM(amount_cents) FROM dbo.checkout_order_boxes
                    //                    WHERE square_order_id = @oid)
                    //   WHERE square_order_id = @oid;
                    // tax_cents is DERIVED here, not quoted by Square: we never
                    // saw the create response for a recovered order. Leaving it
                    // at the column default of 0 would under-refund the buyer's
                    // tax on a later partial refund (spec §8.8), so compute it
                    // from the same 7.25% rate the payload uses.
                    await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents)
SELECT @oid, p.manifest_id, amt.amount_cents,
       CAST(ROUND(amt.amount_cents * 0.0725, 0) AS BIGINT)
FROM dbo.v_pallets p
CROSS APPLY (SELECT CAST(ROUND(COALESCE(p.sale_price, p.list_price, p.total_wholesale, 0) * 100, 0) AS BIGINT) AS amount_cents) amt
WHERE p.manifest_id IN @ids",
                        new { oid = orderId, ids }, transaction: tx);
                    _log.LogWarning("Fulfill: recovered order {OrderId} from {N} line uids", orderId, ids.Count);
                    order = await conn.QueryFirstOrDefaultAsync(
                        "SELECT kind, status, total_cents FROM dbo.checkout_orders WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                }

                string kind = (string)order.kind;
                var boxes = (await conn.QueryAsync(@"
SELECT b.manifest_id, b.amount_cents, b.tax_cents, b.outcome, m.pallet_number, m.publish_state, m.archived_at, m.is_ghost, m.invoice_id
FROM dbo.checkout_order_boxes b JOIN dbo.manifests m ON m.id = b.manifest_id
WHERE b.square_order_id = @oid ORDER BY b.manifest_id",
                    new { oid = orderId }, transaction: tx)).ToList();

                if (boxes.Count == 0)
                {
                    // Money in with nothing sold ALWAYS raises an attention row.
                    // Never mark this COMPLETED and walk away.
                    await conn.ExecuteAsync(
                        "UPDATE dbo.payments SET needs_refund = 1, status = 'UNMATCHED' WHERE square_payment_id = @pid",
                        new { pid = paymentId }, transaction: tx);
                    await conn.ExecuteAsync(
                        "UPDATE dbo.checkout_orders SET status = 'paid', closed_at = SYSUTCDATETIME() WHERE square_order_id = @oid",
                        new { oid = orderId }, transaction: tx);
                    tx.Commit();
                    _log.LogError("Fulfill: order {OrderId} payment {PaymentId} resolved to ZERO boxes — flagged UNMATCHED, refund owed", orderId, paymentId);
                    return new FulfillResult("unmatched", 0, 0, 0, new List<int>());
                }

                int sold = 0, unavailable = 0;
                long refundDue = 0;
                var soldIds = new List<Guid>();
                var pallets = new List<int>();
                foreach (var b in boxes)
                {
                    Guid mid = (Guid)b.manifest_id;
                    pallets.Add((int)b.pallet_number);
                    string? outcome = (string?)b.outcome;
                    // refundDue is always TAX-INCLUSIVE: the buyer paid tax on a
                    // box they are not getting, and we cannot remit it against a
                    // sale that did not happen (spec §8.8).
                    long boxDue = (long)b.amount_cents + (long)b.tax_cents;
                    if (outcome == "sold") { sold++; continue; }                     // re-entrant: already ours
                    if (outcome == "unavailable") { unavailable++; refundDue += boxDue; continue; }

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
                        refundDue += boxDue;
                        _log.LogWarning("Fulfill: BOX #{Num} on order {OrderId} no longer available (state {State}) — refund due {Due}c incl tax",
                            (object?)b.pallet_number, orderId, (string?)b.publish_state, boxDue);
                    }
                }

                long total = (long)order.total_cents;
                if (sold == 0 && refundDue > 0)
                {
                    // Nothing sold: there is no delivery to make either, so the
                    // delivery fee and its own tax go back too. total_cents is
                    // Square's total_money, so this is the whole payment.
                    refundDue = total > 0 ? total : refundDue;
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

                // ORDER total, not the line-item sum: with 7.25% tax and a $10
                // delivery charge the paid amount is legitimately larger than
                // the boxes (spec §8.7). total_cents IS Square's total_money.
                if (amountCents.HasValue && total > 0 && amountCents.Value != total)
                    _log.LogWarning("Fulfill: payment {PaymentId} amount {Amt} != order total {Total} (subtotal+tax+delivery)", paymentId, amountCents, total);

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
- Produces: `POST /api/public/checkout` body `{ "ids": ["<guid>", …], "delivery": "pickup"|"delivery"|"flea", "zip": "27587", "address": "…", "memberNumber": "2600001" }` → `200 { url }` | `400 { error, field? }` | `409 { error, unavailable: [guid…] }` | `503 { error }`. `POST /api/public/checkout/{id}` → same, cart of one, always `pickup`. `GET /api/public/checkout-status` → `{ enabled, cartMax, taxPercent, deliveryCents, deliveryZips: ["27587", …], fleaNote }`.
- The zip list on checkout-status is public on purpose: it is Rob's delivery radius, not customer data, and the drawer needs it to enable/disable a radio without a round trip. It is a **convenience copy** — `CreateCartCheckoutCore` re-checks every zip against `dbo.delivery_zips` (spec §8.3).

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
    // The zip list is read on every public page load, so keep an in-process
    // copy. Rob's edits show up within the TTL, and the checkout call itself
    // always re-reads dbo.delivery_zips, so the authority never goes stale.
    private static readonly TimeSpan ZipCacheTtl = TimeSpan.FromMinutes(5);
    private static (List<string> Zips, DateTime At)? _zipCache;
    public const string FleaNote = "Fridays at the Raleigh Flea Market — we'll confirm the stall and time by phone.";
    public sealed record CheckoutRequest(Guid[]? ids, string? delivery, string? zip, string? address, string? memberNumber);

    [Function("CheckoutStatus")]
    public async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "public/checkout-status")] HttpRequest req,
        CancellationToken ct)
    {
        // Rob's delivery radius, not customer data — safe to publish, and the
        // drawer needs it to enable/disable the $10 radio without a round trip.
        // Still re-validated server-side on every checkout (spec §8.3/§8.4).
        List<string> zips = new();
        if (_square.CheckoutEnabled)
        {
            var cached = _zipCache;
            if (cached != null && DateTime.UtcNow - cached.Value.At < ZipCacheTtl)
            {
                zips = cached.Value.Zips;
            }
            else
            {
                try
                {
                    await using var conn = await _sql.OpenAsync(ct);
                    zips = (await conn.QueryAsync<string>(
                        "SELECT zip FROM dbo.delivery_zips WHERE active = 1 ORDER BY zip")).ToList();
                    _zipCache = (zips, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Serve the last list we had rather than nothing: a quiet
                    // SQL hiccup here would otherwise hide the cart site-wide.
                    _log.LogError(ex, "CheckoutStatus: delivery_zips query failed — serving the cached list ({N} zips)", cached?.Zips.Count ?? 0);
                    zips = cached?.Zips ?? new List<string>();
                }
            }
        }
        return new OkObjectResult(new
        {
            enabled = _square.CheckoutEnabled && _square.Configured,
            cartMax = CartMax,
            taxPercent = 7.25m,
            deliveryCents = SquarePayloads.DeliveryCents,
            deliveryZips = zips,
            fleaNote = FleaNote
        });
    }

    /// <summary>Legacy single-box route (tabs loaded before the cart deploy): a cart of one, pickup.</summary>
    [Function("CreateCheckoutOne")]
    public Task<IActionResult> CreateCheckoutOne(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout/{id}")] HttpRequest req,
        Guid id, CancellationToken ct)
        => CreateCartCheckoutCore(new[] { id }, DeliveryMethod.Pickup, null, null, null, ct);

    [Function("CreateCheckout")]
    public async Task<IActionResult> CreateCheckout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/checkout")] HttpRequest req,
        CancellationToken ct)
    {
        CheckoutRequest? body;
        try { body = await JsonSerializer.DeserializeAsync<CheckoutRequest>(req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct); }
        catch (JsonException) { return new BadRequestObjectResult(new { error = "Invalid JSON" }); }
        if (!DeliveryMethods.TryParse(body?.delivery, out var method))
            return new BadRequestObjectResult(new { error = "Unknown delivery option.", field = "delivery" });
        return await CreateCartCheckoutCore(body?.ids ?? Array.Empty<Guid>(), method, body?.zip, body?.address, body?.memberNumber, ct);
    }

    private async Task<IActionResult> CreateCartCheckoutCore(Guid[] rawIds, DeliveryMethod method,
        string? rawZip, string? rawAddress, string? rawMember, CancellationToken ct)
    {
        if (!_square.CheckoutEnabled || !_square.Configured)
            return new ObjectResult(new { error = "Online checkout is not available right now." }) { StatusCode = 503 };

        var ids = rawIds.Where(g => g != Guid.Empty).Distinct().OrderBy(g => g).ToArray();
        if (ids.Length == 0) return new BadRequestObjectResult(new { error = "Add at least one box." });
        if (ids.Length > CartMax) return new BadRequestObjectResult(new { error = $"A cart holds at most {CartMax} boxes." });

        // Only a delivery order carries a zip/address; the other two ignore them.
        string? zip = null, address = null;
        string? member = string.IsNullOrWhiteSpace(rawMember) ? null
                       : (rawMember!.Trim().Length == 7 ? rawMember.Trim() : null);

        await using var conn = await _sql.OpenAsync(ct);

        if (method == DeliveryMethod.Delivery)
        {
            zip = (rawZip ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(zip, @"^\d{5}$"))
                return new BadRequestObjectResult(new { error = "Enter a 5-digit zip code so we can check delivery.", field = "zip" });
            // The browser's copy of the list is a convenience; this is the authority.
            bool ok = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.delivery_zips WHERE zip = @zip AND active = 1", new { zip }) > 0;
            if (!ok)
                return new BadRequestObjectResult(new
                {
                    error = $"We can't reach {zip} on our own truck — warehouse pickup and the Friday flea-market drop are both free.",
                    field = "zip"
                });
            address = (rawAddress ?? "").Trim();
            if (address.Length == 0)
                return new BadRequestObjectResult(new { error = "Add the street address for the delivery.", field = "address" });
            if (address.Length > 300) address = address[..300];
        }

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

        // Reuse an open link with the IDENTICAL (box, amount) set AND the same
        // delivery choice — open + identical means current, because price/state
        // changes cancel links. A different delivery choice prices differently,
        // so it has to mint a new link (spec §5).
        var wanted = lines.ToDictionary(l => l.ManifestId, l => l.AmountCents);
        string methodDb = DeliveryMethods.ToDb(method);
        var candidates = (await conn.QueryAsync(@"
SELECT o.square_order_id, o.url, o.delivery_method, o.delivery_zip, o.delivery_address, b.manifest_id, b.amount_cents
FROM dbo.checkout_orders o
JOIN dbo.checkout_order_boxes b ON b.square_order_id = o.square_order_id
WHERE o.status = 'open' AND o.kind = 'link' AND o.url IS NOT NULL
  AND o.delivery_method = @method
  AND ((o.delivery_zip IS NULL AND @zip IS NULL) OR o.delivery_zip = @zip)
  AND ((o.delivery_address IS NULL AND @addr IS NULL) OR o.delivery_address = @addr)
  AND o.square_order_id IN (SELECT square_order_id FROM dbo.checkout_order_boxes WHERE manifest_id = @first)",
            new { first = ids[0], method = methodDb, zip, addr = address })).GroupBy(r => (string)r.square_order_id);
        foreach (var g in candidates)
        {
            var set = g.ToDictionary(r => (Guid)r.manifest_id, r => (long)r.amount_cents);
            if (set.Count == wanted.Count && wanted.All(kv => set.TryGetValue(kv.Key, out var amt) && amt == kv.Value))
                return new OkObjectResult(new { url = (string)g.First().url });
        }

        var redirect = $"{_square.PublicBaseUrl}/thanks.html?boxes={string.Join(",", numbers)}";

        // Square caps reference_id at 40 chars and a full 20-box cart is ~144,
        // so build the box list ONLY while it fits; past that a count is more
        // use to Rob than a list truncated mid-number. SquarePayloads' Cap() is
        // a backstop behind this, not the guard.
        var boxList = "NSL " + string.Join(" ", numbers.Select(n => "#" + n));
        var referenceId = boxList.Length <= 40 ? boxList : $"NSL {numbers.Count} boxes";

        SquareService.CartLink link;
        try
        {
            link = await _square.CreateCartPaymentLinkAsync(lines, redirect,
                idempotencyKey: $"nsl-cart-{Guid.NewGuid():N}",
                referenceId: referenceId,
                paymentNote: SquarePayloads.PaymentNote(numbers), method, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A 4xx from Square here is most likely a bad or moved
            // SQUARE_TAX_CATALOG_ID. The setting's ABSENCE falls back to the
            // ad-hoc tax; a WRONG value does not, it just fails. Log that guess
            // explicitly so the cause is obvious from App Insights.
            _log.LogError(ex, "CreateCheckout: Square rejected the payment link for boxes {Boxes} ({Method}). Most likely cause: SQUARE_TAX_CATALOG_ID is wrong or that catalog tax has moved/been deleted — an absent setting falls back to the ad-hoc tax, a wrong value does not.",
                string.Join(",", numbers), methodDb);
            return new ObjectResult(new { error = "Couldn't start checkout — call us at (919) 526-0112 and we'll take care of you." }) { StatusCode = 502 };
        }

        // Every money column below is Square's own number (spec §8.6) — we do
        // not recompute the tax, so our row can never disagree with the receipt.
        long subtotal = link.TotalCents - link.TaxCents - link.DeliveryCents;
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_orders (square_order_id, kind, square_link_id, url, status,
                                 subtotal_cents, tax_cents, delivery_cents, total_cents,
                                 delivery_method, delivery_zip, delivery_address, member_number)
VALUES (@oid, 'link', @lid, @url, 'open', @sub, @tax, @del, @total, @method, @zip, @addr, @member)",
                new { oid = link.OrderId, lid = link.Id, url = link.Url,
                      sub = subtotal, tax = link.TaxCents, del = link.DeliveryCents, total = link.TotalCents,
                      method = methodDb, zip, addr = address, member }, transaction: tx);
            foreach (var l in lines)
                await conn.ExecuteAsync(@"
INSERT INTO dbo.checkout_order_boxes (square_order_id, manifest_id, amount_cents, tax_cents) VALUES (@oid, @mid, @amt, @tax)",
                    new { oid = link.OrderId, mid = l.ManifestId, amt = l.AmountCents,
                          tax = link.LineTaxCents.TryGetValue(l.ManifestId, out var t) ? t : 0L }, transaction: tx);
            tx.Commit();
        }

        _log.LogInformation("CreateCheckout: {N} box(es) {Boxes} {Method} -> link {LinkId} order {OrderId} subtotal {Sub} tax {Tax} delivery {Del} total {Total}",
            lines.Count, string.Join(",", numbers), methodDb, link.Id, link.OrderId, subtotal, link.TaxCents, link.DeliveryCents, link.TotalCents);
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
- Unchanged by the tax work, with one thing to keep straight: `pay.AmountCents` is now legitimately larger than the sum of the box prices (7.25% tax, plus $10 + tax on a delivery order). `FulfillOrderAsync` compares it to `checkout_orders.total_cents` — Square's own `total_money` — and nothing here should ever compare it to a line-item sum (spec §8.7). `refundDue` in the response body is tax-inclusive.

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
        if (orig.invoice_id != null && (string?)orig.publish_state != "sold")
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

> **Keep the `publish_state != "sold"` half of that guard exactly as written.**
> Commit `dca64cb` added it this morning so an already-sold box that still
> carries an invoice id can go through Sold → inventory. Writing the condition
> as `if (orig.invoice_id != null)` alone reverts that fix. Do not drop it.

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
and — **after** the existing `if (palletRows == 0) return new NotFoundResult();`
check, not immediately after `tx.Commit();` — add:
```csharp
            await _fulfill.RetireLinksAsync(conn, canceled, ct);
```
Order matters: a delete that matched no rows has cancelled nothing, so retiring
links there would kill live links for a box that still exists.

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
  - `GET /api/square-payments` rows gain `boxes` (string like `#12, #14` or null), `refund_due_cents`, `refunded_cents`, `delivery_method`.
  - `GET /api/sales-summary` `sales[]` rows gain `boxes` (string), `box_count` (int), `tax_cents`, `delivery_cents`, `delivery_method`. **`amount_cents` for a web sale becomes goods-only** (the sum of sold boxes' `amount_cents`), and `margin_cents` = goods − cost. Tax and the delivery fee are reported in their own columns and never enter revenue or margin (spec §8.6) — sales tax is money we hold for NCDOR, and the $10 covers Norm's fuel.

- [ ] **Step 1: `ListPayments` query**

Replace the SQL with:
```sql
SELECT TOP 100 p.square_payment_id, p.square_order_id, p.manifest_id,
       p.amount_cents, p.refunded_cents, p.refund_due_cents, p.status, p.needs_refund, p.created_at,
       m.pallet_number, m.display_name,
       o.delivery_method, o.tax_cents, o.delivery_cents,
       (SELECT STRING_AGG('#' + CAST(m2.pallet_number AS VARCHAR(10)), ', ') WITHIN GROUP (ORDER BY m2.pallet_number)
        FROM dbo.checkout_order_boxes b JOIN dbo.manifests m2 ON m2.id = b.manifest_id
        WHERE b.square_order_id = p.square_order_id AND b.outcome = 'sold') AS boxes
FROM dbo.payments p
LEFT JOIN dbo.manifests m ON m.id = p.manifest_id
LEFT JOIN dbo.checkout_orders o ON o.square_order_id = p.square_order_id
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
    ///
    /// Square's RefundPayment is AMOUNT-ONLY — there is no way to say "return
    /// this box and its tax"; itemised returns belong to the Orders
    /// returns/exchanges flow, which payment links do not give us. So
    /// refund_due_cents is stored tax-inclusive (spec §8.8) and we just send
    /// it. A staff-typed amountCents is NOT grossed up for tax — whoever types
    /// it owns it; the admin button's title attribute says so.
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
        // NEVER escalate a settled partial refund into a refund of the rest of
        // the payment. If nothing is owed and no amount was typed, say so.
        if (defaultCents == 0 && body.amountCents is not > 0)
            return new ConflictObjectResult(new { error = "Nothing outstanding to refund on this payment." });
        long cents = body.amountCents is > 0 ? Math.Min(body.amountCents.Value, remaining) : defaultCents;

        using var result = await _square.RefundPaymentAsync(body.paymentId, cents, body.reason, ct);
        var refund = result.RootElement.GetProperty("refund");
        var refundId = refund.GetProperty("id").GetString()!;
        var refundStatus = refund.TryGetProperty("status", out var rs) ? rs.GetString() ?? "PENDING" : "PENDING";
        if (refundStatus == "COMPLETED")
            await CheckoutFulfillment.RecordRefundAsync(conn, refundId, body.paymentId, cents);
        else
            // PENDING/APPROVED is Square accepting the request, not money back.
            // Leave needs_refund exactly as it was so the row stays on the
            // Needs-attention list until the refund.updated webhook confirms.
            await conn.ExecuteAsync(
                "UPDATE dbo.payments SET status = 'REFUND_' + @rs WHERE square_payment_id = @pid AND status NOT IN ('REFUNDED')",
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
       SUM(b.amount_cents) AS goods_cents,          -- what we actually sold, EX tax
       MIN(o.tax_cents)      AS tax_cents,          -- per order, not per box
       MIN(o.delivery_cents) AS delivery_cents,
       MIN(o.delivery_method) AS delivery_method,
       SUM(COALESCE(v.total_cost, v.total_cost_units)) AS cost,
       SUM(CASE WHEN COALESCE(v.total_cost, v.total_cost_units) IS NULL THEN 1 ELSE 0 END) AS cost_missing
FROM dbo.payments p
JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id AND b.outcome = 'sold'
JOIN dbo.manifests m ON m.id = b.manifest_id
JOIN dbo.checkout_orders o ON o.square_order_id = p.square_order_id
LEFT JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE p.created_at >= @begin
GROUP BY p.square_payment_id
```
`MIN(o.tax_cents)` is not an aggregate in spirit — `checkout_orders` is one row per payment, so MIN just satisfies the GROUP BY. Note these are the **order's** tax and delivery, which on a partial-unavailable order include tax for boxes that did not sell; the refund row next to it is where that comes back out.

In the Square-payments loop replace the cost/`sales.Add` block with:
```csharp
            decimal? cost = null;
            if (isWeb && (int)web!.cost_missing == 0) cost = (decimal?)web.cost;
            // Revenue is GOODS ONLY (spec §8.6): sales tax belongs to NCDOR and
            // the delivery fee covers the truck. Neither is ours, so neither
            // enters the sale amount or the margin.
            long goods = isWeb ? (long)web!.goods_cents : amt;
            sales.Add(new SaleRow(
                payment_id: pid,
                created_at: created,
                amount_cents: goods,
                refunded_cents: refunded,
                tax_cents: isWeb ? (long)web!.tax_cents : 0,
                delivery_cents: isWeb ? (long)web!.delivery_cents : 0,
                delivery_method: isWeb ? (string?)web!.delivery_method : null,
                channel: isWeb ? "web" : "floor",
                source: "square",
                pallet_number: isWeb ? (int?)web!.pallet_number : null,
                display_name: isWeb ? (string?)web!.display_name : null,
                boxes: isWeb ? (string?)web!.boxes : null,
                box_count: isWeb ? (int)web!.box_count : 0,
                cost: cost,
                margin_cents: isWeb && cost.HasValue ? (long?)(goods - (long)Math.Round(cost.Value * 100)) : null,
                note: null));
```
Then fix the **tiles**, not just the row (spec §8.6). The aggregates
`squareCents`, `webCents` and `floorCents` must accumulate **goods only** for a
web row — the same `goods` value the row uses — never the full `amt`, which
includes the buyer's sales tax and the delivery fee:
```csharp
            squareCents += goods;                       // NOT amt
            if (isWeb) webCents += goods; else floorCents += goods;
            // Tax and delivery are money we hold, not revenue: their own
            // accumulator, their own tile.
            taxAndDeliveryCents += isWeb ? (long)web!.tax_cents + (long)web!.delivery_cents : 0;
```
Add `taxAndDeliveryCents` to the summary response next to the existing totals and
render it as its own tile labelled `Tax + delivery collected` with a one-line
caption: `Held for NCDOR and the truck — not revenue.`
**Task 14 scenario 10 must additionally assert the Website tile reads $43.00,
not $46.12** — getting the row right while the tile still counts tax is the
exact failure this ruling exists to catch.

`refunded` no longer appears in `margin_cents`: boxes that came back are `outcome='unavailable'` and the join already excludes them, so subtracting the refund too would double-count. The one case this reads high is a *goodwill* partial refund on a fully-sold order — `refunded_cents` is still displayed beside it so staff can see it (spec §8.6, accepted imprecision).
Replace the `adminRows` `NOT EXISTS` clause with:
```sql
  AND NOT EXISTS (SELECT 1 FROM dbo.payments p
                  JOIN dbo.checkout_order_boxes b ON b.square_order_id = p.square_order_id
                  WHERE b.manifest_id = m.id AND b.outcome = 'sold'
                    AND p.status <> 'REFUNDED')
```
In the admin-rows `sales.Add`, add `boxes: null, box_count: 1,` after `display_name:` and `tax_cents: 0, delivery_cents: 0, delivery_method: null,` after `refunded_cents:`. Change `SaleRow` to:
```csharp
    private sealed record SaleRow(
        string? payment_id, string? created_at, long amount_cents, long refunded_cents,
        long tax_cents, long delivery_cents, string? delivery_method,
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
- sales table: after the Amount cell add a **Tax / Delivery** cell → `` <td class="money">${x.tax_cents ? money(x.tax_cents) : '—'}${x.delivery_cents ? ` <span title="${esc(x.delivery_method || '')}">+${money(x.delivery_cents)} del</span>` : ''}</td> ``, and add the matching `<th>Tax / Del</th>` to the header row. Put a one-line note under the table: `Amount and margin are goods only — sales tax is held for NCDOR and the delivery fee covers the truck.`
- attention table: the refund button gains `title="Refunds the amount owed including that box's sales tax. Typing a different amount does NOT add tax."`
- payments table: Box cell shows the handover when it is not pickup → append `` ${r.delivery_method && r.delivery_method !== 'pickup' ? ` <span class="tag">${r.delivery_method === 'flea' ? 'flea market' : 'delivery'}</span>` : ''} ``
- the sales table now has **7** columns, so bump the empty-state row from `colspan="6"` to `colspan="7"` (the "No sales in this window." row in `staff/js/sales.js`, which would otherwise sit short of the new Tax / Del column)
- `staff/sales.html:67` copy → `Payments that landed on a box that was already sold (partial refunds for carts) or matched no box. Refund sends the amount owed — the box price plus the sales tax the buyer paid on it — back through Square.`

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

> **Line numbers in this task are stale — the merge with main inserted `renderCounts` (the live homepage stats bar). Work from the quoted strings, never from line numbers. The `// ── checkout` section is now around line 312, not 276. Do not touch `renderCounts`, `fmtCountPrice`, or their export — `index.html` calls `NSL.renderCounts()` and deleting them breaks the homepage.**

**Files:**
- Modify: `js/site.js` — the header comment block, `fetchPublicPallets`, `boxCardHtml`, `bindCardClicks`, the `// ── checkout` section, `showManifest`, `mountManifestModal`, `initPage`, the `window.NSL` export (locate each by these names; the line numbers this task once carried are wrong)
- Modify: `css/site.css` — immediately after the `.view.buy:hover { … }` rule

**Interfaces:**
- Consumes: `GET /api/public/checkout-status` (`{enabled, cartMax, taxPercent, deliveryCents, deliveryZips, fleaNote}`), `GET /api/public/pallets` rows (`manifest_id`, `pallet_number`, `display_name`, `publish_state`, `ask_price`, `photo_url`), `localStorage['nsl.member' | 'nsl.zip' | 'nsl.addr']` (Task 16).
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

- [ ] **Step 2: Cart state (replace the whole `// ── checkout` section — from the `// ── checkout (same behaviour index.html had inline)` comment down to, but not including, the next `// ──` section comment. Do not disturb `renderCounts` / `fmtCountPrice` above it.)**

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
      // Kill switch off = no cart controls at all, not a relabelled one.
      b.hidden = !on;
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
  // Delivery config, filled from /api/public/checkout-status. The zip list is
  // Rob's delivery radius (spec §8.4), not customer data; the server re-checks
  // every zip on checkout, so this copy is only here to enable/disable a radio
  // without a round trip.
  let TAX_PCT = 7.25, DELIVERY_CENTS = 1000, DELIVERY_ZIPS = [], FLEA_NOTE = '';
  function checkoutReady() {
    if (!checkoutProbe) {
      checkoutProbe = fetch('/api/public/checkout-status', { credentials: 'omit' })
        .then(x => x.json())
        .then(cs => {
          window.nslCheckoutEnabled = !!cs.enabled;
          if (Number(cs.cartMax) > 0) CART_MAX = Number(cs.cartMax);
          if (Number(cs.taxPercent) > 0) TAX_PCT = Number(cs.taxPercent);
          if (Number(cs.deliveryCents) > 0) DELIVERY_CENTS = Number(cs.deliveryCents);
          DELIVERY_ZIPS = Array.isArray(cs.deliveryZips) ? cs.deliveryZips.map(String) : [];
          FLEA_NOTE = cs.fleaNote || '';
          return window.nslCheckoutEnabled;
        })
        .catch(() => false);
    }
    return checkoutProbe;
  }

  // ── delivery choice (spec §8.3) ──────────────────────────────────────────
  // Remembered per device, like the cart itself. 'pickup' is always the safe
  // default: it is free and it is what NSL did before this existed.
  const DELIV_KEY = 'nsl.delivery';
  function storedZip()  { try { return localStorage.getItem('nsl.zip')  || ''; } catch { return ''; } }
  function storedAddr() { try { return localStorage.getItem('nsl.addr') || ''; } catch { return ''; } }
  function zipQualifies(z) { return /^\d{5}$/.test(z) && DELIVERY_ZIPS.includes(z); }
  function deliveryChoice() {
    let d = 'pickup';
    try { d = localStorage.getItem(DELIV_KEY) || 'pickup'; } catch { /* private mode */ }
    if (d === 'delivery' && !zipQualifies(storedZip())) return 'pickup';   // never leave a $10 selected that no longer qualifies
    return (d === 'delivery' || d === 'flea') ? d : 'pickup';
  }
  function setDeliveryChoice(d) { try { localStorage.setItem(DELIV_KEY, d); } catch { /* ignore */ } }
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

- [ ] **Step 7: Drop the "We ship." clause from `MF_NOTE`**

`MF_NOTE` in `js/site.js` still reads `… $10 delivery within 20 miles of our warehouse · We ship. Call to claim this box.` Nothing is shipped by carrier (spec §1) and the drawer now offers exactly three handovers. Remove that clause:
```javascript
  const MF_NOTE = 'Pickup in Wake Forest · Free delivery to the Raleigh Flea Market every Friday · $10 delivery within 20 miles of our warehouse · Call to claim this box.';
```

- [ ] **Step 8: Manual check, commit**

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
- Consumes: Task 10 state functions and the delivery helpers (`deliveryChoice`, `setDeliveryChoice`, `zipQualifies`, `storedZip`, `storedAddr`, `TAX_PCT`, `DELIVERY_CENTS`, `DELIVERY_ZIPS`, `FLEA_NOTE`); `POST /api/public/checkout` (Task 4).
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

      <fieldset class="cart-deliv" id="cart-deliv">
        <legend>How do you want them?</legend>
        <label class="deliv-opt"><input type="radio" name="nsl-deliv" value="pickup" checked>
          <span class="deliv-label">Pick up at our Wake Forest warehouse</span><span class="deliv-price">Free</span></label>
        <label class="deliv-opt" id="deliv-opt-delivery"><input type="radio" name="nsl-deliv" value="delivery" disabled>
          <span class="deliv-label">Delivered to you<span class="deliv-to" id="deliv-to"></span></span><span class="deliv-price">${dollars(DELIVERY_CENTS)}</span></label>
        <div class="deliv-zip" id="deliv-zip-row">
          <label for="deliv-zip">Your zip</label>
          <input id="deliv-zip" inputmode="numeric" maxlength="5" autocomplete="postal-code" placeholder="27587">
          <button type="button" class="btn btn-ghost" id="deliv-check">Check</button>
        </div>
        <div class="deliv-addr" id="deliv-addr-row" hidden>
          <label for="deliv-addr">Street address</label>
          <input id="deliv-addr" maxlength="300" autocomplete="street-address" placeholder="123 Main St, Wake Forest">
        </div>
        <label class="deliv-opt"><input type="radio" name="nsl-deliv" value="flea">
          <span class="deliv-label">Meet us at the Raleigh Flea Market on Friday</span><span class="deliv-price">Free</span></label>
      </fieldset>

      <dl class="cart-receipt" id="cart-receipt">
        <div><dt>Subtotal</dt><dd id="cart-sub-amt">$0</dd></div>
        <div id="cart-deliv-line" hidden><dt>Delivery</dt><dd id="cart-deliv-amt">$0</dd></div>
        <div><dt>Sales tax (${TAX_PCT}%)</dt><dd id="cart-tax-amt">$0</dd></div>
      </dl>
      <div class="cart-total"><span>Total</span><strong id="cart-total">$0</strong></div>
      <p class="note" id="cart-note">${esc(CART_NOTE)}</p>
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
    const deliv = {
      set: overlay.querySelector('#cart-deliv'),
      radios: Array.from(overlay.querySelectorAll('input[name="nsl-deliv"]')),
      optDelivery: overlay.querySelector('#deliv-opt-delivery'),
      to: overlay.querySelector('#deliv-to'),
      zipRow: overlay.querySelector('#deliv-zip-row'),
      zip: overlay.querySelector('#deliv-zip'),
      check: overlay.querySelector('#deliv-check'),
      addrRow: overlay.querySelector('#deliv-addr-row'),
      addr: overlay.querySelector('#deliv-addr'),
    };
    const receipt = {
      sub: overlay.querySelector('#cart-sub-amt'),
      delLine: overlay.querySelector('#cart-deliv-line'),
      del: overlay.querySelector('#cart-deliv-amt'),
      tax: overlay.querySelector('#cart-tax-amt'),
      note: overlay.querySelector('#cart-note'),
    };
    deliv.radios.forEach(r => r.addEventListener('change', () => {
      if (r.checked) { setDeliveryChoice(r.value); syncDelivery(); renderTotals(); }
    }));
    deliv.check.addEventListener('click', checkZip);
    deliv.zip.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkZip(); } });
    deliv.addr.addEventListener('input', () => { try { localStorage.setItem('nsl.addr', deliv.addr.value.trim()); } catch {} });
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
    cart = { overlay, body, sub, notice, total, checkout, deliv, receipt };
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

  // Enable/disable the $10 option from what we know about the shopper's zip.
  // If a member signed up on this device we already have it (spec §8.5) and
  // the zip input never appears; otherwise they type it once.
  function syncDelivery() {
    const c = mountCart();
    const z = storedZip();
    const ok = zipQualifies(z);
    const choice = deliveryChoice();
    c.deliv.radios.forEach(r => { r.checked = r.value === choice; });
    c.deliv.optDelivery.querySelector('input').disabled = !ok;
    c.deliv.optDelivery.classList.toggle('is-off', !ok);
    c.deliv.to.textContent = ok ? ` (to ${z})` : '';
    c.deliv.zipRow.hidden = ok;
    c.deliv.addrRow.hidden = !(ok && choice === 'delivery');
    if (c.deliv.addrRow.hidden === false && !c.deliv.addr.value) c.deliv.addr.value = storedAddr();
    c.receipt.note.textContent =
      choice === 'flea' ? (FLEA_NOTE || CART_NOTE)
      : choice === 'delivery' ? "We'll call to schedule the drop — usually within a couple of days."
      : CART_NOTE;
  }

  function checkZip() {
    const c = mountCart();
    const z = (c.deliv.zip.value || '').trim();
    if (!/^\d{5}$/.test(z)) { cartNotice('Enter a 5-digit zip code.'); c.deliv.zip.focus(); return; }
    try { localStorage.setItem('nsl.zip', z); } catch { /* ignore */ }
    if (zipQualifies(z)) {
      cartNotice(`Good news — we deliver to ${z} for ${dollars(DELIVERY_CENTS)}.`);
      setDeliveryChoice('delivery');
    } else {
      cartNotice(`We can't reach ${z} on our own truck — pickup and the Friday flea-market drop are both free.`);
    }
    syncDelivery();
    renderTotals();
  }

  // Display arithmetic ONLY. Square computes the real tax per line and its
  // number is what the buyer pays (spec §8.6); this is here so nobody is
  // surprised by the total on the next screen.
  let goodsCents = 0;
  function renderTotals() {
    const c = mountCart();
    const choice = deliveryChoice();
    const del = choice === 'delivery' ? DELIVERY_CENTS : 0;
    const tax = Math.round((goodsCents + del) * TAX_PCT) / 100;
    const taxCents = Math.round(tax);
    c.receipt.sub.textContent = dollars(goodsCents);
    c.receipt.delLine.hidden = del === 0;
    c.receipt.del.textContent = dollars(del);
    c.receipt.tax.textContent = dollars(taxCents);
    c.total.textContent = dollars(goodsCents + del + taxCents);
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
      c.deliv.set.hidden = true;
      goodsCents = 0;
      renderTotals();
      return;
    }
    c.deliv.set.hidden = false;
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
    c.sub.textContent = `${kept.length} box${kept.length === 1 ? '' : 'es'}`;
    goodsCents = cents;
    syncDelivery();
    renderTotals();
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
      const choice = deliveryChoice();
      const addr = choice === 'delivery' ? (c.deliv.addr.value || '').trim() : '';
      if (choice === 'delivery' && !addr) {
        cartNotice('Add the street address for the delivery.');
        c.deliv.addr.focus();
        resetCheckoutBtn();
        return;
      }
      let member = '';
      try { member = localStorage.getItem('nsl.member') || ''; } catch { /* ignore */ }
      const r = await fetch('/api/public/checkout', {
        method: 'POST', credentials: 'omit',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids, delivery: choice, zip: choice === 'delivery' ? storedZip() : null,
                               address: addr || null, memberNumber: member || null }),
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
      if (r.status === 400 && (j.field === 'zip' || j.field === 'address')) {
        // The server is the authority on the delivery radius; our copy of the
        // zip list can be stale if Rob just edited it.
        cartNotice(j.error || 'Check the delivery address.');
        if (j.field === 'zip') { setDeliveryChoice('pickup'); syncDelivery(); renderTotals(); c.deliv.zip.focus(); }
        else c.deliv.addr.focus();
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

/* delivery choice (spec §8.3) */
.cart-deliv { border: 1px solid #e3e3e3; border-radius: 6px; margin: 0; padding: 10px 12px 12px; }
.cart-deliv[hidden] { display: none; }
.cart-deliv legend { font-weight: 700; font-size: 14px; padding: 0 4px; }
.deliv-opt { display: flex; align-items: center; gap: 10px; padding: 9px 2px; min-height: 44px; cursor: pointer; }
.deliv-opt .deliv-label { flex: 1; font-size: 15px; }
.deliv-opt .deliv-price { font-weight: 700; white-space: nowrap; }
.deliv-opt.is-off { opacity: .55; cursor: default; }
.deliv-to { color: #666; font-weight: 400; }
.deliv-zip, .deliv-addr { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 2px 2px 8px 28px; }
.deliv-zip[hidden], .deliv-addr[hidden] { display: none; }
.deliv-zip label, .deliv-addr label { font-size: 13px; color: #555; }
.deliv-zip input { width: 7em; }
.deliv-addr input { flex: 1; min-width: 12em; }
.deliv-zip input, .deliv-addr input { min-height: 40px; padding: 6px 8px; border: 1px solid #ccc; border-radius: 4px; font-size: 16px; }

/* receipt block */
.cart-receipt { margin: 10px 0 0; font-size: 14px; }
.cart-receipt div { display: flex; justify-content: space-between; padding: 3px 0; }
.cart-receipt div[hidden] { display: none; }
.cart-receipt dt { color: #555; }
.cart-receipt dd { margin: 0; }
.cart-empty { color: #555; font-size: 15px; padding: 24px 0; text-align: center; line-height: 1.6; }
```

- [ ] **Step 4: Manual check (Playwright MCP or a browser). Serve the static files locally — do NOT run the API against production Square (see Task 14 Step 0 rule 7); the cart drawer, badge and prune paths all work against the live read-only `/api/public/pallets`. Then commit**

- Add two boxes → header badge "2" (desktop) / bottom bar "2 boxes · $410" at 400px width.
- Open drawer → both rows, total, Checkout enabled; remove one → row gone, total updates, card control flips back.
- In admin, archive a box that is in the cart → reopen drawer → notice "BOX #N was just sold — removed", row gone.
- Checkout → the Square hosted page loads (minting a link charges nothing); browser Back → button reads "Checkout with Square →" again (pageshow).
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
> **The cron is a convenience, not the dependable path.** GitHub scheduled
> workflows are best-effort (runs are delayed or dropped under load) and GitHub
> **disables a schedule automatically after 60 days without repository
> activity**. A quiet month on this repo silently stops the sweep. The staff
> **Reconcile** button on `staff/sales.html` is the reliable trigger and must
> stay; treat the cron as belt-and-braces.

Rollout (Task 14) sets `RECONCILE_CRON_KEY` on the SWA and `NSL_RECONCILE_KEY` in GitHub (generate with `[Convert]::ToHexString((1..32 | % { Get-Random -Max 256 }) -as [byte[]])`).

---

### Task 14: Production end-to-end (real money, controlled), PR, rollout, docs

**Files:**
- Modify: `SQUARE-INTEGRATION.md` (architecture + invariants), `docs/superpowers/specs/2026-09-13-cart-checkout-design.md` (status line)

> **There is no Square sandbox for this account** — Jeff's call, 2026-09-15: "We don't need sandbox for Square use prod." The tenant has only `SQUARE_PROD_ACCESS_TOKEN` / `SQUARE_PROD_LOCATION_ID`, and standing up a sandbox app is work nobody wants for a storefront this size. Every scenario below therefore runs against the **live Square account with real cards and real money**, which changes the rules rather than the coverage. Read Step 0 before touching anything.

- [ ] **Step 0: Ground rules for testing with real money**

These are not optional. Every one of them exists because the sandbox safety net is gone.

1. **Dedicated test boxes, never real stock.** Create two draft boxes named `ZZ TEST — DO NOT BUY` priced **$18.00** and **$25.00**, plus a third at **$18.00** for the delivery scenarios. Cheap enough that a mistake costs coffee money, large enough that the 7.25% arithmetic is checkable. Set them `live` only for the minutes a scenario needs them and **archive them the moment it is done**. Never use a box Rob is actually trying to sell.
2. **Tell Rob and Norman first.** Test boxes flash SOLD on the live site and test payments land in the Square dashboard they read. Warn them in the thread, and say when you are finished. An unexplained $46 sale is a phone call.
3. **Refund every test payment immediately** and record the `square_payment_id` of each. Note: Square may not return the processing fee (2.9% + 30¢) on a refund — confirm on the first one and expect each paid scenario to cost roughly a dollar in fees, not the ticket price.
4. **Almost nothing here costs money.** Adding to the cart, the drawer, the zip check, the receipt arithmetic and even minting the Square link are all free — a payment link is not a charge. Only the explicit "pay it" steps below move money, and they are marked **💵**. Run every free check first; if any of them is wrong, you never reach the paid ones.
5. **Never delete the production webhook subscription.** The plan's old sandbox step did this to simulate a missed webhook; in production that drops real sales for the duration. Scenario 4 below simulates it in the database instead.
6. **The kill switch is the abort button.** `SQUARE_CHECKOUT_ENABLED=false` on the SWA takes the cart off the site in one command. At the first sign of anything wrong, flip it, then investigate.
7. **Do not run the API locally against production Square.** A local host holding the prod token and the prod connection string can mint real links and mark real boxes sold, and a second instance consuming the same webhooks is a second thing that can get it wrong. `api/local.settings.json` is gitignored (verified), but the hazard is the process, not the file. Test on the deployed site.
8. **Pick a quiet window.** The floor currently carries a single-digit number of live boxes and the site sees little traffic, so a short window with the cart live is a small real risk — but it is not zero, and it is smallest in the early morning.

- [ ] **Step 1: Deploy first, then test the deployed site**

Merge order is the reverse of the sandbox plan: apply `db/cart-checkout.sql`, merge, let the deploy finish, then run the scenarios against `https://northstateliquidators.com`. There is no local run and no tunnel — the production webhook subscription already points at the live site, which is exactly the path being tested.

- [ ] **Step 2: Free scenarios — no money moves (record each in the PR body)**

1. **Receipt arithmetic, pickup.** Add the $18.00 and $25.00 test boxes → drawer reads Subtotal $43.00 / Sales tax (7.25%) $3.12 / **Total $46.12**. (Per line: 18.00 × .0725 = 1.305 → 1.31; 25.00 × .0725 = 1.8125 → 1.81. Assert the **sum**, not a hand-typed cent — Square rounds per line.)
2. **Delivery arithmetic — the payload check.** Clear `localStorage['nsl.zip']`. One $18.00 box → **Delivered to you** is disabled with the zip prompt. Enter `28202` → "We can't reach 28202…", still disabled. Enter `27587` → enables, label reads "(to 27587)", the address field appears. Choose it → Subtotal $18.00 / Delivery $10.00 / Sales tax (7.25%) **$2.04** / **Total $30.04**. The tax is on **$28.00, not $18.00**. (The figure is $2.04, not $2.03: Square applies a LINE_ITEM-scope tax and rounds **per entity** — box `1800 x 7.25% = 130.5 -> 131`, delivery `1000 x 7.25% = 72.5 -> 73`, sum **204**. Rounding the $28.00 in one go gives 203, which is what the drawer used to show and is a cent under what Square charges. Both lines here land on an exact half-cent, so this cart is itself a test of per-line rounding.) **If it reads $1.31, the delivery fee is not being taxed and the Square payload is wrong — stop here.** This is the whole reason the design uses `service_charges[]` instead of `checkout_options.shipping_fee`.
3. **Flea market.** Same box, Friday flea-market option → no Delivery line, total $18.00 + $1.31 tax = $19.31.
4. **The Square page agrees.** Click Checkout on scenario 1 (mints a link, charges nothing) → the hosted page shows **$46.12**, one **"NC & Wake County Sales Tax"** line, no shipping line. If it reads "NC sales tax (7.25%)" the catalog id is unset or wrong and the code took the ad-hoc fallback — the amount is still right, but Rob's reporting will show two different tax lines for the same tax. Close the tab without paying. Repeat for the delivery cart → a separate "Local delivery (within 20 miles)" line **and** a tax line.
5. **Zip list is server-authoritative.** Drawer open with `27587` qualifying → `UPDATE dbo.delivery_zips SET active = 0 WHERE zip = '27587'` → click Checkout without reloading → `400 { field: "zip" }`, drawer flips to pickup with the reason, nothing minted at Square. Re-activate.
6. **Delivery choice changes the link.** Pickup cart → Checkout (link 1, don't pay) → switch to delivery with a qualifying zip → Checkout → a **different** `square_order_id` with `delivery_cents = 1000`. Click Checkout twice on the same choice → identical link, no second order row.
7. **Price change cancels.** Box D in a cart, Checkout (don't pay) → PATCH `listPrice` → its `checkout_orders` row is `canceled`; reopen the drawer → box still there at the new price; Checkout → a new `square_order_id`.
8. **Kill switch.** `SQUARE_CHECKOUT_ENABLED=false` → no cart controls, header button and bottom bar hidden, `localStorage['nsl.cart']` untouched, `POST /api/public/checkout` → 503 and the drawer shows the paused notice. Turn it back on.
9. **Phone width.** Playwright at 400×800: bottom bar visible with a non-empty cart, drawer full-width, rows readable, Checkout button ≥ 48px tall, no horizontal scroll.

- [ ] **Step 3: 💵 Paid scenarios — real charges, refunded immediately**

10. **💵 Two-box cart pays, pickup, taxed.** Pay scenario 1's link with a real card. Then: both boxes `sold`; `SELECT status, subtotal_cents, tax_cents, delivery_cents, total_cents, delivery_method FROM dbo.checkout_orders WHERE square_order_id=…` = `paid, 4300, 312, 0, 4612, pickup`, and **subtotal + tax + delivery = total**; one `payments` row with `amount_cents = 4612`, `status='COMPLETED'`, `manifest_id IS NULL`; two `checkout_order_boxes` rows, `outcome='sold'`, whose `tax_cents` **sum** to the order's 312; **no "amount != order total" warning in the log**; thanks page lists both numbers and the cart is empty. Staff sales page shows **$43.00** in the goods column with $3.12 in Tax/Del — not $46.12. **Refund it**, then re-list the boxes as draft.
11. **💵 Delivery order pays.** Pay scenario 2's link → `subtotal_cents 1800, delivery_cents 1000, tax_cents 204, total_cents 3004, delivery_method 'delivery', delivery_zip '27587'`, `delivery_address` as typed. **Refund it.** **While you have the receipt open, settle the one thing no test can:** Square's half-cent tie-break. We assume half-up (131 and 73). If the receipt shows `tax 202` the tie-break is half-to-even and the drawer's `Math.round` must change to match — it is the only place in this build where a Square behaviour is assumed rather than observed.
12. **💵 Overlap, first pays.** Two tabs: cart A `{test1, test2}`, cart B `{test2, test3}`, both click Checkout. Pay A → order B `status='canceled'` and `link_deleted_at` set (or NULL plus a warning if Square answered without `cancelled_order_id` — then run Reconcile and confirm it stamps). Tab B's page refuses to complete. **Refund A.**
13. **💵 Partial refund path, with tax.** Cart C `{test4, test5}` → reach the Square page, don't pay → mark test4 SOLD in admin (its link is canceled in the DB) → set order C back to `open` with SQL to simulate the documented delete race → pay it. Expect: test5 `sold`, test4 `outcome='unavailable'`, `payments.status='PARTIAL_REFUND_FLAGGED'`, and **`refund_due_cents` = test4's `amount_cents` + its `tax_cents`** — check by hand against `SELECT amount_cents, tax_cents FROM dbo.checkout_order_boxes WHERE manifest_id = <test4>`. The staff attention row shows the tax-inclusive amount; click Refund → Square refunds exactly that; the `refund.updated` webhook sets `refunded_cents`, `status='PARTIAL_REFUNDED'`, `needs_refund=0`. **Refund the remainder.**
14. **Missed webhook — simulated in the database, not at Square.** Take the paid order from scenario 10 **before** refunding it: `DELETE FROM dbo.payments WHERE square_payment_id = '<id>'`, reset the boxes to `live` via `sp_SetPublishState`, and `UPDATE dbo.checkout_orders SET status='open', closed_at=NULL WHERE square_order_id='<id>'`. Now `POST /api/square-reconcile` → `healed: 1`, boxes sold again, a `payments` row restored with `status='COMPLETED'`. This exercises the identical `FulfillOrderAsync` path a missed webhook would take, without touching the live subscription.
15. **Floor sale ignored.** Already proven in production by the Phase 0 hotfix (two RETAIL cash sales stopped being flagged, and `dbo.payments` has had no `UNMATCHED` row since). Confirm rather than re-test: after Rob rings any cash sale, `SELECT COUNT(*) FROM dbo.payments WHERE status='UNMATCHED'` is still 0.

- [ ] **Step 4: Clean up**

Archive all test boxes. Confirm `SELECT COUNT(*) FROM dbo.payments WHERE needs_refund = 1` is 0 and every test payment shows refunded in the Square dashboard. Post the payment ids and refund confirmations in the thread so Rob's books match.

- [ ] **Step 5: Update the docs**

In `SQUARE-INTEGRATION.md` replace the "One link per box, links are single-use." bullet and the "New pieces" table rows for `CheckoutFunction` / `manifests columns` with a short "Cart model (2026-09)" paragraph: one link per checkout attempt in `checkout_orders` (+ `checkout_order_boxes`, per-box `outcome`), fulfilment transactional in `CheckoutFulfillment`, competing links canceled DB-first, partial refunds via `refund_due_cents`, Reconcile ages links out at 7 days and runs from the GitHub cron. Add: orders carry 7.25% NC sales tax as an ADDITIVE LINE_ITEM-scope tax and, for delivery orders, a $10 taxed service charge; the sales dashboard reports goods only. Add a line recording that **this account has no sandbox — all verification is done against production with disposable test boxes**, and point at Step 0's ground rules. Also record that the reconcile **cron is best-effort**: GitHub scheduled workflows can be delayed and are **disabled automatically after 60 days without repository activity**, so the staff Reconcile button is the dependable path and the cron is a convenience. Change the spec's **Status** line to `APPROVED 2026-09-14 (v3) — implemented in PR #<n>`.
```powershell
git add SQUARE-INTEGRATION.md docs/superpowers/specs/2026-09-13-cart-checkout-design.md
git commit -m "docs: cart model in SQUARE-INTEGRATION.md; spec marked approved"
```

- [ ] **Step 6: PR**

```powershell
git push -u origin feature/cart-checkout
gh pr create --title "Cart + combined checkout (multi-box Square orders)" --body-file - <<'EOF'
## Why
Norm: "we need an add to cart button so people can buy multiple items." Rob (9/14): sales tax, a three-way delivery choice, and an address on the join form. Spec: docs/superpowers/specs/2026-09-13-cart-checkout-design.md (reviewed by 4 agents + Square docs check; tax/delivery in §8).

## What
- DB: checkout_orders / checkout_order_boxes / payment_refunds / delivery_zips (additive; db/cart-checkout.sql applied to prod before merge and re-run after)
- API: POST /api/public/checkout {ids, delivery, zip, address}; one Square `order` link with N ad-hoc lines plus a 7.25% ADDITIVE LINE_ITEM-scope tax and, for delivery orders, a $10 taxed service charge (coupons/tips off, no shipping_fee, no shipping-address collection); transactional CheckoutFulfillment with per-box outcome + tax-inclusive partial refund flag; DB-first cancel of competing links on sale / price change / state change / invoice / fake-sale / delete; Reconcile set-based + confirmed deletes + 7-day age-out + cron tick route; partial refunds; sales summary via checkout_order_boxes, goods-only revenue
- Site: Add-to-cart control (replaces Buy now), header badge, phone bottom bar, drawer with fresh re-validation + delivery radio group + live receipt, thanks page for N boxes
- Tests: api.Tests (SquarePayloads incl. tax/service-charge shape, Availability)
- **No card surcharge** — Square does not support it on payment links and no network allows surcharging debit; spec §7.1 has the full reasoning and the cash-discount alternative.

## Production verification run
No Square sandbox exists for this account, so every scenario was run against the live account with disposable $18/$25 test boxes and every payment refunded. (paste results from Task 14, scenarios 1–15; note which were free and which moved money)

## Needs Rob before the live test
- NCDOR filing frequency (spec §8.9 Q1) — **not blocking.** The live Square account already carries the 7.25% "NC & Wake County Sales Tax" object, so NSL is registered; all that is left is how often they file, which is a question for their accountant.
- Confirm the delivery zip list; 12 borderline zips are seeded inactive (§8.9 Q2)
- Confirm the flea-market day/venue/window for the buyer-facing copy (§8.9 Q3)

## Rollout
1. SWA settings: SQUARE_PUBLIC_BASE_URL, SQUARE_SUPPORT_EMAIL, SQUARE_TAX_CATALOG_ID=NJMJVQ3TQDEYCNQJJ5MGTCXT, RECONCILE_CRON_KEY (+ GitHub secret NSL_RECONCILE_KEY), per-env webhook keys
2. Merge → deploy → re-run db/cart-checkout.sql; seed/activate delivery_zips from Rob's answer
3. $1 live test: two $0.50 boxes (confirm tax lands), one delivery order, refund one box and confirm the refund includes its tax, confirm fee rate
4. Days later: db/cart-checkout-drop.sql

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01SBzv2wxVMJKqKEsddmWY2Q
EOF
gh pr checks --watch
```
Expected: `Build and Deploy pass`. Merge is Jeff's call.

- [ ] **Step 7: Production rollout (after merge)**

1. Set the SWA application settings listed in the PR body (Azure Portal → `stapp-nsl-website` → Environment variables), **including `SQUARE_TAX_CATALOG_ID=NJMJVQ3TQDEYCNQJJ5MGTCXT`** — without it the code silently falls back to the ad-hoc tax and Rob's reporting ends up with two differently-named tax lines for the same tax. Production webhook subscription must include `payment.updated`, `payment.created`, `refund.updated`, `refund.created`.
2. Re-run `db/cart-checkout.sql` (Task 1 Step 3 command).
3. **Confirm the Square location's own tax settings** before the first live order — if the location auto-applies a tax to orders, our reference to the same catalog object must not end up stacked. Web and floor must both land on 7.25% (spec §8.9 Q5). NCDOR **registration is already answered**: the live account carries the 7.25% tax object, so NSL is registered — only the filing frequency is outstanding, and that is their accountant's call, not a blocker for this rollout.
4. Live test: two boxes priced $0.50 each (or one $1 box) → pay with a real card → both flip SOLD, `tax_cents` non-zero and equal to Square's `total_tax_money` → refund one from admin → `refunded_cents` matches the tax-inclusive `refund_due_cents` → confirm the 2.9% + 30¢ fee on the Square Dashboard. Run one $0.50 **delivery** order too and confirm the total is `0.50 + 10.00 + tax on 10.50`.
5. Run the "Square reconcile" workflow manually once → 200 with counts.
6. Tell Rob: (a) web orders stay OPEN in the Square Dashboard's Order Manager forever (Square can't close payment-link fulfillments via API) — that is normal; (b) his NC filing figure is tax collected minus tax refunded from `checkout_orders.tax_cents` and refunded boxes' `tax_cents`, **not** Square's partial-refund report, which does not break tax out (spec §8.8); (c) the delivery zip list is a table he can change — send him the current rows.

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
Before running, `grep -rn "checkout_link_id\|checkout_order_id\|checkout_url\|checkout_created_at" api db --include=*.cs --include=*.sql` must show **no `.cs` files at all** — that is the whole gate. `.sql` files (and docs/briefs) are expected to match: `db/square-payments.sql` (superseded), `db/cart-checkout.sql` (guarded by `COL_LENGTH`), `db/wishlist4.sql` (a comment) and this file. Do not halt on those. `v_pallets` does not select these columns (verified 2026-09-13).

- [ ] **Step 2: Apply and commit**

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
Invoke-Sqlcmd -ServerInstance sql-nsl-prod-nc5h2y.database.windows.net -Database sqldb-nsl-prod -AccessToken $tok -InputFile db/cart-checkout-drop.sql -Verbose
git add db/cart-checkout-drop.sql
git commit -m "db: drop legacy manifests.checkout_* columns (cart phase 2)"
```

---

### Task 16: Member address shortcut — capture zip/address on signup (spec §8.5)

> **Append-only task, added 2026-09-14 with spec v3.** The member street-address work it waits on has already shipped (commit `355a6f5`, on `main`): `dbo.members`, the join modal and `POST /api/public/register` all carry `address1` / `address2` today. Touch only the join modal's **success handler** here — not the form markup, `sp_RegisterMember` or `MembersFunction`.

**Files:**
- Modify: `js/site.js` — the join modal's submit success path (around the `rememberMember(n)` call)

**Interfaces:**
- Consumes: the join form's zip field (`#join-zip`, exists today) and the street-address field the other agent is adding.
- Produces: `localStorage['nsl.zip']` (5-digit string) and `localStorage['nsl.addr']` (one street line), read by Task 10's `storedZip()` / `storedAddr()` and by the drawer's `syncDelivery()`.

**Why this shape.** Rob asked for "if they are a member, it will automatically know." The public site has **no login**: signup posts to `POST /api/public/register` and the browser keeps the member number in `localStorage['nsl.member']`. There is no public endpoint that reads a member row back, and **there must not be** — member numbers are sequential (`2600001`, `2600002`, …), so a public `GET /api/public/member/{number}` would let anyone walk the range and harvest home addresses. So the shortcut is device-local, exactly like the member number itself. Say so to Rob in plain words: *this works on the phone they signed up on; on a new device they type their zip once.* The real fix is member login — `RESELLER-PROGRAM-DESIGN.md` §1, which Rob has separately asked for — and once it exists `POST /api/public/checkout` can read the address server-side and the drawer needs no address form for members at all.

- [ ] **Step 1: Store the zip and address alongside the member number**

In the join modal's submit handler, where the successful response currently calls `rememberMember(n)`, add:

```javascript
      // Device-local delivery shortcut (spec §8.5). The member number already
      // lives here; the zip is what the cart drawer needs to answer "do you
      // qualify for $10 delivery" without asking again. NOT a substitute for
      // member login — see RESELLER-PROGRAM-DESIGN.md §1.
      try {
        const z = (body.zip || '').trim();
        if (/^\d{5}$/.test(z)) localStorage.setItem('nsl.zip', z);
        const street = (body.address1 || '').trim();   // shipped field name (see MembersFunction RegisterRequest / #join-address1)
        if (street) localStorage.setItem('nsl.addr', [street, body.city, body.state].filter(Boolean).join(', '));
      } catch { /* private mode — the drawer just asks for the zip */ }
```

`address1` / `address2` are the names the join form posts (`#join-address1`, `#join-address2` in `js/site.js`) and the names `RegisterRequest` in `api/Functions/MembersFunction.cs` binds — use them exactly. If a shopper left the street line blank the drawer simply degrades to "qualifies, type your street address", which is still the whole of the eligibility answer.

- [ ] **Step 2: Check**

With the SWA CLI running: sign up with zip `27587` → `localStorage['nsl.zip'] === '27587'` → open the cart drawer → the **Delivered to you** radio is already enabled, labelled "(to 27587)", and **no zip input is shown**. Sign up with `28202` in a fresh profile → the radio stays disabled with the "can't reach" explanation. Private-browsing window → no throw, drawer falls back to asking for the zip.

- [ ] **Step 3: Commit**

```powershell
git add js/site.js
git commit -m "feat(cart): remember a new member's zip for the delivery check (device-local)"
```
