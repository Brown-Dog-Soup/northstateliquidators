# Cart + Combined Checkout — Design

**Status:** REVISED DRAFT v3 (2026-09-14). v2 (2026-09-13) came out of a
four-track review (backend audit, spec-vs-code, front-end, Square API
verification) and a production data check; nothing in it has been built yet.
v3 folds in Rob's 2026-09-14 checkout requests — **7.25% NC sales tax**, a
**three-way delivery choice**, and the member street address — and records the
decision **not** to add a card surcharge. All of that lives in **§8**; §1, §2,
§4, §5 and §7 carry the consequences.
**CLIENT DECISIONS 2026-09-14 (Rob, on the "Laundry" thread):** the card
surcharge is **dropped for good** — "Good point on the CC fee. Ignore. Fine the
way it is." He declined the cash-discount alternative too, so **pricing is
unchanged and no fee logic of any kind is built** (§7.1 stands as the record of
why). Member password login is **deferred** — "Good with not having a password
for now... we will trend in that direction as we begin to have member tiers and
discounts" — so §8's member-recognition stays device-local for now and a login
is a prerequisite only for the later tier/discount work. Sales tax and the
three-way delivery choice are confirmed as specced.
**Requested by:** Norm — "we need an add to cart button so people can buy
multiple items" — multiple boxes of any size (Mega Box, Mini Pallet, Full
Pallet, Individual) in one purchase.
**Builds on:** `SQUARE-INTEGRATION.md` (live since 2026-08-28). The June plan
(`docs/SQUARE-CHECKOUT-BUILD.md`) had already locked "full cart + combined
checkout"; the August build deliberately shipped the simpler one-box-per-link
model first. This restores the cart on top of what shipped.

---

## 0. Findings in the LIVE flow (fix first / fold in)

The review found defects in the shipped one-box flow. Two are confirmed in
production data (queried 2026-09-13):

| # | Finding | Evidence | Disposition |
|---|---|---|---|
| L1 **HIGH** | **Every floor (POS) sale is logged as `UNMATCHED` + `needs_refund=1`.** The `payment.updated` subscription fires for the whole Square account; the webhook flags any payment it can't match. | Prod `dbo.payments`: two $250 **CASH / RETAIL** payments on 2026-09-11 flagged for refund. Admin's payments list shows them as "needs attention". | **Phase 0 hotfix** (own PR, ships before the cart): only treat `application_details.square_product IN ('ECOMMERCE_API','INVOICES')` as ours; everything else → 200, not inserted. One-off SQL to clear the two rows. |
| L2 **HIGH** | Fixed idempotency key `nsl-{id}-link-v1`: once a box's link is retired (invoice, fake-sale undo, pulled and re-listed), the next Buy replays Square's cached dead link or 400s. | `SquareFunction.cs:80` | Replaced by the cart design (per-attempt key, §4). |
| L3 MED | Stored link reused after a price change; buyers pay the old price. | `SquareFunction.cs:71`; PATCH never touches links | §4 price-change rule + reconcile amount check. |
| L4 MED | Webhook not transactional: `payments` INSERT commits first; a later failure leaves the box live and retries return `duplicate`. | `SquareFunction.cs:153-194` | §4 single transaction. |
| L5 MED | `InvoiceBox` deletes the public link before any DB write; a Square failure afterwards leaves a live box serving a dead link. | `SquareFunction.cs:247-261` | §4 DB-first cancel pattern. |
| L6 MED | Reconcile `TOP 50 … ORDER BY created ASC` starves: 50 never-resolving rows block newer paid orders forever. | `SquareFunction.cs:550-555` | §4 reconcile redesign. |
| L7 MED | `SoldToInventory` ignores an outstanding invoice: box fake-sold + cloned while the invoice stays payable; the real payment then gets refund-flagged. | `sp_SoldToInventory`, `PalletsFunction.cs:501-528` | **Phase 0 hotfix**: 409 when `invoice_id IS NOT NULL`. |
| L8 MED | Refund key `nsl-refund-{paymentId}` + "any COMPLETED refund ⇒ REFUNDED": breaks on partial or failed refunds. Also Square caps refund keys at **45 chars**. | `SquareService.cs:296`, `SquareFunction.cs:121-135` | §4 refund model. |
| L9 MED | A box pulled live→draft/archived via PATCH stays payable on Square until someone clicks Reconcile (no timer exists; design said hourly). | `PalletsFunction.cs:278-296` | §4 cancel-on-state-change + reconcile trigger. |
| L10 LOW | `DeletePallet` hard-deletes a box with an open link/invoice. | `PalletsFunction.cs:451-481` | §4. |
| L11 LOW | Unguarded `GetProperty` on webhook JSON → 500 → 11 Square retries. | `SquareFunction.cs:123,140,145` | **Phase 0 hotfix**: `TryGetProperty`, 200 `ignored`. |
| L12 LOW | Redirect URL hard-coded to prod host; one webhook key for both environments. | `SquareFunction.cs:75`, `SquareService.cs:48-52` | §4 config. |

Square-doc facts that change the design (all verified against
developer.squareup.com on 2026-09-13):
- **`DeletePaymentLink` is not a reliable fence.** Square staff-acknowledged
  reports of deletes returning 200 with no `cancelled_order_id` and the link
  staying payable for hours. ⇒ the DB, not the delete call, is the source of
  truth for "canceled"; the partial-refund path is the real safety net.
- **Coupons are ON by default** on hosted checkout; **tips** are additive on
  top of `amount_money`. ⇒ `enable_coupon:false`, `allow_tipping:false`, and
  never assert `amount_money == line-item sum`; compare to `order.total_money`.
- Refund `idempotency_key` max **45 chars**; max 20 refunds per payment.
- `payment.updated` fires multiple times per payment (authorize → complete →
  later `refunded_money` updates). Gate on COMPLETED + dedupe (already done).
- Payment-link orders have no `customer_id`; buyer email is on the Payment.
- Line-item `note` is visible to the buyer on the receipt.
- Redirect params in production are `orderId` and `transactionId` (both = the
  order id). Sandbox appends nothing. Cosmetic only.
- `order.state` stays OPEN forever after payment (fulfillment can't be closed
  via API). Tell Rob so web orders in the Dashboard's Order Manager don't look
  stuck.

---

## 1. What changes and what doesn't

**Unchanged**
- Square Checkout API hosted page (no on-site card form, no PCI surface).
- Kill switch `SQUARE_CHECKOUT_ENABLED` gates every Add-to-cart control.
- "Paid" is decided by the `payment.updated` webhook (COMPLETED), never by the
  redirect. Reconcile still heals missed webhooks.
- SOLD is only ever set through `sp_SetPublishState`.
- One-of-a-kind inventory: quantities are always 1. No catalog sync.
- NSL contacts the buyer from the Square receipt to arrange handover; nothing
  is shipped by carrier.
