# Square Payments Integration — Design

**Status:** Design complete 2026-08-28 — verified against developer.squareup.com
(sources at bottom). Awaiting credentials (Square Developer Console app) to
begin the sandbox build.
**Context:** Rob's 8/25 NSL Update asked for Square as the site's payment
processor (NSL's existing in-person processor). Supersedes the May "leaning
Stripe" plan — only the processor changes; the Shopify exit and custom
storefront on the SWA stand.

---

## What we're selling (shapes the whole design)

One-of-a-kind boxes. Each live pallet/box is a unique product with one ask
price (`v_public_pallets.ask_price` = sale_price ?? list_price ?? wholesale
roll-up). Once sold it's gone. No cart, no quantities, no recurring SKUs.

**Chosen approach: Square Checkout API "Quick Pay" payment links.**
- One server-side REST call creates a Square-hosted checkout page for the box
  (ad-hoc name + price — **no Square Catalog sync**, which would recreate the
  Shopify mismatch we left).
- Zero front-end payment code, zero PCI burden on us (hosted page), and
  Apple Pay / Google Pay / Cash App Pay / Afterpay work out of the box.
- Web sales land in the same Square account as floor sales — one ledger,
  one deposit stream, the reporting Rob already trusts.
- Fees: **2.9% + 30¢** per online API payment (post Jan-2026 pricing), no
  monthly cost. (Verify the first live sale lands at the API rate, not the
  3.3% "Square Online free plan" rate — separate line on Square's fee page.)

The embedded alternative (Web Payments SDK + Payments API) buys full UI
control at the cost of front-end payment code, CSP/secure-context
requirements, and per-wallet integrations — not worth it for this catalog.

## Architecture

```
index.html (public)          api/ (.NET 8 isolated Functions)         Square
─────────────────           ────────────────────────────────         ──────
[Buy this box] ──────POST──▶ /api/public/checkout/{palletId}
                             - box live + unsold?  else 409
                             - existing link? return it ──────────┐
                             - else POST /v2/online-checkout/     │
                               payment-links (quick_pay,          ├─────▶ hosted
                               idempotency_key nsl-{id}-v1)       │       checkout
                             - store link id + order_id on box    │       page
     ◀──────── { url } ── redirect browser ◀──────────────────────┘
                                                                      buyer pays
                             /api/square/webhook  ◀──────POST──────── payment.updated
                             - verify x-square-hmacsha256-signature   (status=COMPLETED)
                             - dedupe on event_id
                             - match data.object.payment.order_id
                               to the box's stored order_id
                             - EXEC sp_SetPublishState 'sold'
                             - insert dbo.payments row
```

### Key mechanics (verified against Square docs)

- **Create link:** `POST /v2/online-checkout/payment-links` with
  `quick_pay: { name, price_money: { amount (cents), currency: USD },
  location_id }`, `checkout_options.redirect_url` →
  `/thanks.html?box={pallet_number}`, and an **idempotency key** derived from
  the box (`nsl-{manifest_id}-link-v1`) so a double-clicked Buy button can
  never mint two links. Response carries `payment_link.id`, **`order_id`**
  (the correlation key), and the `https://square.link/u/…` URL.
- **One link per checkout attempt, not one link per box** (see "Cart model
  (2026-09)" below — this superseded the original one-box-per-link design).
  Two shoppers whose carts overlap on a box both get a link; whoever pays
  first wins and the other's link is DB-canceled. No inventory "hold" state
  needed — a box stays live until money clears, so an abandoned checkout
  never hides a box from other buyers.
- **Paid signal = webhook**, not the redirect. Subscribe to
  **`payment.updated`**; act when `payment.status == "COMPLETED"`, matching
  `data.object.payment.order_id` to the stored order_id. Verify the
  **`x-square-hmacsha256-signature`** header (HMAC-SHA256 over notification
  URL + raw body, keyed with the subscription's Signature Key) — the official
  `Square` .NET SDK ships `WebhooksHelper.VerifySignature` for exactly this.
  Square retries failed deliveries up to ~11 times over 24h, so the handler
  is idempotent: unique index on `square_payment_id`, dedupe on `event_id`,
  already-sold ⇒ 200 no-op.
- **Redirect is UX only.** Production appends `orderId` to the redirect URL;
  **sandbox appends nothing** (documented limitation). The thanks page thanks
  the buyer and says "we'll contact you" — it never decides sold-ness.
- **Polling fallback / reconciliation:** `GET /v2/orders/{order_id}`.
  GOTCHA: paid payment-link orders go `DRAFT → OPEN` and **stay OPEN
  forever** — "paid" = `tenders[]` present / `net_amount_due_money == 0`,
  NEVER `state == "COMPLETED"`. Reconcile sweeps open orders on this basis:
  paid-but-not-sold ⇒ heal it; box archived/pulled or the link aged out ⇒
  `DELETE /v2/online-checkout/payment-links/{id}` (cancels the order). It is
  NOT a timer — see "Cart model (2026-09)" below for how it actually runs.
- **The narrow race** (payment completing mid-delete, or a second payment
  slipping through): webhook handler tolerates a COMPLETED payment for an
  already-sold/canceled box by flagging it for refund
  (`POST /v2/refunds`) — surfaced in admin, one click, rare.
- **Shared account:** the webhook fires for floor POS sales too. Only payments
  with `application_details.square_product` ECOMMERCE_API or INVOICES are
  ours; unmatched payments from other products are ignored (2026-09-13 hotfix).

### Cart model (2026-09)

The site sells a cart of boxes as one Square order per checkout attempt, not
one link per box. What a maintainer needs to know before touching any of
this:

- **One checkout attempt = one `dbo.checkout_orders` row** (subtotal, tax,
  delivery, total, delivery method/zip/address, status) **+ one
  `dbo.checkout_order_boxes` row per box** in it (that box's own price, its
  own tax, and an `outcome` of `sold`/`unavailable` once fulfilled). This
  retires the `manifests.checkout_link_id` / `checkout_order_id` /
  `checkout_url` / `checkout_created_at` columns this document used to
  describe — they no longer exist.
- **Fulfilment is one routine, one transaction, two callers.**
  `CheckoutFulfillment.FulfillOrderAsync` (`api/Services/CheckoutFulfillment.cs`)
  is called by both the webhook and Reconcile, so there is only one copy of
  the sold-or-refund decision to keep correct. Everything from the
  `payments` INSERT (the idempotency anchor) through the per-box row-locked
  availability check to the competing-link cancel runs inside one
  `SqlTransaction` — a crash mid-way rolls the anchor back and Square's own
  retry redoes the whole thing safely.
- **The database, not Square's delete response, is the source of truth for
  "this link is dead."** Square has acknowledged `DELETE
  /v2/online-checkout/payment-links/{id}` can return success without
  actually cancelling the link. So every cancel — a sale, a price change, a
  box pulled from live, an invoice, a fake-sale, a delete — writes
  `checkout_orders.status = 'canceled'` first, inside the same transaction
  as whatever caused it (`CheckoutFulfillment.CancelOpenLinksForBoxesAsync`).
  Only after that commits does the code best-effort ask Square to delete the
  link (`RetireLinksAsync`); if Square can't confirm it, Reconcile keeps
  retrying. **Do not reorder this to call Square first** — that is exactly
  the ordering that makes the unreliable delete dangerous.
- **An order line deliberately outlives the box it names.**
  `checkout_order_boxes.manifest_id` carries no foreign key to `manifests`,
  on purpose: a box can be hard-deleted or pulled after its link was minted,
  and the row has to survive that so fulfilment's `LEFT JOIN` can still see
  it, add the box's price *and* tax to `refund_due_cents` (always
  tax-inclusive), and flag the payment. **An orphaned
  `checkout_order_boxes` row is a debt owed to a customer, not garbage —
  never add the FK back, never write a job that cleans these up.**
- **Reconcile treats a Square "not found" as evidence only once something
  else in the same run proves the credential reaches our own merchant** (one
  order of ours reading back). Otherwise a rotated/misconfigured access
  token would read every order as "not found," and the sweep would close out
  — and stop ever looking at again — orders that are in reality still open
  and paid at the real merchant. See `SquareFunction.SquareAnswered` /
  `LooksLikeWrongMerchant`.
- **Tax and the delivery fee are never revenue or margin.** The 7.25% NC +
  Wake County tax is sent as an `ADDITIVE`, `LINE_ITEM`-scope tax (via the
  account's own Square catalog tax object when `SQUARE_TAX_CATALOG_ID` is
  set, so the buyer's receipt and Rob's Square dashboard both read "NC &
  Wake County Sales Tax" — unset or wrong, it silently falls back to an
  ad-hoc tax with a different name but the same amount). A delivery order
  also carries a per-zip (`dbo.delivery_zips.fee_cents`, $10 default), taxed
  `service_charges[]` line — never `checkout_options.shipping_fee`, which
  Square marks non-taxable. `SalesSummary` reports **goods only**; keep tax
  and delivery out of any revenue/margin figure.
- **Reconcile has two triggers and only one is dependable.** SWA-managed
  Functions have no timer trigger, so the "sweep open links hourly" line
  this document used to have was never true of the cart build. The staff
  Reconcile button on `staff/sales.html` (`POST /api/square-reconcile`) is
  the path to trust. A GitHub Actions workflow
  (`.github/workflows/square-reconcile.yml`) also calls a keyed route
  (`POST /api/public/reconcile-tick`, header `x-nsl-cron-key` =
  `RECONCILE_CRON_KEY`) every 15 minutes as a convenience — but **GitHub
  disables a schedule automatically after 60 days with no repository
  activity**, so it is not something to depend on staying alive unattended.
  Either way, an open link is force-closed after 7 days
  (`ReconcileLinkMaxAgeDays`).
- **This Square account has no sandbox.** All verification — including the
  production go-live run for the cart — happens against the live account,
  with disposable, low-priced test boxes that get refunded immediately after
  each paid scenario. Square does not reliably return the 2.9% + 30¢
  processing fee on a refund, so budget roughly a dollar in fees per paid
  test scenario.

### New pieces

| Piece | What it does |
|---|---|
| NuGet **`Square`** (v46+) | Official SDK — post-v41 rewrite; avoid `Square.Legacy` / old `Square.Connect` samples. Covers the API calls + webhook signature helper. |
| `SquareService` (api/Services) | Wraps create/retrieve/delete payment link + retrieve order. Sandbox/production base URL from config. |
| `SquareFunction.CreateCheckout` | `POST /api/public/checkout` — anonymous; body `{ids, delivery, zip, address, memberNumber}`; validates every box in the cart, reuses an identical open link, else mints one Square order covering the whole cart. |
| `SquareWebhookFunction` | `POST /api/square/webhook` — anonymous route; Square's signature IS the auth. Delegates the sold/refund decision to `CheckoutFulfillment.FulfillOrderAsync`. |
| `SquareFunction.Reconcile` / `ReconcileTick` | `POST /api/square-reconcile` (staff button) and `POST /api/public/reconcile-tick` (keyed cron) run the same sweep — heal missed webhooks, close dead links, retry unconfirmed deletes, replay orphaned refunds. See "Cart model (2026-09)" above. |
| `dbo.payments` table | square_payment_id (UNIQUE), square_order_id, manifest_id, amount, status, refund_due_cents, refunded_cents, needs_refund, event JSON, created_at. |
| `dbo.checkout_orders` / `dbo.checkout_order_boxes` | One row per checkout attempt, one row per box in it. See "Cart model (2026-09)" above. |
| Buy button + `/thanks.html` | Add-to-cart on live boxes (homepage row + manifest modal), header badge, phone bottom bar, drawer with delivery choice. Thanks page: lists every box bought. |

### Config (SWA application settings on `stapp-nsl-website` — never committed)

`SQUARE_ACCESS_TOKEN` · `SQUARE_WEBHOOK_SIGNATURE_KEY` ·
`SQUARE_ENVIRONMENT` (`sandbox`|`production`, picks
connect.squareupsandbox.com vs connect.squareup.com) · `SQUARE_LOCATION_ID`.

Personal access token (per environment) is correct for a single-merchant
integration — OAuth is for multi-seller platforms. Tokens live server-side
only; the browser never talks to Square except on Square's own pages.

### What stays manual in v1 (deliberately)

- **Refunds / disputes** — Square Dashboard, same as the floor.
- **Pickup/shipping coordination** — buyer contact info comes from the Square
  order/receipt; NSL reaches out. No shipping rates at launch.
- **Wholesale / reseller pricing** — unchanged, inquiry-based.

### Rollout plan

1. **Console setup (Jeff, ~5 min):** create the app in the Square Developer
   Console (none exists yet) → collect sandbox + production access tokens,
   application ID, sandbox + production location IDs. Creds go straight into
   SWA app settings, pointed at **sandbox**.
2. **Build:** payments migration, SquareService, the three Functions, Buy
   button + thanks page.
3. **Sandbox end-to-end:** sandbox webhook subscription → buy with test card
   `4111 1111 1111 1111` (CVV 111) → webhook fires (sandbox webhooks DO
   work) → box flips SOLD on the site. Remember sandbox won't append
   redirect params and payment links are only partially supported there.
4. **Production cutover:** production webhook subscription (its own
   signature key), flip the four settings, **$1 live test box** with a real
   card, confirm the fee rate on the deposit, refund it.
5. Buy buttons visible on all live boxes. Done.

### Open decisions for Rob/Norm

- Every live box buyable online, or an explicit "sellable online" flag?
  (Proposal: every live box — that's what "live" means.)
- Pickup-only at launch (proposal: yes; shipping later).
- Who watches for the rare double-payment refund flag (proposal: whoever
  already does the Square Dashboard).

---

## Sources (verified 2026-08-28)

Square docs: online-payment-options · checkout-api (quick-pay, manage,
common-pitfalls) · reference/checkout-api/create-payment-link,
delete-payment-link, webhooks · webhooks/overview, step3validate ·
payments-api/webhooks · reference/webhooks/payment.updated ·
reference/objects/Order · orders-api/retrieve-order ·
build-basics/access-tokens, idempotency · devtools/sandbox (overview,
payments) · web-payments/overview · sdks/dotnet (+ migration) ·
squareup.com/us/en/payments/our-fees. Forum threads confirming: redirect
params production-only (#20871), delete-link race (#21874), payment.updated
as the paid signal (#22282), links never expire (community #384704).
