# Preferred Reseller Program — Technical Design

**Source request:** Norm Turner email 2026-06-15 + `NSL_Reseller_Program_Guide_v2.docx`
**Author:** Jeff Blanchard (Surya Technologies)
**Status:** Design / for review with Norm + Rob
**Last updated:** 2026-06-15

---

## 1. What the program is (from the guide)

A free, no-application loyalty program for resellers. Buyers create a profile on the
website; tier and discounts unlock automatically as purchase count grows.

| Tier (NC region) | Threshold | Discount | Early-access preview |
|---|---|---|---|
| 🥈 Silver — Mountains | 3+ completed purchases | 5% | 24h before public |
| 🥇 Gold — Piedmont | 8+ completed purchases | 10% | 48h before public |
| 💎 Platinum — Coastal | 15+ completed purchases | 15% | 72h before public |

Rules: discount applies to **Mega Boxes, Standard Pallets, Bulk/Wholesale Loads**;
tier is paused (not lost) after **90 days** with no purchase; discounts apply at online
checkout automatically, or "give your name" at the warehouse.

**Two distinct capabilities are bundled in this one request:**
1. **Identity + accounts** — sign up, verify email + phone, log in, manage a profile.
2. **Loyalty engine** — track purchases, derive tier, gate early access, notify, discount.

These have different best-practice answers and should be built/owned separately.

---

## 2. What we already have (the constraints)

| Layer | Today | Reuse for resellers? |
|---|---|---|
| Hosting | Azure Static Web Apps (SWA) | ✅ same site, new `/account/*` area |
| API | .NET 8 isolated Azure Functions, Dapper, `NSL.Api` | ✅ add `AccountFunction`, `ResellerFunction` |
| DB | Azure SQL — `UNIQUEIDENTIFIER` PKs, T-SQL, `sp_*` procs + `v_*` views, `GRANT … TO nsl_api` | ✅ new tables/procs, same style |
| Auth | SWA built-in auth, **AAD provider** → staff only (`/staff/*` = `authenticated`) | ⚠️ staff tenant is wrong IdP for external resellers — see §3 |
| Storefront | Static `index.html`, anonymous `GET /api/public/pallets` → `v_public_pallets` | ✅ extend view + add authenticated reseller read path |
| Publish states | `manifests.publish_state` = draft / live / ghost / sold via `sp_SetPublishState` | ✅ early access bolts onto this — see §6 |
| Payments | **None.** Retail → Shopify links; wholesale → "CALL TO BUY" | ⚠️ auto-discount needs a reseller-aware checkout that doesn't exist yet — see §7 |
| Email/SMS | **None anywhere** | ⚠️ must add a messaging capability — see §8 |

Two things in the guide have **no foundation yet** and are the real cost drivers:
online checkout (for auto-discount) and outbound messaging (for verification + early-access alerts).

---

## 3. Identity — the central decision

Resellers are **external consumers** (flea-market vendors, eBay sellers). They must **not**
become members of the NCMB-style staff M365/Entra tenant. We need a customer-identity (CIAM)
solution. Two viable paths:

### ✅ DECIDED: Microsoft Entra External ID (CIAM) as a second SWA OIDC provider

- SWA supports a **custom OpenID Connect provider** alongside the existing staff AAD one.
  Route `/staff/*` → staff AAD (unchanged); route `/account/*` → External ID.
- Gives us, **out of the box and maintained by Microsoft**: email sign-up + verification,
  password reset, account lockout, leaked-password protection, and **phone (SMS) OTP** as a
  built-in auth method — which directly satisfies the guide's email + cell verification steps.
- **Free up to 50,000 monthly active users** — NSL will live in the free band indefinitely.
- Clean separation of concerns: **External ID owns credentials; our SQL owns program data**,
  joined on the stable subject claim (`oid`/`sub`).

**Why not roll our own auth in the API?** We could store accounts in SQL and hash passwords —
it fits the Dapper/proc pattern. But it makes *us* permanently responsible for password
hashing, reset-token security, lockout, OTP delivery, and breach response on a system owned by
**non-technical people** under a **$0-labor friends rate**. Identity is the one component where
hand-rolling is a long-term liability, not a saving. Recommend we do **not** own credentials.

> **Tradeoff accepted:** External ID is a one-time tenant setup (user flow + branding + SWA
> wiring) that I do once.

**v1 verifies email only.** Phone OTP is an External ID user-flow option we turn on later (§8);
v1 collects `cell_phone` but leaves `phone_verified_at` NULL. Everything below assumes
External ID + "our SQL keyed on the subject claim."