- `checkout_options.ask_for_shipping_address` stays **off**. Turning it on
  would buy us one address field and cost us the three-way choice (§8.3).
- Wholesale invoice flow keeps its behaviour (and gains the same correlation
  table).

**Changed**
- One Square order covers N boxes. "One link per box, stored on the box"
  becomes "one link per checkout attempt, in its own table, linked to N
  boxes". A box may appear in several open links at once.
- The webhook fulfils every box on the order inside one transaction and
  records a per-box outcome (`sold` | `unavailable`); unavailable boxes are
  flagged for a **partial** refund.
- When boxes sell, change price, or leave `live`, every other open link
  containing them is canceled in the DB first, then deleted at Square
  best-effort, with Reconcile sweeping stragglers.
- "Buy now" goes away; one control per card: **Add to cart → ✓ In cart**.
- **Every order is taxed at 7.25%** (4.75% NC + 2.00% Wake County + 0.50%
  transit), supplied by us as an ADDITIVE, `LINE_ITEM`-scope tax on the Square
  order and applied to every box line. §8.1.
- **The buyer picks how they get the boxes** before the link is minted:
  warehouse pickup (free), local delivery ($10, zip allow-list), or the free
  Friday drop at the Raleigh Flea Market. The $10 rides as an
  `order.service_charges[]` entry that carries the same tax — NC taxes a
  delivery charge as part of the sale. §8.2–§8.4.
- `checkout_orders` grows `subtotal_cents`, `tax_cents`, `delivery_cents`,
  `delivery_method`, `delivery_zip`, `delivery_address`, `member_number`;
  `checkout_order_boxes` grows `tax_cents` so a per-box refund returns that
  box's tax with it. New table `dbo.delivery_zips`. §2, §8.6.
- The sales dashboard reports **goods only** as revenue: tax and the delivery
  fee are shown in their own columns and never enter margin. §8.6.

---

## 2. Data model — two scripts

### `db/cart-checkout.sql` (additive, applied BEFORE the deploy and re-run after)

```sql
CREATE TABLE dbo.checkout_orders (
    square_order_id  VARCHAR(64)   NOT NULL PRIMARY KEY,
    kind             VARCHAR(16)   NOT NULL,            -- 'link' | 'invoice'
    square_link_id   VARCHAR(64)   NULL,                -- links only
    url              NVARCHAR(500) NULL,
    status           VARCHAR(16)   NOT NULL DEFAULT 'open',  -- open | paid | canceled
    subtotal_cents   BIGINT        NOT NULL DEFAULT 0,  -- boxes only (what we actually sell)
    tax_cents        BIGINT        NOT NULL DEFAULT 0,  -- Square's total_tax_money
    delivery_cents   BIGINT        NOT NULL DEFAULT 0,  -- Square's total_service_charge_money
    total_cents      BIGINT        NOT NULL,            -- Square's total_money = subtotal + tax + delivery
    delivery_method  VARCHAR(16)   NOT NULL DEFAULT 'pickup',  -- pickup | delivery | flea
    delivery_zip     VARCHAR(10)   NULL,                -- only for delivery_method='delivery'
    delivery_address NVARCHAR(300) NULL,                -- one free-text line + city/state, for the van
    member_number    CHAR(7)       NULL,                -- if the browser had one (nsl.member)
    created_at       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    closed_at        DATETIME2     NULL,
    link_deleted_at  DATETIME2     NULL                 -- Square confirmed (cancelled_order_id present)
);
CREATE INDEX IX_co_status ON dbo.checkout_orders (status, kind, created_at);

CREATE TABLE dbo.checkout_order_boxes (
    square_order_id  VARCHAR(64)      NOT NULL REFERENCES dbo.checkout_orders,
    manifest_id      UNIQUEIDENTIFIER NOT NULL,          -- no FK: DeletePallet cleans up explicitly (§4)
    amount_cents     BIGINT           NOT NULL,          -- price at link time (ex-tax)
    tax_cents        BIGINT           NOT NULL DEFAULT 0,-- this line's total_tax_money, straight from Square
    outcome          VARCHAR(16)      NULL,              -- NULL | 'sold' | 'unavailable'
    fulfilled_at     DATETIME2        NULL,
    PRIMARY KEY (square_order_id, manifest_id)
);
CREATE INDEX IX_cob_manifest ON dbo.checkout_order_boxes (manifest_id);

-- Rob's delivery radius, as a list he can edit without a deploy (§8.4).
CREATE TABLE dbo.delivery_zips (
    zip        VARCHAR(10)   NOT NULL PRIMARY KEY,
    fee_cents  INT           NOT NULL DEFAULT 1000,
    active     BIT           NOT NULL DEFAULT 1,
    note       NVARCHAR(200) NULL,
    updated_at DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.payments ADD
    refund_due_cents BIGINT NULL,      -- what admin should refund (partial case), TAX INCLUDED
    refunded_cents   BIGINT NOT NULL DEFAULT 0;   -- cumulative, from refund.updated

-- Invariant, asserted on insert: total_cents = subtotal_cents + tax_cents +
-- delivery_cents, and subtotal_cents = SUM(checkout_order_boxes.amount_cents).
-- Every one of those numbers is read back out of Square's create response, not
-- computed by us, so our arithmetic can never disagree with what the buyer pays.

-- Idempotent copy of existing per-box links/invoices (amount = current ask; the
-- old code never stored the link amount).
INSERT INTO dbo.checkout_orders (...) SELECT ... FROM dbo.manifests m
WHERE m.checkout_order_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.checkout_orders WHERE square_order_id = m.checkout_order_id);
-- kind = CASE WHEN m.invoice_id IS NOT NULL THEN 'invoice' ELSE 'link' END
```

### `db/cart-checkout-drop.sql` (days later, once no old code can run)
Drops `checkout_link_id`, `checkout_order_id`, `checkout_url`,
`checkout_created_at` from `manifests`. `invoice_id` / `invoice_url` stay
(box-level "reserved" state, read by admin). Mark `db/square-payments.sql`
superseded in its header so a re-apply can't resurrect the columns.

Why no FK from `checkout_order_boxes` to `manifests`: `DeletePallet` hard
deletes; a FK would 500 it, and `ON DELETE CASCADE` would silently leave a
payable Square link for a box that no longer exists. Instead `DeletePallet`
cancels the box's open link orders inside its existing transaction (§4).

---

## 3. Browser cart (`js/site.js`, `css/site.css`, `thanks.html`)

- **No per-page markup.** `initPage()` injects the header cart button into
  `.site-nav .actions` on all three public pages and lazily mounts the drawer,
  the same way the join modal is mounted today. thanks.html is standalone and
  gets two inline lines.
- **Storage:** `localStorage["nsl.cart"]` = JSON array of `manifest_id`
  strings (Set semantics, cap 20 with a notice). try/catch with an in-memory
  fallback (private mode). Only ids — names/prices always come from the feed.
