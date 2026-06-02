# On-Site Checkout (Stripe) — Build Plan

**Status:** Plan for review — no code written yet
**Author:** Jeff (with Claude Code)
**Date:** 2026-06-02

## Goal

Make **everything purchasable directly on northstateliquidators.com** — both single
"treasure hunt" retail items *and* pallets — instead of redirecting to the
password-gated Shopify dev store. Move the selling layer onto the Azure stack we
already run, with **Stripe** as the payment processor.

### Decisions locked (from kickoff)

| Decision | Choice |
|---|---|
| Checkout depth | **Full cart + combined checkout** (not per-item Buy Now) |
| Fulfillment at launch | **Local pickup only** — no shipping labels, no carrier rates |
| Shopify | **Keep running as fallback** during transition; cut over once proven |
| Scope | **Everything purchasable** — retail items *and* pallets |
| Payments | **Stripe** (2.9% + 30¢) — replaces Shopify Starter's 5% + 30¢ |

### Why this is a small build, not a rebuild

The storefront backbone already exists:

- `GET /api/public/pallets` is **already anonymous** and already returns
  `ask_price`, `list_price`, `sale_price`, `is_sold`, `is_on_sale`, `publish_state`
  from `v_public_pallets`. The homepage pallet ledger already renders from it
  (`index.html:669`).
- `staticwebapp.config.json` already separates `/api/public/*` (anonymous) from
  `/api/*` (Entra-gated staff). Customer endpoints have a clean, safe home.
- `publish_state` (`draft|live|ghost|sold`) + `sp_SetPublishState` already model
  "what's for sale" and "mark sold" — and keep `is_ghost` / `sold_at` /
  `line_items` consistent. We reuse this, we don't reinvent it.
- DI, Dapper, `SqlService`, `BlobService` (SAS photo signing) — established
  patterns; new Functions slot straight in.

What's actually missing: a **single-item** public read (today only pallets are
public; retail items still come via the Shopify `featured` sync), a **reservation
hold** so two shoppers can't buy the same one-of-a-kind item, an **orders**
record for pickup fulfillment, the **Stripe Checkout Session + webhook**
Functions, and a **cart UI**.

---

## Architecture

```
Shopper on northstateliquidators.com
  │  browses grid (retail items) + pallet ledger
  │  ── GET /api/public/items     (NEW)  ← live, unsold, unreserved line_items
  │  ── GET /api/public/pallets   (exists) ← live + ghost/sold pallets
  │
  │  adds to cart (localStorage, client-side — items are qty=1)
  │  clicks Checkout
  ▼
POST /api/public/checkout        (NEW)
  • re-reads price + availability from SQL  (NEVER trusts client prices)
  • sp_ReserveForCheckout: holds each item/pallet for 30 min, writes orders row (pending)
  • creates Stripe Checkout Session (mode=payment, pickup only, collect email+phone)
  • returns session.url  →  browser redirects to Stripe-hosted payment page
  ▼
Stripe hosted checkout  →  success_url / cancel_url back to our site
  ▼
POST /api/public/stripe-webhook  (NEW, anonymous, signature-verified)
  • checkout.session.completed → sp_MarkOrderPaid:
      - single items  → line_items.sold_at = now
      - pallets        → sp_SetPublishState(manifest, 'sold')
      - order.status   → 'paid'
      - send pickup-confirmation email (M365 / Graph)
  • checkout.session.expired / async cancel → release reservation (clear holds, order='cancelled')
```

---

## 1. Database migration — `db/stripe-checkout.sql`

Idempotent, applied after `wishlist2-part2.sql`. Follows the existing
proc-centric style and `GRANT … TO nsl_api` convention.

### New columns (reservation holds)

```sql
-- line_items: hold a single retail item during an open checkout
ALTER TABLE dbo.line_items ADD reserved_until DATETIME2 NULL;
-- manifests: hold a whole pallet during an open checkout
ALTER TABLE dbo.manifests  ADD reserved_until DATETIME2 NULL;
```

An item/pallet is **available** when `sold_at IS NULL` and
(`reserved_until IS NULL OR reserved_until < SYSUTCDATETIME()`). The lazy
expiry check means no background job is required for MVP (a future timer can
clean stale `pending` orders).

### New tables (order record for pickup fulfillment)

