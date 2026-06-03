# On-Site Checkout (Square) — Build Plan

**Status:** Plan for review — no code written yet
**Author:** Jeff (with Claude Code)
**Date:** 2026-06-03
**Companion to:** `docs/STRIPE-CHECKOUT-BUILD.md` (same goal, Stripe processor)

## Goal

Make **everything purchasable directly on northstateliquidators.com** — both single
"treasure hunt" retail items *and* pallets — instead of redirecting to the
password-gated Shopify dev store. Move the selling layer onto the Azure stack we
already run, with **Square** as the payment processor.

### Why Square (decision context)

- **Rob already uses Square.** The payouts blocker — business EIN + bank account
  for settlement — that gates a fresh Stripe account *and* the Shopify merchant
  transfer is most likely **already cleared** on Rob's existing Square account.
  ⇒ **Confirm we build on that account** (we need its Application ID + access
  token + webhook signature key; sandbox first).
- **NSL has a physical retail floor + warehouse pickup.** Square is built for
  brick-and-mortar: free POS app, cheap reader, lower card-present rates
  (~2.6% + 10¢ in person vs ~2.9% + 30¢ online — same online rate as Stripe).
  One Square account can run the **in-store register and the website on one
  payout and one set of reports**. Stripe has no walk-up register story.
- Trade-off vs Stripe: Square has **no built-in automatic sales-tax engine**
  (Stripe Tax). For **pickup-only at one NC location** a flat NC rate is correct
  and trivial, so this costs us nothing at launch.

### Decisions locked (carried from the Stripe plan)

| Decision | Choice |
|---|---|
| Checkout depth | **Full cart + combined checkout** (not per-item Buy Now) |
| Fulfillment at launch | **Local pickup only** — no shipping labels, no carrier rates |
| Shopify | **Keep running as fallback** during transition; cut over once proven |
| Scope | **Everything purchasable** — retail items *and* pallets |
| Payments | **Square** — build on Rob's existing account |

### Integration style — two options