- **Card control:** one button. `Add to cart` → `✓ In cart` (tap opens the
  drawer; removal only via × in the drawer, to avoid accidental removes).
  Rendered from cart state at build time in `boxCardHtml` (cards are
  re-rendered via innerHTML). In-cart cards get a yellow outline. The manifest
  modal's Buy button becomes the same control. **"Buy now" is removed** — two
  purchase paths with different cart semantics cause double orders, and the
  thanks page can't remove "just the paid ones".
- **Phones:** the header is not sticky on shop/faq under 900px, so a fixed
  bottom bar ("🛒 2 boxes · $410 — View cart →") appears whenever the cart is
  non-empty. Header button remains for desktop.
- **Drawer:** slide-in panel reusing the manifest modal's `.mf-*` styles. One
  row per box: `BOX #N · name · price`, ×. Footer pinned: the three-way
  **How do you want them?** radio group (§8.3), then a receipt block —
  Subtotal / Delivery / Sales tax (7.25%) / **Total** — all computed in cents
  in the browser for display only, and `Checkout with Square →`. Square's
  own numbers on the hosted page are the ones that count; the drawer's job is
  that nobody is surprised by the total on the next screen. On open: **fresh** fetch
  (new `refreshPublicPallets()`; the existing fetch is memoized for the page
  lifetime) → prune rule: keep iff row present AND `publish_state==='live'`
  AND `ask_price > 0` (fake-sold boxes stay in the feed for 48h with
  `is_sold=1`; archived boxes vanish — both prune). Removed boxes are named
  in an `aria-live` notice. **On fetch failure do not prune** — show "Couldn't
  check your cart right now" and keep the ids.
- **Checkout click:** disable button → `POST /api/public/checkout {ids}` →
  `location.href = url`. `409 {unavailable:[…]}` → prune those, re-render,
  notice. `409` without the list (old code during deploy) → fresh-fetch prune.
  `503` → "Online checkout is paused — call us". Never `location.reload()`.
- **Back from Square (bfcache):** `pageshow` with `persisted` → reset the
  button, re-validate the cart. **Multi-tab:** `storage` event on `nsl.cart`
  updates badge/drawer.
- **Kill switch off with a non-empty cart:** hide controls, keep storage.
- **Thanks page:** accepts `?boxes=12,14,15` and legacy `?box=N`
  (`/^\d+(,\d+)*$/`), lists all box numbers, clears `nsl.cart`, links back to
  `shop.html?view=all`. Ignores Square's `orderId`/`transactionId` params.
- **Accessibility:** a shared `openOverlay/closeOverlay` (focus capture and
  restore, Tab wrap, Escape, body scroll lock) used by the drawer; the
  manifest and join modals can adopt it later.

---

## 4. Server (`api/Functions/SquareFunction.cs`, `PalletsFunction.cs`, `api/Services/SquareService.cs`)

### `POST /api/public/checkout` — body `{ ids: Guid[], delivery?, zip?, address?, memberNumber? }`
1. Kill switch / configured (503).
2. De-dupe; 1..20 else 400.
3. Load all boxes in one query. Each must be `live`, not ghost, not archived,
   `invoice_id IS NULL`, priced > 0; else `409 { error, unavailable: [ids] }`.
4. **Validate the fulfilment choice** (§8.3). `delivery` ∈ `pickup` (default) |
   `delivery` | `flea`. For `delivery`: `zip` must be a 5-digit zip that is
   `active` in `dbo.delivery_zips`, else `400 { error, field:"zip" }` — the
   browser's copy of the list is a convenience, never the authority; `address`
   is required, trimmed, ≤300 chars. `zip`/`address` are ignored for the other
   two methods. This is the only new way this endpoint can 400.
5. **Reuse** an `open` `kind='link'` order with the identical
   `(manifest_id, amount_cents)` set **and the same `delivery_method` /
   `delivery_zip` / `delivery_address`** — no time window. Open + identical is
   current *by construction* because price changes and state changes cancel
   links (below). A different delivery choice mints a new link; the old one
   ages out or is canceled by the first competing sale.