```sql
CREATE TABLE dbo.orders (
    id                   UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    stripe_session_id    VARCHAR(255) NULL,      -- cs_… ; set on session create
    stripe_payment_intent VARCHAR(255) NULL,     -- pi_… ; set on webhook
    status               VARCHAR(20) NOT NULL DEFAULT 'pending',  -- pending|paid|picked_up|cancelled
    customer_email       NVARCHAR(255) NULL,
    customer_phone       NVARCHAR(50)  NULL,
    customer_name        NVARCHAR(200) NULL,
    subtotal             DECIMAL(12,2) NULL,
    tax                  DECIMAL(12,2) NULL,
    total                DECIMAL(12,2) NULL,
    pickup_notes         NVARCHAR(500) NULL,
    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    paid_at              DATETIME2 NULL
);

CREATE TABLE dbo.order_items (
    id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    order_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.orders(id),
    item_type     VARCHAR(10) NOT NULL,          -- 'item' | 'pallet'
    line_item_id  UNIQUEIDENTIFIER NULL,         -- when item_type='item'
    manifest_id   UNIQUEIDENTIFIER NULL,         -- when item_type='pallet'
    title         NVARCHAR(300) NULL,            -- snapshot at purchase time
    unit_price    DECIMAL(12,2) NOT NULL,        -- snapshot (server-trusted)
    qty           INT NOT NULL DEFAULT 1
);
```

### New view — `v_public_items` (single retail items for the grid)

Single, unsold, unreserved line items that belong to a **live** pallet, with a
sell price and a photo. Mirrors how `v_public_pallets` gates by `publish_state`.

```sql
CREATE VIEW dbo.v_public_items AS
SELECT li.id AS line_item_id, li.manifest_id, li.title, li.brand, li.description,
       li.category, li.condition, li.est_resale AS price, li.est_msrp AS compare_at,
       li.photo_blob_url
FROM dbo.line_items li
JOIN dbo.manifests m ON m.id = li.manifest_id
WHERE m.publish_state = 'live' AND m.archived_at IS NULL
  AND li.sold_at IS NULL
  AND (li.reserved_until IS NULL OR li.reserved_until < SYSUTCDATETIME())
  AND li.est_resale > 0;
```

(`v_public_pallets` gets the same `reserved_until` availability filter folded in
so a pallet mid-checkout drops out of the ledger.)

### New procs

- **`sp_ReserveForCheckout(@order_id, @items_json)`** — transactional. For each
  requested item/pallet: verify still available (else raise → checkout returns
  409 "item just sold"), set `reserved_until = DATEADD(MINUTE,30,now)`, insert
  `order_items` with the **server-side** price. Insert the parent `orders` row.
  Returns the trusted line items + computed subtotal for the Stripe session.
- **`sp_MarkOrderPaid(@stripe_session_id, @payment_intent)`** — idempotent
  (safe on webhook retries). Mark items `sold_at`, pallets via
  `sp_SetPublishState 'sold'`, order `paid` + `paid_at`. No-op if already paid.
- **`sp_ReleaseExpiredReservations()`** — clears holds where `reserved_until`
  passed and order still `pending`; flips those orders to `cancelled`. Called
  lazily at the top of the public reads (cheap) and on `session.expired`.

---

## 2. API — three new Functions (`api/Functions/`)

Stack note: add the **`Stripe.net`** NuGet package to `api.csproj`; register a
typed Stripe client in `Program.cs`; `StripeSecretKey` + `StripeWebhookSecret`
live in **SWA app settings** alongside `SqlConnectionString` (never in repo).

### `PublicItemsFunction.cs` — `GET /api/public/items`
Anonymous read of `v_public_items`, photos SAS-signed via the existing
`SignRowPhotos` pattern. Powers the "This week's hunt" grid.

### `CheckoutFunction.cs` — `POST /api/public/checkout`
Body: `{ items: [{type:'item'|'pallet', id}], customer:{email,phone,name} }`.
1. `sp_ReserveForCheckout` → trusted line items + subtotal (returns **409** if
   anything was already taken — UI shows "that item just sold, removed from cart").
2. Build Stripe `SessionCreateOptions`: `Mode=payment`,
   `LineItems` from **server prices** (cents), `CustomerEmail`,
   `PhoneNumberCollection`, **no `ShippingAddressCollection`** (pickup only),
   `SuccessUrl=/order-confirmed?session={CHECKOUT_SESSION_ID}`,
   `CancelUrl=/cart`. Tax: **Stripe Tax** automatic (one flag) — recommended
   over a hardcoded NC rate so it stays correct.
3. Persist `stripe_session_id` on the order; return `{ url }`.

### `StripeWebhookFunction.cs` — `POST /api/public/stripe-webhook`
Anonymous but **verifies the Stripe-Signature header** against
`StripeWebhookSecret` (reject otherwise — this is the security boundary).
- `checkout.session.completed` → `sp_MarkOrderPaid` + send pickup email.
- `checkout.session.expired` → release reservation.
Idempotent: Stripe retries; `sp_MarkOrderPaid` no-ops if already paid.