| | A. Payment Links (hosted) | B. Web Payments SDK (on-site) |
|---|---|---|
| Flow | Redirect to a Square-hosted page, back to us on success | Card form embedded in our cart drawer; no redirect |
| Maps to | Stripe Checkout Session (our Stripe plan's exact flow) | Stripe Elements + PaymentIntents |
| Effort | **Lowest** — one server call, no client card handling, no PCI surface | More frontend; card tokenized client-side, charged server-side |
| Recommendation | **Start here for MVP** (1:1 with the Stripe plan) | Upgrade later for a stay-on-site UX |

This doc specs **Option A (Payment Links)** as the build, and notes the Option B
delta where it matters.

### Why this is a small build, not a rebuild

Identical to the Stripe plan — the storefront backbone already exists:

- `GET /api/public/pallets` is already anonymous and returns `ask_price`,
  `list_price`, `sale_price`, `is_sold`, `is_on_sale`, `publish_state` from
  `v_public_pallets`; the homepage ledger already renders it.
- `staticwebapp.config.json` already separates `/api/public/*` (anonymous) from
  `/api/*` (Entra-gated staff) — customer endpoints have a safe home.
- `publish_state` (`draft|live|ghost|sold`) + `sp_SetPublishState` already model
  "what's for sale" / "mark sold" and keep `is_ghost` / `sold_at` / `line_items`
  consistent. Reused, not reinvented.
- DI, Dapper, `SqlService`, `BlobService` (SAS photo signing) — new Functions
  slot straight in.

What's missing is the same list as the Stripe plan — a **single-item** public
read, a **reservation hold**, an **orders** record, the **Square checkout +
webhook** Functions, and a **cart UI**. **Everything except the payment
Functions is byte-for-byte the Stripe plan.**

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
  • sp_ReserveForCheckout: holds each item/pallet 30 min, writes orders row (pending)
  • Square: CreateOrder (line items + NC tax) then CreatePaymentLink(order_id,
    checkout_options.redirect_url, ask_for_shipping_address=false)
  • stores square_order_id + square_payment_link_id on our order
  • returns payment_link.url  →  browser redirects to Square-hosted page
  ▼
Square hosted checkout  →  redirect_url back to our site (/order-confirmed)
  ▼
POST /api/public/square-webhook  (NEW, anonymous, HMAC-verified)
  • payment.updated (status=COMPLETED) → match by payment.order_id → sp_MarkOrderPaid:
      - single items  → line_items.sold_at = now
      - pallets        → sp_SetPublishState(manifest, 'sold')
      - order.status   → 'paid'
      - send pickup-confirmation email (M365 / Graph)
  • order expiry / no payment → sp_ReleaseExpiredReservations frees the hold
```

> Note: the success `redirect_url` is a UX convenience only. **The trust
> boundary is the HMAC-verified webhook**, never the redirect — same rule as the
> Stripe plan.

---

## 1. Database migration — `db/square-checkout.sql`

Idempotent, applied after `wishlist2-part2.sql`. Identical to the Stripe plan's
`stripe-checkout.sql` **except the order's processor columns**. (If the Stripe
migration already ran, this is just an `ALTER` to add the Square columns — keep
both, the processor is a column, not a schema fork.)

### Reservation holds (unchanged from Stripe plan)

```sql
ALTER TABLE dbo.line_items ADD reserved_until DATETIME2 NULL;
ALTER TABLE dbo.manifests  ADD reserved_until DATETIME2 NULL;
```

Available = `sold_at IS NULL` AND (`reserved_until IS NULL OR reserved_until <
SYSUTCDATETIME()`). Lazy expiry — no background job needed for MVP.

### Orders tables — Square-keyed columns

```sql
CREATE TABLE dbo.orders (
    id                     UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    square_order_id        VARCHAR(255) NULL,   -- set on CreateOrder
    square_payment_link_id VARCHAR(255) NULL,   -- set on CreatePaymentLink
    square_payment_id      VARCHAR(255) NULL,   -- set on webhook (payment.updated)
    status                 VARCHAR(20) NOT NULL DEFAULT 'pending', -- pending|paid|picked_up|cancelled
    customer_email         NVARCHAR(255) NULL,
    customer_phone         NVARCHAR(50)  NULL,
    customer_name          NVARCHAR(200) NULL,
    subtotal               DECIMAL(12,2) NULL,
    tax                    DECIMAL(12,2) NULL,
    total                  DECIMAL(12,2) NULL,
    pickup_notes           NVARCHAR(500) NULL,
    created_at             DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    paid_at                DATETIME2 NULL
);

CREATE TABLE dbo.order_items (
    id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    order_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.orders(id),
    item_type     VARCHAR(10) NOT NULL,          -- 'item' | 'pallet'
    line_item_id  UNIQUEIDENTIFIER NULL,
    manifest_id   UNIQUEIDENTIFIER NULL,
    title         NVARCHAR(300) NULL,            -- snapshot at purchase time
    unit_price    DECIMAL(12,2) NOT NULL,        -- snapshot (server-trusted)
    qty           INT NOT NULL DEFAULT 1
);
```

### View `v_public_items` + procs — **unchanged from the Stripe plan**

`v_public_items` (single, unsold, unreserved items on a `live` pallet with a
sell price + photo) and the three procs are processor-agnostic:

- **`sp_ReserveForCheckout(@order_id, @items_json)`** — transactional; verifies
  availability (else raise → checkout returns 409), sets 30-min `reserved_until`,
  inserts `order_items` at **server-side** prices, inserts the parent `orders`
  row, returns trusted lines + subtotal.
- **`sp_MarkOrderPaid(@square_order_id, @square_payment_id)`** — idempotent
  (safe on webhook retries); marks items `sold_at`, pallets via
  `sp_SetPublishState 'sold'`, order `paid` + `paid_at`. **Only the lookup key
  changes** (Square order id instead of Stripe session id).
- **`sp_ReleaseExpiredReservations()`** — clears expired holds, flips stale
  `pending` orders to `cancelled`; called lazily at the top of public reads.

---

## 2. API — three new Functions (`api/Functions/`)

Stack note: add the **`Square`** NuGet package (new SDK; **v43.x** at time of
writing — the modern `using Square; new SquareClient(...)` client, *not* the
deprecated `Square.Legacy`). Register a typed `SquareClient` in `Program.cs`.
`SquareAccessToken`, `SquareLocationId`, `SquareWebhookSignatureKey`, and
`SquareEnvironment` (`Sandbox`/`Production`) live in **SWA app settings**
(never in repo), alongside `SqlConnectionString`.

### `PublicItemsFunction.cs` — `GET /api/public/items`
Anonymous read of `v_public_items`, photos SAS-signed via the existing
`SignRowPhotos` pattern. Powers the "This week's hunt" grid. **Identical to the
Stripe plan** (no Square code here).

### `CheckoutFunction.cs` — `POST /api/public/checkout`
Body: `{ items: [{type:'item'|'pallet', id}], customer:{email,phone,name} }`.
1. `sp_ReserveForCheckout` → trusted line items + subtotal (**409** if anything
   was already taken — UI shows "that item just sold, removed from cart").
2. **Square `Orders.CreateOrder`**: `LocationId`, one `OrderLineItem` per
   reserved item (name + `BasePriceMoney` in **cents**, `Quantity`), plus a
   single NC sales-tax `OrderLineItemTax` (flat percentage, pickup location).
   Set `ReferenceId` = our `orders.id` for cross-matching. Pass an
   **`IdempotencyKey`** = our order id.
3. **Square `Checkout.CreatePaymentLink`** with `order_id` from step 2 and
   `CheckoutOptions { RedirectUrl = "/order-confirmed", AskForShippingAddress =
   false }` (pickup only), `PrePopulatedData` = customer email/phone.
4. Persist `square_order_id` + `square_payment_link_id`; return
   `{ url: paymentLink.Url }`. Browser redirects.

> **Option B (on-site) delta:** skip the payment link. Client uses the **Web
> Payments SDK** (`Square.payments(appId, locationId)`) to tokenize the card to a
> `token`; POST it here; call **`Payments.CreatePayment`** with `SourceId=token`,
> `AmountMoney`, `IdempotencyKey`, `OrderId`. No redirect; confirm in-page.

### `SquareWebhookFunction.cs` — `POST /api/public/square-webhook`
Anonymous but **verifies the `x-square-hmacsha256-signature` header** using the
SDK helper — the security boundary:

```csharp
var sig = req.Headers["x-square-hmacsha256-signature"].ToString();
var body = await new StreamReader(req.Body).ReadToEndAsync();
if (!WebhooksHelper.VerifySignature(body, sig, signatureKey, notificationUrl))
    return Unauthorized();   // not from Square — reject
```

- `payment.updated` with `payment.status == "COMPLETED"` → look up our order by
  `payment.order_id` → `sp_MarkOrderPaid` + send pickup email.
- (Optionally also handle `order.updated` → state `COMPLETED` as a backstop.)
- **Idempotent**: Square retries; `sp_MarkOrderPaid` no-ops if already paid.

`/api/public/square-webhook` is already covered by the existing `/api/public/*`
anonymous route — no `staticwebapp.config.json` change needed.

---

## 3. Frontend — cart + checkout (`index.html` + small JS)

**Identical to the Stripe plan** (Option A redirects to Square instead of
Stripe — the cart code doesn't care which URL it sends the browser to):

1. **Hunt grid**: replace the three hardcoded Shopify `<a>` cards with a JS
   render from `GET /api/public/items` — photo, title, price, "was"
   strike-through, **Add to cart**.
2. **Pallet ledger** (already dynamic): add **Add to cart** to `live` pallets;
   ghost/sold stay as SOLD social proof.
3. **Cart drawer**: localStorage cart (id + type + display snapshot; price
   re-verified server-side). Subtotal, remove, **Checkout** → collect
   email/phone → `POST /api/public/checkout` → `window.location = url`.
4. **New static pages**: `order-confirmed.html` (reads the redirect, shows
   pickup instructions + order summary), reuse cart for cancel. Existing
   loading-dock CSS.

> Option B only: load the Square **Web Payments SDK** script and render the card
> field inside the cart drawer; everything else is the same.

---

## 4. Admin PWA — list-for-sale control

Unchanged from the Stripe plan. `staff/admin.html` already PATCHes
`publishState`/`listPrice`/`salePrice`. Add a clear **"Publish for sale" →
`publish_state='live'`** toggle + per-item sell-price confirm, and (fast-follow)
a simple **Orders** view reading `dbo.orders` for pickup fulfillment.

---

## 5. Security & correctness checklist

- [x] **Never trust client prices** — server re-reads from SQL in `sp_ReserveForCheckout`.
- [x] **Oversell guard** — `reserved_until` hold; qty=1 items can't double-sell.
- [x] **Webhook signature verification** — `WebhooksHelper.VerifySignature` is the trust boundary for "is it paid."
- [x] **Idempotent** — `IdempotencyKey` on every Square create call; `sp_MarkOrderPaid` no-ops on retry.
- [x] **Secrets in SWA app settings**, not repo (matches `SqlConnectionString`).
- [x] **Square Sandbox first** — full flow with sandbox cards before production token.
- [x] Public reads stay under `/api/public/*`; staff/admin stays Entra-gated.

---

## 6. Transition / fallback (Shopify stays up)

1. Build + deploy behind the scenes against **Square Sandbox**; Shopify dev
   store untouched.
2. Test the whole flow in Sandbox end-to-end (item + pallet, expiry/release,
   webhook paid).
3. Soft launch: flip the hunt grid to the SQL-backed buyable version; keep the
   Shopify links as a fallback.
4. Once real orders flow cleanly and pickup works, **retire Shopify** (stop the
   $5/mo Starter + the `featured`-tag double-entry). Owners list only via the
   scan/admin PWA.

Fee impact: **5% + 30¢ (Shopify Starter) → ~2.9% + 30¢ online** (and ~2.6% + 10¢
for the same goods sold in person on the register), inventory single-source.

---

## 7. Suggested build order (milestones)

1. **Confirm the Square account + credentials** (Rob's account; Sandbox app first) — App ID, access token, Location ID, webhook signature key.
2. **DB migration** (`db/square-checkout.sql`) — columns, tables, views, procs. Apply to `sqldb-nsl-prod`.
3. **`Square` SDK + DI wiring** + SWA app settings (sandbox).
4. **`PublicItemsFunction`** — grid reads from SQL (no buying yet; verify render).
5. **`CheckoutFunction` + `SquareWebhookFunction`** — full sandbox purchase of one item via Payment Link.
6. **Cart UI** — drawer, add-to-cart, checkout, confirmation page.
7. **Pallets purchasable** — extend grid + checkout to `item_type='pallet'`.
8. **NC tax line + pickup email** finalize, soft launch, then retire Shopify.
9. *(Optional later)* **Option B** Web Payments SDK for stay-on-site checkout; **POS** — sell the retail floor on the same Square account.

---

## Open questions for Jeff / Rob

- **Which Square account / business entity** do we build on — Rob's existing
  one? Is it already on NSL's EIN + bank for payouts, or Rob's personal? (This
  determines whether the payouts blocker is truly cleared.)
- **Hosted (Payment Links) vs on-site (Web Payments SDK)** for launch — I
  recommend Payment Links for MVP, Web Payments SDK as a fast-follow.
- **POS too?** Do Norm/Rob want the retail floor ringing up on the same Square
  account now, or just the website first?
- **Pickup logistics copy** — hours / address / "we'll email when ready" for
  `order-confirmed.html` and the email.
- **NC tax rate** — flat combined state+county rate at the warehouse location;
  confirm the exact figure.
- **Reservation window** — 30 min default OK?
- **Orders view** — build now or fast-follow after first real sale?
```