6. Else `CreateCartPaymentLinkAsync(order)`: one ad-hoc line per box —
   `name="BOX #N — display name"` (≤512), `quantity="1"`,
   `base_price_money`, `applied_taxes:[{tax_uid:"NC-SALES-725"}]`, `note` =
   short human text naming the chosen handover ("Pickup in Wake Forest"),
   **not** the GUID (visible on receipt). Plus `order.taxes[]` with the single
   7.25% ADDITIVE `LINE_ITEM`-scope entry, and — only when `delivery='delivery'`
   — one `order.service_charges[]` entry ($10, `SUBTOTAL_PHASE`, `scope:ORDER`,
   `treatment_type:LINE_ITEM_TREATMENT`, `taxable:true`, `applied_taxes` →
   the same tax uid). Exact shape in §8.1/§8.2. Order `reference_id` = the box numbers while they fit Square's 40-character cap, otherwise `NSL <n> boxes` — a human-readable label for the Square dashboard, **not** a correlation key (the line-item `uid` is). `checkout_options`: `redirect_url =
   {baseUrl}/thanks.html?boxes=…` (base from config, not hard-coded),
   `enable_coupon:false`, `allow_tipping:false`, `ask_for_shipping_address`
   omitted (false — §8.3), **no `shipping_fee`** (§8.2),
   `merchant_support_email` set, one `custom_field`
   "Name & phone for pickup". `idempotency_key = nsl-cart-{Guid}`.
   `payment_note` = box numbers (≤500).
7. Insert `checkout_orders` from the create response's
   `related_resources.orders[0]`: `total_cents` = `total_money`, `tax_cents` =
   `total_tax_money`, `delivery_cents` = `total_service_charge_money`,
   `subtotal_cents` = `total_cents − tax_cents − delivery_cents`, plus
   `delivery_method` / `delivery_zip` / `delivery_address` / `member_number`.
   Child rows carry `amount_cents` **and** `tax_cents` read from that same
   response's `line_items[]`, matched on `uid` = manifest id — Square has
   already done the per-line rounding, so we never round twice (§8.6).
   Return `{ url }`.

`POST /api/public/checkout/{id}` stays as a wrapper (cart of one, redirects
with `?boxes=N`) for tabs loaded before the deploy.

### Fulfilment — one `FulfillOrderAsync(conn, tx, orderId, paymentId, amount, raw, source)`
Used by the webhook and Reconcile. Runs **entirely inside one SqlTransaction**
(pattern from `DeletePallet`; `sp_SetPublishState`'s inner TRAN nests fine):
1. `payments` INSERT (idempotency anchor). `inserted == 0` → done (`duplicate`).
2. Order row by `order_id`. None → **fallback**: `RetrieveOrder` and read the
   line-item `uid`s → each one **is** the box's `manifest_id` (that is what the
   create call stamps them with), so they map straight to boxes and rescue the
   deploy gap and a lost insert. `reference_id` is **not** a correlation key:
   Square caps it at 40 characters, so a full cart's box list does not survive
   there. Still none → `UNMATCHED` (only for our own products, see L1).
3. Boxes `ORDER BY manifest_id` (deadlock-safe). For each with
   `outcome IS NULL`:
   - **available** = `publish_state='live' AND archived_at IS NULL AND
     is_ghost=0 AND invoice_id IS NULL` (for `kind='link'`); for
     `kind='invoice'` = not sold. → `sp_SetPublishState 'sold'`, history row
     `changed_by='square'`, `outcome='sold'`.
   - otherwise `outcome='unavailable'` (someone else got it, or it was pulled).
   A box already `outcome='sold'` by this same order is skipped, never
   refund-flagged (re-entrancy after a partial failure).
4. `payments.manifest_id` = the box when exactly one;
   `refund_due_cents = SUM(amount_cents + tax_cents WHERE outcome='unavailable')`
   — **tax included**, because the buyer paid tax on a box they are not getting
   (§8.8); if *nothing* sold, add `delivery_cents` and the delivery's own tax
   (`checkout_orders.tax_cents − SUM(box tax_cents)`) as well, since there is
   no longer a delivery to make. `needs_refund=1` and
   `status='PARTIAL_REFUND_FLAGGED'` (or `REFUND_FLAGGED` if nothing sold).
   Log a warning if `amount` ≠ `checkout_orders.total_cents`. **That comparison
   is against the ORDER total, never the sum of the box prices** — with tax and
   a delivery charge the paid amount is legitimately larger than the line-item
   sum, and `total_cents` is Square's own `total_money`, which already includes
   both (§8.7).
5. Order → `paid`, `closed_at`.
6. **Retire competitors, DB-first:** for every box just sold, other `open`
   `kind='link'` orders containing it → `status='canceled'` (same tx).
7. COMMIT. Then best-effort `DeletePaymentLink` on each canceled order; set
   `link_deleted_at` **only when the response has `cancelled_order_id`**.
   Failures/missing id are left for Reconcile.

Webhook = signature check → parse with `TryGetProperty` → product filter (L1)
→ COMPLETED gate → `FulfillOrderAsync`. `refund.updated` COMPLETED →
`refunded_cents += amount`, `status = CASE WHEN refunded_cents >= amount_cents
THEN 'REFUNDED' ELSE 'PARTIAL_REFUNDED' END`, `needs_refund=0` if
`refunded_cents >= refund_due_cents`. `FAILED/REJECTED` → `needs_refund=1`.

### Cancel-on-change (the same helper everywhere): `CancelOpenLinksForBoxAsync(conn, tx, manifestId)`
Sets competing `open` `kind='link'` orders → `canceled` in the caller's
transaction; Square deletes happen after commit, best-effort. Called from:
- `UpdatePallet` when `listPrice`/`salePrice` change or `publishState` leaves
  `live` or `archived` becomes true (fixes L3, L9).
- `InvoiceBox` (before creating the invoice; fixes L5), `SoldToInventory`
  (replaces the fail-closed 502 — DB cancel is the fence now), `DeletePallet`
  (also deletes the box's `checkout_order_boxes` rows; fixes L10).

### Reconcile (`POST /api/square-reconcile`) — set-based first, Square second
1. DB pass, no Square calls: cancel `open` `kind='link'` orders where any
   box is unavailable (rule above), any `amount_cents` ≠ current ask×100, or
   `created_at` older than **7 days** (Square links never expire; we must).
2. Sweep `canceled AND link_deleted_at IS NULL` → `DeletePaymentLink`
   (stamp only with `cancelled_order_id`). Cap per run.
3. `RetrieveOrder` the remaining `open` orders **newest first**, capped per
   run: paid (`IsOrderPaid`, plus `state <> 'CANCELED'`) →
   `FulfillOrderAsync(source='reconcile')`; `state='CANCELED'` → mark
   canceled. `kind='invoice'` orders are never canceled here.
4. Trigger: keep the staff button; add a GitHub Actions cron (every 15 min)
   calling it with a function key so L9 stops depending on a click.

### Refunds (`POST /api/square-refund`)
Amount = `COALESCE(refund_due_cents, amount_cents - refunded_cents)` (or an
explicit `amountCents` ≤ remaining, for staff-initiated partials). Key
`nslr-{paymentId}-{amountCents}` (≤45 chars). Guard: `refunded_cents >=
amount_cents` → 409.

`RefundPayment` is **amount-only** — there is no way to tell Square "return
these line items and their taxes"; itemised returns belong to the Orders
returns/exchanges flow, which payment links do not give us
(https://developer.squareup.com/docs/payments-api/refund-payments). So the
amount we send has to be tax-inclusive already, which is exactly what
`refund_due_cents` now is. Consequence to tell Rob: a partial refund lands in
Square's reporting as an amount, not as a tax adjustment, so when he files NC
sales tax the figure is **tax collected minus tax refunded** out of our
`checkout_orders` / `checkout_order_boxes` columns, not Square's partial-refund
report. §8.8.

### Invoice adaptations
`InvoiceBox` inserts a `kind='invoice'` order with one child row;
`CancelBoxInvoice` finds it via `checkout_order_boxes.manifest_id` +
`kind='invoice' AND status='open'` → `canceled`. `invoice_id/invoice_url` on
`manifests` unchanged.

### Sales summary / payments list
- `SalesSummary` web rows and admin-rows `NOT EXISTS` both join
  `payments.square_order_id → checkout_order_boxes` with `outcome='sold'`,
  status test `<> 'REFUNDED'` (not `LIKE 'COMPLETED%'`). Cost = SUM of sold
  boxes' cost; **the margin formula lives in §8.6 and only there** —
  `margin_cents = goods_cents − cost`. (An earlier draft of this section said
  `amt − refunded − cost`; that is wrong and is struck, so nobody "fixes" the
  code back to it.) `SaleRow` gains `boxes`
  (string, e.g. `#12, #14`); `pallet_number` keeps its type.
- `ListSquarePayments` adds `boxes` (STRING_AGG) and `refund_due_cents`;
  `staff/js/sales.js` renders `r.boxes || '#'+r.pallet_number` and shows the
  due amount on the refund button.

### Config
`SQUARE_PUBLIC_BASE_URL` for the redirect; webhook signature key and URL
split per environment like the access token.

---

## 5. Races and failure modes