> Add `/api/public/stripe-webhook` and `/api/public/items`/`/checkout` are
> already covered by the existing `/api/public/*` anonymous route — no
> `staticwebapp.config.json` change needed beyond confirming the webhook path
> sits under `/api/public/`.

---

## 3. Frontend — cart + checkout (`index.html` + small JS)

1. **Hunt grid** (`index.html:535`): replace the three hardcoded Shopify
   `<a>` cards with a JS render from `GET /api/public/items` — photo,
   title, price, "was" compare-at strike-through, **Add to cart** button.
2. **Pallet ledger** (already dynamic at `:669`): add **Add to cart** to live
   pallets (those with `publish_state='live'`); ghost/sold stay as SOLD social
   proof.
3. **Cart drawer**: localStorage cart (id + type + snapshot for display only —
   price is re-verified server-side). Slide-out with line items, remove,
   subtotal, **Checkout** button → collects email/phone → `POST /api/public/checkout`
   → `window.location = url`.
4. **New static pages**: `order-confirmed.html` (reads `?session=`, shows
   pickup instructions + order summary) and reuse cart for cancel. Styled in the
   existing loading-dock CSS.

---

## 4. Admin PWA — list-for-sale control

Mostly already there. `staff/admin.html` already PATCHes `publishState`,
`listPrice`, `salePrice` via `PalletsFunction.Update`. Additions:
- Surface a clear **"Publish for sale" → `publish_state='live'`** toggle and
  per-item sell-price confirmation so Norm/Rob flip warehouse-scanned inventory
  to purchasable without touching Shopify.
- (Optional) a simple **Orders** view reading `dbo.orders` so they see what's
  been bought and needs pickup. Could be a fast follow.

---

## 5. Security & correctness checklist

- [x] **Never trust client prices** — server re-reads from SQL in `sp_ReserveForCheckout`.
- [x] **Oversell guard** — `reserved_until` hold; qty=1 items can't double-sell.
- [x] **Webhook signature verification** — the trust boundary for "is it paid."
- [x] **Idempotent webhook** — `sp_MarkOrderPaid` safe on Stripe retries.
- [x] **Secrets in SWA app settings**, not repo (matches `SqlConnectionString`).
- [x] **Stripe test mode first** — full flow with test cards before live keys.
- [x] Public reads stay under `/api/public/*`; staff/admin stays Entra-gated.

---

## 6. Transition / fallback (Shopify stays up)

1. Build + deploy behind the scenes; Shopify dev store untouched.
2. Test the whole flow in **Stripe test mode** end-to-end.
3. Soft launch: flip the hunt grid to the SQL-backed buyable version; keep the
   Shopify links available as a fallback link if needed.
4. Once a few real orders flow cleanly and pickup works, **retire Shopify**
   (stop the $5/mo Starter + the `featured`-tag double-entry). Owners then list
   only via the scan/admin PWA.

Fee impact: **5% + 30¢ → 2.9% + 30¢**, and inventory becomes single-source
(no re-keying into Shopify).

---

## 7. Suggested build order (milestones)

1. **DB migration** (`db/stripe-checkout.sql`) — columns, tables, views, procs. Apply to `sqldb-nsl-prod`.
2. **`Stripe.net` + DI wiring** + app settings (test keys).
3. **`PublicItemsFunction`** — grid reads from SQL (no buying yet; verify render).
4. **`CheckoutFunction` + `StripeWebhookFunction`** — full test-mode purchase of one item.
5. **Cart UI** — drawer, add-to-cart, checkout, confirmation page.
6. **Pallets purchasable** — extend grid + checkout to `item_type='pallet'`.
7. **Admin publish-for-sale polish** + optional Orders view.
8. **Stripe Tax + pickup email** finalize, soft launch, then retire Shopify.

---

## Open questions for Jeff

- **Stripe account**: new Stripe account under NSL (need Norm/Rob EIN + bank for
  payouts — same blocker that gated the Shopify transfer), or Jeff's existing
  account temporarily for test mode?
- **Pickup logistics copy**: hours / address / "we'll email you when ready" —
  what should `order-confirmed.html` and the email say?
- **Tax**: Stripe Tax (auto, tiny per-tx fee) vs flat NC 4.75%+local rate. I
  recommend Stripe Tax.
- **Reservation window**: 30 min default OK?
- **Orders view**: build now or fast-follow after first real sale?