---

## 4. Data model (new — matches existing T-SQL conventions)

All new tables use `UNIQUEIDENTIFIER` PKs with `NEWID()`, `DATETIME2` + `SYSUTCDATETIME()`,
`VARCHAR` for enums / `NVARCHAR` for user text, and `GRANT … TO nsl_api`.

```sql
-- A reseller account. external_id = the OIDC subject claim from Entra External ID.
CREATE TABLE dbo.resellers (
    id              UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    external_id     NVARCHAR(200)    NOT NULL UNIQUE,         -- maps to identity provider
    full_name       NVARCHAR(200)    NOT NULL,
    business_name   NVARCHAR(200)    NULL,                    -- optional per guide
    email           NVARCHAR(256)    NOT NULL,
    cell_phone      VARCHAR(20)      NOT NULL,
    city            NVARCHAR(120)    NOT NULL,
    zip             VARCHAR(10)      NOT NULL,
    business_type   VARCHAR(60)      NOT NULL,                -- see guide's list
    email_verified_at DATETIME2      NULL,
    phone_verified_at DATETIME2      NULL,
    notify_email    BIT              NOT NULL DEFAULT 1,
    notify_sms      BIT              NOT NULL DEFAULT 1,
    notified_tier   VARCHAR(20)      NOT NULL DEFAULT 'none', -- last tier we congratulated
    created_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Many-to-many: a reseller selects ALL platforms they sell on.
CREATE TABLE dbo.reseller_platforms (
    reseller_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.resellers(id),
    platform    VARCHAR(40)      NOT NULL,   -- facebook_marketplace, ebay, whatnot, amazon, …
    CONSTRAINT PK_reseller_platforms PRIMARY KEY (reseller_id, platform)
);

-- Source of truth for tier progression: one row per completed purchase.
CREATE TABLE dbo.reseller_purchases (
    id           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    reseller_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.resellers(id),
    manifest_id  UNIQUEIDENTIFIER NULL REFERENCES dbo.manifests(id), -- pallet bought, if known
    channel      VARCHAR(20)      NOT NULL DEFAULT 'warehouse',      -- online | warehouse
    amount       DECIMAL(12,2)    NULL,
    discount_pct INT              NULL,    -- tier % applied at time of sale (audit)
    purchased_at DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    recorded_by  NVARCHAR(200)    NULL,    -- staff identity for warehouse sales
    notes        NVARCHAR(MAX)    NULL
);
CREATE INDEX IX_reseller_purchases_reseller ON dbo.reseller_purchases(reseller_id, purchased_at DESC);

-- Phase 3: "Schedule a warehouse appointment" dashboard action.
CREATE TABLE dbo.reseller_appointments (
    id           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    reseller_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.resellers(id),
    requested_for DATETIME2       NOT NULL,
    status       VARCHAR(20)      NOT NULL DEFAULT 'requested', -- requested | confirmed | cancelled
    notes        NVARCHAR(MAX)    NULL,
    created_at   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### Tier is **derived, not stored**

A view computes everything the dashboard needs from the purchase count + last purchase date,
so tier can never drift out of sync:

```sql
CREATE VIEW dbo.v_reseller_account AS
WITH agg AS (
    SELECT r.id,
           COUNT(p.id)        AS purchase_count,
           MAX(p.purchased_at) AS last_purchase_at
    FROM dbo.resellers r
    LEFT JOIN dbo.reseller_purchases p ON p.reseller_id = r.id
    GROUP BY r.id
)
SELECT r.*,
       a.purchase_count,
       a.last_purchase_at,
       CASE WHEN a.purchase_count >= 15 THEN 'platinum'
            WHEN a.purchase_count >= 8  THEN 'gold'
            WHEN a.purchase_count >= 3  THEN 'silver'
            ELSE 'member' END                                   AS tier,
       CASE WHEN a.purchase_count >= 15 THEN 15
            WHEN a.purchase_count >= 8  THEN 10
            WHEN a.purchase_count >= 3  THEN 5 ELSE 0 END        AS discount_pct,
       CASE WHEN a.purchase_count >= 15 THEN 0
            WHEN a.purchase_count >= 8  THEN 15 - a.purchase_count
            WHEN a.purchase_count >= 3  THEN 8  - a.purchase_count
            ELSE 3 - a.purchase_count END                       AS purchases_to_next_tier,
       CASE WHEN a.last_purchase_at IS NULL THEN 0
            WHEN a.last_purchase_at < DATEADD(DAY,-90,SYSUTCDATETIME()) THEN 0
            ELSE 1 END                                          AS is_active