| Case | Outcome |
|---|---|
| Two shoppers, overlapping carts, first pays | Fulfilment cancels the second link in the DB and deletes it at Square. Their hosted page errors. Drawer prune catches it if they come back. |
| Second pays anyway (delete window, or Square's known delete failure) | Unsold boxes sell normally; boxes already sold → `outcome='unavailable'`, `refund_due_cents`, one-click partial refund in admin. |
| Price changed while a link is open | `UpdatePallet` cancels the link; Reconcile double-checks amounts. |
| Box pulled/archived/invoiced while in carts | Cancel-on-change; drawer prunes; checkout returns 409 with the id. |
| Buyer abandons the hosted page | Nothing held; link canceled by Reconcile after 7 days or the first competing sale. |
| Webhook missed | Reconcile fulfils the whole order (same routine, same transaction). |
| Fulfilment crashes mid-way | Transaction rolls back including the `payments` anchor; Square's retry redoes it. |
| Floor sale on the terminal | Ignored by product filter; never flagged. |
| Double-click Checkout | Button disabled; server returns the identical open link (same boxes *and* same delivery choice). |
| Buyer changes the delivery choice with a link already open | The reuse test includes `delivery_method`/`zip`/`address`, so a new link is minted. The stale one is canceled by the first competing sale or aged out at 7 days; both are ours, so a buyer who goes back and pays the old one still pays a valid price — only the delivery line differs, and the order row records which. |
| Rob deactivates a zip while a link is open | The open link keeps its $10 charge (already priced and disclosed). Reconcile does **not** cancel on a zip change — its amount test only covers box prices. Accepted: worst case is one delivery just outside the new radius. |
| Delivery chosen, then every box turns out unavailable | Nothing sold; `refund_due_cents` = the whole payment including the delivery fee and all tax, `status='REFUND_FLAGGED'`. |
| Delivery chosen, some boxes unavailable | The sold boxes go out on the same run; the refund returns the unavailable boxes' price **plus their tax**, and the $10 stays — the van still drives. |
| Buyer types a qualifying zip but a bogus street address | Not detectable. NSL phones the buyer from the Square receipt, as it already does for pickup. |

---

## 6. Rollout

**Phase 0 — hotfix PR (small, independent, this week):** L1 product filter
+ SQL to clear the two flagged floor payments; L7 invoice guard on
Sold-to-inventory; L11 `TryGetProperty`. Sandbox not required; verify with a
floor sale after deploy (no new `UNMATCHED` row).

**Phase 1 — cart PR:**
0. **Before a single taxed order is taken in production:** Rob must hold an NC
   Certificate of Registration for sales & use tax and know his filing
   frequency — collecting sales tax without one is not something a checkout
   flag can fix (§8.9). Separately, confirm the Square *location's* own tax
   settings so a floor sale and a web sale both land on 7.25% and the web
   order is not taxed twice.
1. Apply `db/cart-checkout.sql` to prod (additive), then seed
   `dbo.delivery_zips` from the list Rob confirms (§8.4). Open PR → preview
   build is the compile check (no local .NET SDK).
2. **Production verification — there is no sandbox for this account** (Jeff,
   2026-09-15). Disposable `ZZ TEST — DO NOT BUY` boxes at $18/$25, every
   payment refunded at once, the live webhook subscription never touched (a
   missed webhook is simulated in the DB instead), and the API never run
   locally against production Square. Ground rules and the full scenario list
   live in the plan's Task 14. In outline: two-box cart pays →
   both SOLD, one `payments` row, order `paid`; overlap A{1,2} / B{2,3}: pay
   A → B canceled + deleted; force-pay B → box 3 sells, box 2
   `unavailable`, `refund_due_cents` = box 2, admin refund refunds exactly
   that; unsubscribe webhook, pay, run Reconcile → healed; price change on a
   box in an open cart → link canceled.
3. Playwright on shop.html: add/✓ toggle, badge + bottom bar, drawer prune
   after archiving a box, 409 path, thanks clears cart, phone width, Back from
   Square resets the button.
4. Merge → deploy → **re-run** the copy portion of `cart-checkout.sql`.
5. Production: two $0.50 boxes, live card, confirm fee rate, refund.
6. Days later: `cart-checkout-drop.sql`.

---

## 7. Out of scope (deliberately)

Quantities > 1, catalog sync, carrier shipping rates, holds while a shopper is
on the Square page, accounts/saved carts and member login (belongs to the
reseller-program work — `RESELLER-PROGRAM-DESIGN.md` §1), coupons/tips
(disabled on purpose so the paid amount == our order total), live
distance/geocoding for the delivery radius (§8.4).

### 7.1 Card surcharge — NOT built, and here is why

Rob, 2026-09-14: *"add … a 3% credit card processing fee if they use a card."*
Recommended against, explained, and the client has not come back with a new
instruction. **Build without it.** Recorded here so nobody reopens it blind:

1. **Square cannot do it on this surface.** Square's surcharge feature covers
   the Point of Sale / Retail / Restaurant apps, the card reader, and Square
   Invoices (web only). It is explicitly *not* supported on **Square Website
   transactions** — which is what a payment link is — nor on Virtual Terminal,
   Kiosk, Connected Terminal, or Appointments.
   https://squareup.com/help/us/en/article/8596-set-up-and-manage-card-surcharges
   We could of course add our own 3% `service_charge`, which is exactly the
   problem: it would be *us* surcharging, under the card-network rules below,
   with none of Square's compliance machinery behind it.
2. **Debit and prepaid cards may never be surcharged** — not by any network,
   not even when the card is run as credit. A hosted checkout page cannot tell
   a debit card from a credit card before the payment is taken, so a flat 3%
   on the order would surcharge debit cards. That is a straight violation.
3. Where surcharging *is* allowed it comes with conditions: Visa caps it at
   3% (and at the merchant's actual cost of acceptance, whichever is lower),
   it must be disclosed at the point of entry to the store and again on the
   receipt, and **the acquirer must be notified 30 days in advance**.
4. **North Carolina** permits it only in the weak sense that no statute
   forbids it — surcharge-ban bills died in committee in 2011, 2023 and 2025.
   "Not illegal" is not the same as "compliant with the card network rules".
5. The economics do not work either: our card cost is ~2.9% + 30¢, so on a
   $180 box a compliant 3% cap under-recovers on the fixed 30¢ and
   over-recovers on a $600 pallet. It cannot exceed actual cost of acceptance.

**The alternative we offered Rob, still open:** a **cash/ACH discount** —
price everything at the card price and knock a stated percentage off for cash
or check at the warehouse. Legal in all 50 states, no network notification, no
debit/credit distinction to get wrong.

If Rob chooses the cash-discount route, note what it is **not**: it is not a
checkout feature. It is a pricing decision plus a sign. Concretely: raise ask
prices by the chosen margin, put "Cash price: −3%" on the shop page and in the
drawer's pickup copy, and have the floor ring the cash price in Square POS.
Nothing in `POST /api/public/checkout`, the payload builder, the webhook or the
refund path changes — the online price *is* the card price. The only code that
would move is display copy. Cost: an hour, not a sprint.

---

## 8. Sales tax & delivery (added 2026-09-14 for Rob)

Rob, 2026-09-14:

> "At checkout, add 7.25% sales tax and a 3% credit card processing fee if they
> use a card."
>
> "Checkout, allow people to select whether they will pick up from our
> warehouse, request delivery for $10 and add to cost, or deliver to Flea
> Market for free. If they have no address entered, then it should prompt them
> to add their address at checkout to see if they qualify for the $10 delivery.
> If they are a member, it will automatically know."
>
> "Add lines for people to add their physical address when they join."

Decided: tax **yes** (§8.1), delivery **yes** (§8.2–§8.4), card surcharge **no**
(§7.1). Member street address **yes**, but "it will automatically know" holds
only as far as this device (§8.5).

Everything below was verified against developer.squareup.com on 2026-09-14.

### 8.1 The tax

**7.25% = 4.75% NC state + 2.00% Wake County local + 0.50% Wake transit.** One
rate, one line: the warehouse is in Wake Forest and the flea-market stall is
also in Wake County, so there is no second jurisdiction to reason about. North
Carolina folds a delivery charge into the "sales price" of a taxable sale
(G.S. 105-164.3 —
https://www.ncleg.gov/EnactedLegislation/Statutes/PDF/BySection/Chapter_105/GS_105-164.3.pdf),
so **the $10 delivery is taxed too**: $10.00 of delivery costs the buyer
$10.73.

**Verified against the live Square account, 2026-09-15.** The account already
carries a catalog tax **"NC & Wake County Sales Tax"**, `catalog_object_id`
**`NJMJVQ3TQDEYCNQJJ5MGTCXT`**, `percentage 7.25`, `ADDITIVE`, enabled,
`applies_to_custom_amounts: true`, at the Wake Forest location (27587). Two
consequences:

1. **NSL is demonstrably registered to collect NC sales tax** — nobody
   configures that rate and rings it on a terminal otherwise. The blocking
   open question "is Rob registered with NCDOR" is answered yes in practice;
   what remains for his accountant is filing frequency, not permission.
2. **Reference the existing tax rather than inventing our own.** Passing
   `applied_taxes: [{ catalog_object_id: "NJMJVQ3TQDEYCNQJJ5MGTCXT" }]` makes a
   web sale land under the *same named tax* as a floor sale in Rob's Square
   reporting, so his sales-tax total reconciles in one place and a future rate
   change is one edit in Square rather than a code deploy. Supplying our own
   ad-hoc 7.25% would produce a second, differently-named tax line in the same
   reports for the same legal tax. Prefer the catalog reference; keep the
   ad-hoc shape below as the fallback if the catalog object turns out not to be
   present at the location used for the link (`present_at_all_locations` is
   **false**, so confirm it on the first order rather than assuming).

> **A gap that exists right now, before any of this ships.** The live
> single-box checkout builds its link with `quick_pay { name, price_money }`
> and **no tax at all** (`SquareService.CreatePaymentLinkAsync`). So the same
> box costs 7.25% more at the counter than on the website. Exposure to date is
> nil — `dbo.payments` holds exactly one real web payment ever, the $1.00
> launch test on 2026-08-28, refunded — but it starts the moment somebody buys
> a box online, and web checkout is enabled today. Either ship the cart before
> that happens, add the tax to the existing one-box path as a small standalone
> change, or turn web checkout off until one of those lands.

Square will not compute the tax for us on an ad-hoc order: a payment link built
from an ad-hoc `order` has no catalog *item*, so no tax is attached by default
and we must name one — either the catalog object above, or inline:

```jsonc
"order": {
  "location_id": "…",
  "line_items": [
    { "uid": "<manifest_id>", "name": "BOX #12 — Tools", "quantity": "1",
      "base_price_money": { "amount": 18000, "currency": "USD" },
      "applied_taxes": [ { "tax_uid": "NC-SALES-725" } ],
      "note": "Pickup in Wake Forest, NC" }
  ],
  "taxes": [
    { "uid": "NC-SALES-725", "name": "NC sales tax (7.25%)",
      "percentage": "7.25", "type": "ADDITIVE", "scope": "LINE_ITEM" }
  ]
}
```

- `type: "ADDITIVE"` — our ask prices are tax-exclusive and tax is added on
  top. `INCLUSIVE` would carve the tax back *out* of the box price and quietly
  shrink revenue by 6.76% per box.
- `scope: "LINE_ITEM"` with an explicit `uid`, referenced from every line's
  `applied_taxes`, rather than the terser `scope: "ORDER"`. An ORDER-scope tax
  is spread across **line items only**; it does not reach a service charge.
  Square is explicit that a service charge is taxed only when it carries
  `applied_taxes`, and that those can only point at a LINE_ITEM-scope tax:
  *"Setting `service_charges.taxable` has no bearing on whether a tax is
  charged on the service charge. As long as you set
  `service_charges.applied_taxes`, a tax will be applied"* … *"this is why the
  tax needs to be a `LINE_ITEM` tax."*
  https://developer.squareup.com/docs/orders-api/service-charges
  Since NC taxes the delivery fee, LINE_ITEM scope is the shape that lets the
  one tax object cover both the boxes and the $10. (Scope/type semantics:
  https://developer.squareup.com/docs/orders-api/taxes and
  https://developer.squareup.com/docs/orders-api/apply-taxes-and-discounts)
- The buyer still sees **one** "NC sales tax (7.25%)" line on the hosted page
  and the receipt — one tax object, not one per box.

### 8.2 The delivery fee — `service_charges[]`, not `checkout_options.shipping_fee`

**Recommendation: `order.service_charges[]`.** Both routes exist and both show
the buyer a separate line, but `shipping_fee` cannot be taxed.

| | `order.service_charges[]` | `checkout_options.shipping_fee` |
|---|---|---|
| Where it lives | inside our order, with the line items | a checkout option, outside the order we build |
| Taxable | **yes** — `applied_taxes` → our tax uid | **no**. Square materialises it as a service charge with `"taxable": false`, `"total_tax_money": 0` and no `applied_taxes`, and there is no field to change that |
| Name | ours ("Local delivery — within 20 miles") | ours (`name`) |
| Phase | we choose `SUBTOTAL_PHASE` (before tax) | Square uses `SUBTOTAL_PHASE` |
| Buyer sees a separate line | yes | yes |
| Couples to shipping-address collection | no | in every Square example it rides with `ask_for_shipping_address: true` |

The taxability row decides it on its own: NC taxes the delivery charge, so a
fee we cannot attach tax to is the wrong tool. The shape:

```jsonc
"service_charges": [
  { "uid": "NSL-DELIVERY", "name": "Local delivery (within 20 miles)",
    "amount_money": { "amount": 1000, "currency": "USD" },
    "calculation_phase": "SUBTOTAL_PHASE",
    "scope": "ORDER",
    "treatment_type": "LINE_ITEM_TREATMENT",
    "taxable": true,
    "applied_taxes": [ { "tax_uid": "NC-SALES-725" } ] }
]
```

`SUBTOTAL_PHASE` (before taxes) rather than `TOTAL_PHASE` (after) — a
TOTAL_PHASE charge is added on top of the taxed total and therefore never gets
taxed itself. `taxable: true` is belt-and-braces for whoever reads the order in
the Dashboard; `applied_taxes` is what actually does it. The whole array is
**omitted** for pickup and for the flea-market drop, so those orders carry no
service charge at all.

`checkout_options.shipping_fee` is never sent. Shape and the materialised
`taxable: false` service charge:
https://developer.squareup.com/docs/checkout-api/optional-checkout-configurations

### 8.3 Where the delivery choice is collected — our drawer, not Square's page

Square's hosted page is not ours to extend. The realistic options were:

- **(a) Collect it in the cart drawer, before the link is minted**, and encode
  the outcome as a service charge on the order. ✅ **Recommended, and what this
  spec builds.**
- (b) Turn on `checkout_options.ask_for_shipping_address`. Rejected: it gives
  the buyer an address form, not a three-way choice. Setting it true also
  forces the order's fulfilment type to `SHIPMENT` and makes name, phone and
  address **mandatory** for everyone — including the 90% who are driving to
  Wake Forest to pick up a pallet. There is no way to say "ask only if they
  chose delivery", and no way to price the three options differently from it.
  (What it *does* return, for the record: after payment Square populates
  `Order.fulfillments[].shipment_details.recipient` with the name, phone and
  address, and moves the order DRAFT → OPEN. Since we already have the address
  from our own form, that is a duplicate we do not need.)

So: `ask_for_shipping_address` stays omitted (false), and the drawer footer
gains a required radio group above the receipt block:

```
How do you want them?
  ( ) Pick up at our Wake Forest warehouse            Free
  ( ) Delivered to you                                $10
  ( ) Meet us at the Raleigh Flea Market on Friday    Free
```

- **Pickup** is preselected. It is the current behaviour and the common case.
- **Delivered to you** is *disabled* until a qualifying zip is known, with the
  reason shown inline rather than as a dead control:
  *"$10 delivery is for addresses within about 20 miles of Wake Forest — add
  your zip to check."* plus a small **zip** input and a **Check** button. On a
  hit the radio enables and a one-line **street address** field (plus city,
  prefilled) appears, required before Checkout. On a miss:
  *"We can't reach 28202 on our own truck — pickup or the Friday flea-market
  drop are still free."*
- **Flea Market** shows the stall and the day in the label so nobody has to
  guess where they are meeting us.
- The group is `role="radiogroup"` with a visible legend, arrow-key navigation
  and a live region for the eligibility answer, reusing the drawer's existing
  `aria-live` notice element.
- The receipt block under it updates live: Subtotal · Delivery · Sales tax
  (7.25%) · **Total**. Browser-side arithmetic, display only — Square's numbers
  on the next screen are authoritative, and §8.6 explains why they can differ
  by a cent and why we do not care.
- The choice is sent with the ids on `POST /api/public/checkout`
  (`{ ids, delivery, zip, address, memberNumber }`) and **re-validated
  server-side**; the browser list is a convenience.
- Kill-switch and 409 behaviour are unchanged. A 400 on the zip renders in the
  same notice element as everything else.

### 8.4 "Within 20 miles" — a zip allow-list, not a geocoder

**Recommendation: a curated zip allow-list in `dbo.delivery_zips`**, read by
`GET /api/public/checkout-status` and enforced on `POST /api/public/checkout`.

Why not a live distance lookup (Google Distance Matrix, Azure Maps, Mapbox):

- The origin never moves. One warehouse, one answer per zip, forever. A
  distance API would recompute a constant on every checkout.
- It buys false precision. "Within 20 miles" is really "is this worth Norm
  taking the truck out", which is Rob's judgement, not a haversine. He will
  want to say yes to a 23-mile regular and no to a 19-mile nightmare street.
  A list lets him. A radius does not.
- It puts a third-party key, a monthly bill, a rate limit and a new failure
  mode on the hot path of minting a payment link, for a handful of orders a
  week.
- A 30-row table is auditable, editable by Rob without a deploy, and free.

A table rather than app config for exactly that last reason — Rob changes the
radius by editing rows, not by asking for a deploy — and it leaves room for
`fee_cents` to differ by zip later without another migration.

**Starter set — needs Rob's confirmation before it is seeded.** Straight-line
distances from Wake Forest; nobody has driven these.

*Core (comfortably inside 20 miles):* `27587` Wake Forest, `27588` Wake Forest
(PO boxes), `27571` Rolesville, `27596` Youngsville, `27525` Franklinton,
`27522` Creedmoor, `27614`, `27616`, `27615`, `27613`, `27617`, `27609`,
`27604` north Raleigh, `27545` Knightdale, `27591` Wendell.

*Borderline — Rob's call, seeded inactive:* `27601`, `27605`, `27606`, `27607`,
`27608`, `27610`, `27612` inner Raleigh; `27597` Zebulon; `27549` Louisburg;
`27560` Morrisville; `27703` Durham; `27529` Garner.

Ask Rob to strike or add rather than to approve a number. The list is the
policy.

**The member shortcut.** If the shopper's zip is already known (§8.5), the
eligibility answer is known too and the zip input never appears — the radio is
simply enabled or not, with the zip echoed: *"Delivered to you — $10 (to
27587)"*. That is the whole of "if they are a member, it will automatically
know", and its limits are the next section.

### 8.5 Member address, and the honest limit on "it will automatically know"

`dbo.members` today has `city`, `state`, `zip` and no street address. A
separate agent is adding the street lines and the matching fields to the join
modal; this spec does not touch that work and only depends on the result.
Suggested column names so the two changes meet: `address_line1 NVARCHAR(200)
NULL`, `address_line2 NVARCHAR(100) NULL`, alongside the existing `city` /
`state` / `zip`.

What the cart needs from it is small: **the zip, for eligibility**, and the
**full address, for the delivery run** — the latter copied onto
`checkout_orders.delivery_address` at link time, so the record of where a
specific order went does not drift when the member later moves.

**How the cart knows the shopper is a member — and why the answer is "only on
this device".** The public site has no login. Signup posts to
`POST /api/public/register` and the browser keeps the returned member number in
`localStorage['nsl.member']`; the header button relabels to ★ Member #2600001
from that. There is no public endpoint that reads a member row back —
`MembersFunction` exposes the list and the CSV export to staff only.

**Do not add a public `GET /api/public/member/{number}`.** Member numbers are
sequential (`YY` + a 5-digit counter: 2600001, 2600002, …), so such a route
would let anyone walk the range and harvest every member's home address. That
is a much worse trade than an extra form field.

So, v1: when signup succeeds, the join modal also stores the zip (and, once it
collects them, the address lines) in `localStorage` next to the member number —
`nsl.zip`, `nsl.addr`. The drawer prefills from those, and the server
re-validates the zip against `delivery_zips` regardless. Rob should hear it
plainly: **this works on the device they signed up on, the same guarantee the
member number already has. On a new phone they type their zip once.**

**Prerequisite for anything stronger: member login.** Rob has separately asked
for member accounts — `RESELLER-PROGRAM-DESIGN.md` §1 ("sign up, verify email +
phone, log in, manage a profile"), Entra External ID behind `/account/*`. Once
that ships, `POST /api/public/checkout` can read the signed-in member's address
server-side from `dbo.members`, the drawer needs no address form for members at
all, and "it will automatically know" becomes true everywhere. Until then it is
device-local. Flagged rather than fudged.

### 8.6 Rounding and storage

**We never compute the authoritative tax.** Every money column on
`checkout_orders` and `checkout_order_boxes` is read out of the
`CreatePaymentLink` response's `related_resources.orders[0]`, which is the same
order the buyer pays:

| Column | Source |
|---|---|
| `checkout_orders.total_cents` | `total_money.amount` |
| `checkout_orders.tax_cents` | `total_tax_money.amount` |
| `checkout_orders.delivery_cents` | `total_service_charge_money.amount` |
| `checkout_orders.subtotal_cents` | `total_cents − tax_cents − delivery_cents` |
| `checkout_order_boxes.amount_cents` | that line's `base_price_money.amount` |
| `checkout_order_boxes.tax_cents` | that line's `total_tax_money.amount`, matched on `uid` = manifest id |

Square has already done the per-line rounding, and it hands back the per-line
result, so there is no apportionment to invent and no chance of our numbers
disagreeing with the buyer's receipt by a cent. The browser's live receipt in
the drawer is `round(subtotal × 0.0725)` on the whole cart, which can differ
from Square's per-line sum by a cent or two on a large cart; that is a display
figure and the hosted page corrects it one click later. The order row is never
built from it.

**Why the split matters beyond bookkeeping:** the sales dashboard must not
count tax or delivery as revenue or margin. So `SalesSummary`'s web rows report
`goods_cents = SUM(checkout_order_boxes.amount_cents WHERE outcome='sold')` as
the sale amount, surface `tax_cents` and `delivery_cents` as their own columns,
and compute `margin_cents = goods_cents − cost`. Boxes with
`outcome='unavailable'` are already excluded by the join, so a refund for them
never needs subtracting twice. (Known imprecision, accepted: a *goodwill*
partial refund on a fully-sold order is not attributed back to a box, so margin
for that one row reads high. The `refunded_cents` column is displayed next to
it so staff can see it.)

### 8.7 The paid-amount check

The existing warning in fulfilment — "payment amount ≠ order total" — must
compare against **`checkout_orders.total_cents`**, and that column must hold
Square's `total_money`, not our sum of box prices. With tax and a delivery
charge in play, `payment.amount_money` legitimately exceeds the line-item sum;
a check against the sum would fire on every single taxed order.

Confirmed from Square's own documented response: for a $20.00 item with a
$4.99 fee, `related_resources.orders[0].total_money` is `2499`, alongside
`total_tax_money` and `total_service_charge_money` broken out separately, and
`net_amounts.total_money` agrees
(https://developer.squareup.com/docs/checkout-api/optional-checkout-configurations).
`total_money` is the amount due and is what the buyer's payment will match.
This was already the v2 intent — §0 says *"never assert `amount_money ==`
line-item sum; compare to `order.total_money`"* — but with tax and delivery it
stops being a nicety and becomes load-bearing, so it is restated here.

### 8.8 Refunds when tax and a delivery charge are present

`RefundPayment` takes an **amount**, full stop. There is no way to say "return
box #14 and the tax on it"; itemised returns with `return_amounts` /
`return_taxes` belong to the Orders returns-and-exchanges flow, which a payment
link does not open up to us
(https://developer.squareup.com/docs/payments-api/refund-payments,
https://developer.squareup.com/docs/orders-api/order-returns-exchanges).
Square's own reporting also treats an amount-only partial refund as an amount,
**not** as a tax adjustment.

**Recommendation — yes, refund the proportional tax, and do it by storing it
rather than by computing it.** Because `checkout_order_boxes.tax_cents` holds
the exact tax Square charged on that box, the "some boxes sold out from under
the buyer" path becomes arithmetic with no apportionment:

- some boxes sold, some not →
  `refund_due_cents = SUM(amount_cents + tax_cents WHERE outcome='unavailable')`.
  The delivery fee and its tax stay: the van is still going out for the boxes
  that did sell.
- nothing sold →
  `refund_due_cents = total_cents` (the whole payment: boxes, all tax, and the
  delivery fee, since there is no longer a delivery to make).
- a staff-initiated goodwill refund is still an explicit `amountCents` and is
  the staff member's problem to gross up; the admin button's tooltip says so.

Not refunding the tax would mean keeping money the buyer paid us for a box they
never received — and we cannot remit it to NCDOR against a sale that did not
happen. The practical cost of doing it right is one extra column.

**Tell Rob:** his NC filing figure is *tax collected minus tax refunded*, taken
from `checkout_orders.tax_cents` and the `tax_cents` of refunded boxes — our
numbers, not Square's partial-refund report, which does not break tax out.

### 8.9 Open questions for the client

| # | Question | Who | Blocking? |
|---|---|---|---|
| 1 | **Is NSL registered with NCDOR to collect sales tax**, and at what filing frequency? Nothing here is legal to switch on without a Certificate of Registration. | Rob | **Yes — blocks the live taxed order** |
| 2 | Confirm / edit the zip allow-list in §8.4. The borderline list is seeded inactive until he answers. | Rob | Yes — blocks seeding `delivery_zips` |
| 3 | Flea-market drop: confirm the day is **Friday**, the venue, and the handover window, so the radio label and the receipt note are right. | Rob | Yes — it is buyer-facing copy |
| 4 | Cash/ACH discount instead of the card surcharge (§7.1) — yes or no? Pricing decision, not a checkout change. | Rob | No |
| 5 | Does the Square **location** already have a tax configured? If so, reconcile it with our 7.25% so floor and web agree and nothing is taxed twice. | Jeff to check, Rob to confirm | Yes |
| 6 | **Does NC tax a surcharge line?** Not resolved and not confirmed with NCDOR by anyone. It is moot while §7.1 stands, but if the cash-discount route is ever inverted into a surcharge, this has to be answered first. | Rob's CPA / NCDOR | No (moot today) |
| 7 | Minor: secondary sources disagree on whether a *separately stated* delivery charge is taxable in NC. The statutory definition of "sales price" reads as inclusive, which is why §8.1 taxes it, but Rob's CPA should confirm once. Getting it wrong costs $0.73 an order in the safe direction. | Rob's CPA | No |
