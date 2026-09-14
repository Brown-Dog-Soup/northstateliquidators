# Cart + Combined Checkout — Design

**Status:** REVISED DRAFT v2 (2026-09-13) — after a four-track review
(backend audit, spec-vs-code, front-end, Square API verification) and a
production data check. Awaiting Jeff's sign-off.
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
- Pickup-only fulfillment; NSL contacts the buyer from the Square receipt.
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
    total_cents      BIGINT        NOT NULL,
    created_at       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    closed_at        DATETIME2     NULL,
    link_deleted_at  DATETIME2     NULL                 -- Square confirmed (cancelled_order_id present)
);
CREATE INDEX IX_co_status ON dbo.checkout_orders (status, kind, created_at);

CREATE TABLE dbo.checkout_order_boxes (
    square_order_id  VARCHAR(64)      NOT NULL REFERENCES dbo.checkout_orders,
    manifest_id      UNIQUEIDENTIFIER NOT NULL,          -- no FK: DeletePallet cleans up explicitly (§4)
    amount_cents     BIGINT           NOT NULL,          -- price at link time
    outcome          VARCHAR(16)      NULL,              -- NULL | 'sold' | 'unavailable'
    fulfilled_at     DATETIME2        NULL,
    PRIMARY KEY (square_order_id, manifest_id)
);
CREATE INDEX IX_cob_manifest ON dbo.checkout_order_boxes (manifest_id);

ALTER TABLE dbo.payments ADD
    refund_due_cents BIGINT NULL,      -- what admin should refund (partial case)
    refunded_cents   BIGINT NOT NULL DEFAULT 0;   -- cumulative, from refund.updated

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
  row per box: `BOX #N · name · price`, ×. Footer pinned: total (computed in
  cents), pickup note, `Checkout with Square →`. On open: **fresh** fetch
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

### `POST /api/public/checkout` — body `{ ids: Guid[] }`
1. Kill switch / configured (503).
2. De-dupe; 1..20 else 400.
3. Load all boxes in one query. Each must be `live`, not ghost, not archived,
   `invoice_id IS NULL`, priced > 0; else `409 { error, unavailable: [ids] }`.
4. **Reuse** an `open` `kind='link'` order with the identical
   `(manifest_id, amount_cents)` set — no time window. Open + identical is
   current *by construction* because price changes and state changes cancel
   links (below).
5. Else `CreatePaymentLinkAsync(order)`: one ad-hoc line per box —
   `name="BOX #N — display name"` (≤512), `quantity="1"`,
   `base_price_money`, `note` = short human text ("Pickup in Wake Forest"),
   **not** the GUID (visible on receipt). Order `reference_id` = a compact
   join of pallet numbers. `checkout_options`: `redirect_url =
   {baseUrl}/thanks.html?boxes=…` (base from config, not hard-coded),
   `enable_coupon:false`, `allow_tipping:false`, `ask_for_shipping_address`
   omitted (false), `merchant_support_email` set, one `custom_field`
   "Name & phone for pickup". `idempotency_key = nsl-cart-{Guid}`.
   `payment_note` = box numbers (≤500).
6. Insert `checkout_orders` (`total_cents` from the create response's
   `related_resources.orders[0].total_money`) + child rows. Return `{ url }`.

`POST /api/public/checkout/{id}` stays as a wrapper (cart of one, redirects
with `?boxes=N`) for tabs loaded before the deploy.

### Fulfilment — one `FulfillOrderAsync(conn, tx, orderId, paymentId, amount, raw, source)`
Used by the webhook and Reconcile. Runs **entirely inside one SqlTransaction**
(pattern from `DeletePallet`; `sp_SetPublishState`'s inner TRAN nests fine):
1. `payments` INSERT (idempotency anchor). `inserted == 0` → done (`duplicate`).
2. Order row by `order_id`. None → **fallback**: `RetrieveOrder`, read
   `reference_id` → pallet numbers → boxes (rescues the deploy gap and a lost
   insert). Still none → `UNMATCHED` (only for our own products, see L1).
3. Boxes `ORDER BY manifest_id` (deadlock-safe). For each with
   `outcome IS NULL`:
   - **available** = `publish_state='live' AND archived_at IS NULL AND
     is_ghost=0 AND invoice_id IS NULL` (for `kind='link'`); for
     `kind='invoice'` = not sold. → `sp_SetPublishState 'sold'`, history row
     `changed_by='square'`, `outcome='sold'`.
   - otherwise `outcome='unavailable'` (someone else got it, or it was pulled).
   A box already `outcome='sold'` by this same order is skipped, never
   refund-flagged (re-entrancy after a partial failure).
4. `payments.manifest_id` = the box when exactly one; `refund_due_cents =
   SUM(amount_cents WHERE outcome='unavailable')`; `needs_refund=1` and
   `status='PARTIAL_REFUND_FLAGGED'` (or `REFUND_FLAGGED` if nothing sold).
   Log a warning if `amount` ≠ `total_cents`.
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

### Invoice adaptations
`InvoiceBox` inserts a `kind='invoice'` order with one child row;
`CancelBoxInvoice` finds it via `checkout_order_boxes.manifest_id` +
`kind='invoice' AND status='open'` → `canceled`. `invoice_id/invoice_url` on
`manifests` unchanged.

### Sales summary / payments list
- `SalesSummary` web rows and admin-rows `NOT EXISTS` both join
  `payments.square_order_id → checkout_order_boxes` with `outcome='sold'`,
  status test `<> 'REFUNDED'` (not `LIKE 'COMPLETED%'`). Cost = SUM of sold
  boxes' cost; `margin = amt - refunded - cost`. `SaleRow` gains `boxes`
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
| Double-click Checkout | Button disabled; server returns the identical open link. |

---

## 6. Rollout

**Phase 0 — hotfix PR (small, independent, this week):** L1 product filter
+ SQL to clear the two flagged floor payments; L7 invoice guard on
Sold-to-inventory; L11 `TryGetProperty`. Sandbox not required; verify with a
floor sale after deploy (no new `UNMATCHED` row).

**Phase 1 — cart PR:**
1. Apply `db/cart-checkout.sql` to prod (additive). Open PR → preview build
   is the compile check (no local .NET SDK).
2. Sandbox (local Functions host with sandbox settings): two-box cart pays →
   both SOLD, one `payments` row, order `paid`; overlap A{1,2} / B{2,3}: pay
   A → B canceled + deleted; force-pay B in sandbox → box 3 sells, box 2
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

Quantities > 1, catalog sync, shipping rates, holds while a shopper is on the
Square page, accounts/saved carts (belongs to the reseller-program work),
coupons/tips (disabled on purpose so paid amount == our total).