FROM dbo.resellers r
JOIN agg a ON a.id = r.id;
```

Procs (same naming as `sp_CreateManifest`, `sp_SetPublishState`):
`sp_UpsertReseller`, `sp_RecordResellerPurchase`, `sp_GetResellerAccount`,
`sp_SetNotificationPrefs`, `sp_RequestAppointment`.

---

## 5. API surface (new functions, `NSL.Api`)

All reseller routes are **authenticated** (SWA injects `x-ms-client-principal`); the function
reads the subject claim and joins to `dbo.resellers.external_id` — a reseller can only ever see
their own row.

| Route | Auth | Purpose |
|---|---|---|
| `POST /api/account/profile` | reseller | First-login upsert from the sign-up form (creates `resellers` + platforms) |
| `GET  /api/account` | reseller | Dashboard payload: tier, counts, `purchases_to_next_tier`, active flag |
| `PATCH /api/account` | reseller | Update contact info / notification prefs / platforms |
| `GET  /api/account/pallets` | reseller | Inventory incl. early-access previews for this tier (§6) |
| `GET  /api/account/purchases` | reseller | Purchase history / receipts |
| `POST /api/account/appointments` | reseller | Request a warehouse appointment |
| `POST /api/staff/purchases` | staff | Record a purchase (warehouse sale) → drives tier |
| `GET  /api/staff/resellers/lookup?q=` | staff | "Give your name" warehouse lookup → shows tier + discount to apply |

`staticwebapp.config.json` additions:

```jsonc
{ "route": "/account",     "rewrite": "/.auth/login/<externalId>?post_login_redirect_uri=/account/" },
{ "route": "/account/*",   "allowedRoles": ["authenticated"] },
{ "route": "/api/account/*", "allowedRoles": ["authenticated"] }
// existing /staff/* and /api/* rules unchanged
```

---

## 6. Early access — bolts onto the existing publish-state machine

The guide's early access = "resellers see/reserve a load before the public, by tier window."
We do **not** need a new publish state. Add a single timestamp:

```sql
ALTER TABLE dbo.manifests ADD public_at DATETIME2 NULL;  -- when a LIVE pallet opens to the public
```

**Mechanics:**
- Staff set a pallet `live` *and* set `public_at` to a future time (e.g. "public Friday 9am").
- Tier window before `public_at`: Platinum −72h, Gold −48h, Silver −24h.
- **Public** read path (`v_public_pallets`) gains `AND (public_at IS NULL OR public_at <= SYSUTCDATETIME())`
  → public can't see a pallet still in its preview window. `NULL` = legacy/immediate (backward compatible).
- **Reseller** read path `sp_GetResellerPallets @reseller_id` returns pallets where
  `public_at <= DATEADD(HOUR, tier_window, SYSUTCDATETIME())`, each flagged `early_access`
  with a countdown to public — that's the badge in the dashboard.

This reuses `sp_SetPublishState`, `is_ghost`, `sold_at` untouched; it's purely additive.
"Reserve an item" is a later enhancement (a `reseller_reservations` table or, for MVP,
a "call to reserve" CTA on previewed pallets).

---

## 7. Discounts — honest scoping

"Discounts apply automatically at online checkout" presumes an **online checkout that does not
exist** (retail is Shopify links; wholesale is phone). This intersects directly with the
**Stripe-vs-Shopify decision** already in flight (memory: leaning Stripe, custom storefront).

Realistic application of the tier discount, in order of what's buildable now:

1. **Warehouse (works in Phase 1):** staff lookup (`/api/staff/resellers/lookup`) shows the
   reseller's tier + % to apply at the register. Records the sale, which advances the tier.
2. **Dashboard display (Phase 2):** reseller's `/api/account/pallets` shows **their**
   tier-discounted ask price, so the value is visible even before a real cart exists.
3. **Automatic online checkout (Phase 3):** when the Stripe reseller-aware checkout lands, the
   discount is applied **server-side** from `v_reseller_account.discount_pct` — never trust a
   client-sent percentage. Until then, online auto-discount is explicitly out of scope.

> Recommend telling Norm/Rob: accounts + tiers + early access ship first; *fully automatic*
> online checkout discounting rides on the Stripe storefront decision and lands with it.

---

## 8. Notifications + verification (messaging)

Required: email + SMS for (a) sign-up verification and (b) early-access alerts. We have neither.

### ✅ DECIDED: email-only for v1; SMS deferred

- **Email (v1)** → **Microsoft Graph `sendMail`** from an existing M365 mailbox/alias
  (e.g. `alerts@northstateliquidators.com`). They already pay for M365 → effectively $0 marginal.
  Carries both verification (via External ID) and early-access alerts in v1.
- **SMS (later)** → added once worthwhile: **Azure Communication Services (ACS)** toll-free
  number for early-access texts, plus turning on External ID **phone OTP** at sign-up. Deferred
  specifically so the **~2–4 week toll-free carrier registration does not gate launch.**

> ⚠️ **When SMS is added (third-party pass-throughs to flag for Norm/Rob then):**
> - **Toll-free registration** lead time **~2–4 weeks** — start it before we want SMS live.
> - SMS billed **per message segment**; small but real monthly cost. Email stays free via M365.
> - These are the only new out-of-pocket costs; under the friends rate my labor stays $0.

Early-access alerts run as a **timer-triggered Function**: each tier-window crossing for a
pallet with a future `public_at` enqueues **email** to opted-in resellers of that tier (SMS
joins the same path later). Until then the `notify_sms` preference is collected but dormant.

---

## 9. Coverage check vs. the guide

| Guide requirement | Where handled |
|---|---|
| Sign-up access point / "Create Reseller Account" | §3 External ID flow + `/account` + `POST /api/account/profile` |
| Fields: name, business, email, cell, city/zip, platforms, business type | §4 `resellers` + `reseller_platforms` |
| Email verification link | Entra External ID user flow |
| Cell phone SMS OTP | Entra External ID user flow |
| Secure password + forgot-password | Entra External ID (we never store passwords) |
| Dashboard: tier, purchase count, to-next-tier | §4 `v_reseller_account` → `GET /api/account` |
| Dashboard: browse inventory w/ early-access indicator | §6 `/api/account/pallets` |
| Dashboard: purchase history / receipts | §4 `reseller_purchases` → `/api/account/purchases` |
| Dashboard: update contact / notify prefs / platforms | §5 `PATCH /api/account` |
| Dashboard: schedule warehouse appointment | §4 `reseller_appointments` (Phase 3) |
| Auto tier progression (3/8/15) | §4 derived view |
| 90-day activity pause | §4 `is_active` |
| Early access 24/48/72h by tier | §6 `public_at` + tier window |
| Notify resellers first via text/email | §8 timer Function + ACS/Graph |
| Discount at online checkout | §7 (Phase 3, rides Stripe decision) |
| Discount at warehouse ("give your name") | §7 staff lookup (Phase 1) |

---

## 10. Phased delivery

**Phase 1 — Accounts + tiers (the spine).**
External ID wired into SWA; sign-up form + profile upsert; dashboard (tier, counts, edit
profile); staff purchase-recording + warehouse lookup with discount. **Email verification via
External ID; phone collected but unverified; no SMS.**

**Phase 2 — Early access + email notifications.**
`public_at` + tier-window read paths; dashboard early-access badges; **Graph email alerts**.
SMS (ACS toll-free + External ID phone OTP) added in a later pass once registration clears.

**Phase 3 — Checkout discount + extras.**
Server-side tier discount in the Stripe storefront (rides the storefront decision);
appointment scheduling; optional reserve-a-pallet.

---

## 11. Open decisions (for Jeff / for Norm + Rob)

1. ~~**[Jeff]** Entra External ID vs. roll-your-own auth.~~ **✅ Entra External ID.**
2. ~~**[Jeff]** SMS vendor.~~ **✅ Email-only v1; SMS (ACS) deferred so toll-free lead time doesn't gate launch.**
3. **[Norm/Rob]** Accept that automatic *online* discounting waits for the Stripe storefront;
   warehouse discounting + early access ship first.
4. **[Norm/Rob]** What counts as a "completed purchase" for tier math — any sale, or only
   pallets/boxes above some value? (Affects `sp_RecordResellerPurchase`.)
5. **[Norm/Rob]** "Reserve an item" in the preview window — real hold, or "call to reserve" for v1?
6. **[Norm/Rob]** Early-access alerts email-only at launch — OK? (SMS texts come in a later pass.)
7. **[Norm/Rob]** Who records warehouse purchases (which advance tiers), and how do they ID the
   reseller at the register — name lookup enough, or do we need a card/QR?
8. **[Norm/Rob]** "Schedule a warehouse appointment" — real booking calendar, or a request form
   that emails the team for v1?
```
