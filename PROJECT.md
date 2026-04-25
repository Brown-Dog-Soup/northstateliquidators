# North State Liquidators — Website Project

**Owner:** Jeff Blanchard
**Status:** Kickoff
**Working dir:** `C:\repo\ElHeffe\Powershell\North State Liquidators`
**Started:** 2026-04-17
**Site type:** **E-commerce** — must sell directly (wholesale pallets AND retail items)

---

## Overview

Website build-out for **North State Liquidators**. Collaborative project with friends.

---

## Team

### Client — North State Liquidators

| Name | Role | Contact |
|---|---|---|
| Norman Turner | Owner & Operator | Norman.Northstateliq@gmail.com |
| Rob TeCarr | Owner & Operator | rob.northstateliq@gmail.com |
| Main line | | (919) 526-0112 |

### Build team

| Name | Role | Contact |
|---|---|---|
| Jeff Blanchard | — | jeffrey.blanchard@gmail.com |
| _TBD_ | | |

---

## Business / Client Info

| Item | Value |
|---|---|
| Business name | North State Liquidators |
| Model | Wholesale + retail liquidation |
| Inventory sources | Amazon, big-box retailers — overstock, shelf-pulls, customer returns |
| Inventory formats | Palletized and truckload; also individual retail items |
| Categories | Electronics, home goods, apparel, seasonal, general merchandise |
| Location | **Raleigh, North Carolina** |
| Phone | (919) 526-0112 |
| Owners | Norman Turner & Rob TeCarr |
| Existing site? | _TBD_ |
| Domain owned? | _TBD_ |

### Business Description

North State Liquidators is a wholesale and retail liquidation company specializing in the acquisition and resale of overstock, shelf-pulls, and customer-return merchandise from major national retailers such as Amazon and big-box stores. They provide businesses, resellers, and everyday consumers with access to deeply discounted products across a wide range of categories.

Core business is sourcing palletized and truckload inventory — from brand-new in-box to lightly handled returns — and redistributing at significant savings. They serve as a supply partner for small business owners, flea market vendors, online sellers, and discount retailers.

In addition to bulk wholesale, they offer direct-to-consumer deals — a "treasure hunt" shopping experience for retail customers.

**Tagline theme:** Driven by value, volume, and relationships. Bridging excess retail inventory and market demand.

### Audiences

| Audience | Buying mode | What they want |
|---|---|---|
| Resellers / small businesses | Bulk — pallets, truckloads | Manifests, photos, pricing per pallet, logistics |
| Flea market vendors | Bulk — pallets, mixed lots | Category mixes, low cost-per-unit |
| Online sellers (Amazon/eBay) | Bulk + targeted single items | Condition grading, unit counts, brand names |
| Discount retailers | Truckloads | Volume pricing, consistent supply |
| Retail consumers | Single items — "treasure hunt" | In-store or online deals, photos, price |

---

## Goals

- [ ] Serve **both** B2B (wholesale pallets/truckloads) and B2C (retail/treasure hunt) audiences clearly
- [ ] Convey trust and legitimacy — these customers are wary of liquidation scams
- [ ] Showcase inventory freshness — "what's available now"
- [ ] Capture leads for bulk buyers (contact forms, pallet manifest requests)
- [ ] Drive foot traffic or online retail sales for direct-to-consumer
- [ ] Agree on visual direction / branding

---

## Tech Stack

**Direction:** E-commerce platform. Must support online checkout, payments, inventory, shipping — and be manageable by non-technical owners (Norman & Rob are warehouse operators, not devs).

### Recommended: Shopify

**Why Shopify is the default recommendation:**
- Built for non-technical owners to list products, snap phone pics, hit publish
- Handles payments, tax, shipping labels, inventory tracking out of the box
- Wholesale pricing via B2B Catalogs (Shopify Plus) or apps like `Wholesale Club` for dual B2B/B2C pricing
- Huge theme ecosystem — liquidation/warehouse themes exist
- Integrates with Square POS if/when they add in-store retail
- Mobile admin app — owners can list pallets from the warehouse floor
- **Cost:** Basic $39/mo + 2.9% + 30¢ per transaction

**Tradeoffs:**
- Monthly fee (vs. WooCommerce free)
- Less custom flexibility (but they don't need it)
- Transaction fees unless using Shopify Payments (recommend they do)

### Alternatives considered

| Option | Good for | Why not primary |
|---|---|---|
| **WooCommerce** (WordPress) | Tight budget, technical owner | Maintenance burden too high for Norman/Rob |
| **Square Online** | Deep Square POS integration | Weaker product catalog/bulk features |
| **BigCommerce** | Enterprise feel, no transaction fees | Steeper learning curve |
| **Wix / Squarespace** | Very cheap | Inventory management too shallow for pallet volume |
| **Custom (Next.js + Stripe)** | Ultimate flexibility | Owners can't maintain; overkill |

### Decision needed
- Confirm Shopify Basic ($39/mo) is within budget
- Decide: one unified storefront with B2B pricing toggle, OR two separate storefronts (retail + wholesale)?

---

## Domain & Hosting Plan *(revised 2026-04-17)*

| Item | Value |
|---|---|
| Domain | `northstateliquidators.com` — ✅ **registered 2026-04-17** |
| Registrar | **GoDaddy** (Jeff's existing account) |
| Repo | https://github.com/Brown-Dog-Soup/northstateliquidators (public) |
| Hosting | **GitHub Pages** — static site from `main` branch root |
| Platform change | **Dropped Shopify-first** plan. GH Pages is free; matches pattern from `harpercallahanbooks`. |
| Email | **Microsoft 365 Business Basic** ✅ approved 2026-04-18 (SKU confirmed 2026-04-24) — norm@ + rob@ ($6/user × 2 = $12/mo, → $14/mo after 2026-07-01 price change) plus shared aliases hello@, wholesale@, sales@ |
| Selling | **Shopify Starter** ✅ approved 2026-04-18 — $5/mo + 2.9%+30¢/tx; phone-app product listings from warehouse |

### DNS records to set at GoDaddy

| Type | Host | Value |
|---|---|---|
| `A` | `@` | `185.199.108.153` |
| `A` | `@` | `185.199.109.153` |
| `A` | `@` | `185.199.110.153` |
| `A` | `@` | `185.199.111.153` |
| `CNAME` | `www` | `Brown-Dog-Soup.github.io` |

Delete any GoDaddy "parked" A record on `@` before adding.

### DNS records for Microsoft 365 email *(pending — add AFTER quote is signed)*

Email for `@northstateliquidators.com` will be hosted as a verified custom domain on **TenantIQ Pro LLC's**
Microsoft 365 tenant (`tenantiqpro.com`). The domain-verification TXT value and DKIM CNAME targets are
generated by the M365 admin center when the domain is added — pull the exact values from there before applying.

| Type | Host | Value | Purpose |
|---|---|---|---|
| `TXT` | `@` | `MS=msXXXXXXXX` *(from M365 admin)* | Domain verification |
| `MX` | `@` | `northstateliquidators-com.mail.protection.outlook.com` (priority 0) | Mail routing |
| `TXT` | `@` | `v=spf1 include:spf.protection.outlook.com -all` | SPF |
| `CNAME` | `autodiscover` | `autodiscover.outlook.com` | Outlook autoconfig |
| `CNAME` | `selector1._domainkey` | `selector1-northstateliquidators-com._domainkey.tenantiqpro.onmicrosoft.com` | DKIM |
| `CNAME` | `selector2._domainkey` | `selector2-northstateliquidators-com._domainkey.tenantiqpro.onmicrosoft.com` | DKIM |
| `TXT` | `_dmarc` | `v=DMARC1; p=quarantine; rua=mailto:dmarc@northstateliquidators.com` | DMARC |

> **Important:** MX + SPF + DMARC coexist with the existing GitHub Pages A records on `@` (different record
> types). Do **not** remove the four `185.199.108–111.153` A records or the `www` CNAME — those host the
> public site. `Set-GoDaddyDns.ps1` should be extended to layer in email records without touching the web
> records.

### E-commerce strategy (revised for static hosting)

GitHub Pages is static — no cart or payment processing on-domain. **MVP approach:**

| Use case | Solution | Cost |
|---|---|---|
| Retail "hunt" items | Stripe Payment Links (buy-now buttons) | Free + 2.9% + 30¢/tx |
| Wholesale pallets | Inquiry form (Web3Forms) — "Request Manifest" | Free |
| Email capture / leads | Web3Forms or Formspree | Free tier |

**Upgrade path if volume grows:**
- Shopify Buy Button embeds ($5/mo Starter) for unified cart
- Snipcart drop-in cart (2% fee above $500/mo)
- Full Shopify ($39/mo) if wholesale tiers + inventory tracking become essential

### Subdomains worth reserving
- `shop.` — split retail/wholesale if needed later
- `mail.` — branded email
- `blog.` — SEO content ("finds of the week")

---

## Features (Draft)

### B2B / Wholesale side
- [ ] **Pallet & truckload listings** — photos, category mix, est. retail value, asking price, condition grade
- [ ] **Manifest downloads** (PDF/CSV) where available
- [ ] **Bulk inquiry form** — business name, resale cert, volume, categories of interest
- [ ] **Reseller account / gated portal** (future) — for repeat buyers, verified status
- [ ] Logistics info — pickup, shipping, freight, loading dock

### B2C / Retail side
- [ ] **"Treasure hunt" featured deals** — rotating high-value finds
- [ ] **In-store hours & directions** (if physical location)
- [ ] **Online retail catalog** (future / optional) — Shopify or WooCommerce
- [ ] **New arrivals** feed — keeps people coming back

### Trust / Content
- [ ] Home / landing — value prop, dual-audience split
- [ ] About — company story, legitimacy signals, sourcing relationships
- [ ] How it works — condition grades, buying process
- [ ] FAQ — returns, inspection, pickup, payment
- [ ] Contact — phone, email, hours, address, map
- [ ] Social proof — testimonials, reseller success stories, photos of inventory

### Ops / Marketing
- [ ] SEO basics (local + "liquidation pallets [region]" keywords)
- [ ] Mailing list signup — "new pallets every week"
- [ ] Social media links (Facebook Marketplace, Instagram are huge in this space)

---

## Milestones

| Date | Milestone | Status |
|---|---|---|
| 2026-04-17 | Project kickoff | ✅ |
| 2026-04-17 | Domain registered | ✅ |
| 2026-04-17 | Logo received | ✅ |
| 2026-04-17 | First design mockup | ✅ |
| 2026-04-17 | GitHub repo created | ✅ |
| 2026-04-17 | GitHub Pages enabled | ✅ |
| 2026-04-17 | DNS configured (GoDaddy API) | ✅ |
| 2026-04-18 | Email + selling stack approved (M365 + Shopify Starter) | ✅ |
| 2026-04-24 | Services proposal NSL-202604-0001 drafted (pricing revised same day) | ✅ |
| 2026-04-24 | M365 domain `northstateliquidators.com` verified on TenantIQ Pro tenant | ✅ |
| 2026-04-24 | 2 × Business Basic licenses purchased | ✅ |
| 2026-04-24 | Microsoft 365 mailboxes provisioned (norm@, rob@ + shared hello@/wholesale@/sales@) | ✅ |
| 2026-04-24 | DNS records for email live at GoDaddy (MX, SPF, DKIM, DMARC, Autodiscover) | ✅ |
| 2026-04-24 | HTTPS cert issued by GitHub Pages | ✅ |
| 2026-04-25 | Shopify Partner org + dev store created (`north-state-liquidators-dev`) | ✅ |
| 2026-04-25 | Shopify Admin API access via CLI (`shopify app dev` GraphiQL proxy) | ✅ |
| 2026-04-25 | API-driven product sync to GitHub Pages site (`Sync-NSLFeatured.ps1`) | ✅ |
| 2026-04-25 | Phone workflow doc for Norm + Rob (`SHOPIFY-PHONE-WORKFLOW.md`) | ✅ |
| | Norm + Rob first sign-in + MFA setup | ⏳ |
| | DKIM signing enabled in Defender admin | ⏳ |
| | Norm + Rob trial-list 5–10 real products from warehouse | ⏳ |
| | Design iterations with Norm/Rob | ⏳ |
| | Inquiry form (Web3Forms) for wholesale | ⏳ |
| | Transfer dev store → real merchant store (Starter or Basic) | ⏳ |
| | Payment + shipping configured (post-transfer) | ⏳ |
| | Soft launch (internal test) | ⏳ |
| | Public launch | ⏳ |

---

## Branding

### Logo
- Primary logo: `north_state_liquidators_logo.jpeg` ✅ in project dir
- Style: **illustrated / cartoon**, friendly and approachable — not corporate
- Features NC state outline, NC state flag, pallet jack with boxes, two cartoon owners in branded polos
- Star on the flag appears to mark a location in **central NC** (Triangle / Piedmont area) — confirm exact city

### Color Palette (derived from logo)

| Role | Color | Hex (approx) | Use |
|---|---|---|---|
| Primary | NC Flag Red | `#CC0000` | CTAs, accents, "SHOP NOW" / "REQUEST PALLET" buttons |
| Secondary | Warehouse Yellow | `#F9D71C` | Headlines, callouts, price tags |
| Accent | Pallet-Jack Orange | `#F7941D` | Secondary CTAs, hover states |
| Neutral | State Gray-Green | `#8B9B8B` | Backgrounds, dividers |
| Dark | NC Flag Navy | `#002868` | Headers, body text |
| Light | White / cream | `#FFFFFF` / `#F5F0E6` | Backgrounds |

### Design direction
- **Approachable, local, trustworthy** — lean into the "NC family business" feel the logo is already signaling
- **Warehouse / "treasure hunt" energy** — bold type, high contrast, price stickers, "NEW PALLETS" style callouts
- Avoid over-polished corporate look — would clash with the cartoon logo and the liquidation vibe customers expect

### Fonts (recommended starting point)
- **Headline:** a bold, slightly condensed display face to match the logo's chunky yellow type (candidates: *Anton*, *Bebas Neue*, *Oswald Bold*)
- **Body:** a clean workhorse sans (candidates: *Inter*, *Source Sans 3*, *Roboto*)

---

## Files

| File | Purpose |
|---|---|
| `PROJECT.md` | This file — project tracker |
| `north_state_liquidators_logo.jpeg` | Primary logo |
| `mockups/v1-loading-dock.html` | First concept mockup (Loading Dock + NC Local hybrid) |
| `Quote/NSL_Services_Proposal.html` | Services proposal NSL-202604-0001 (email + Shopify + hosting, $0 labor friends-rate) |
| `Quote/NSL_Services_Proposal.pdf` | Rendered PDF of the proposal |
| `Quote/render-proposal.js` | Playwright HTML→PDF renderer (same pattern as IHG quote) |
| `Provision-NSLMailboxes.ps1` | Idempotent script to provision Norm/Rob + shared mailboxes on TenantIQ Pro tenant (tenant-guarded) |
| `Sync-NSLFeatured.ps1` | Pull Featured collection from Shopify, regenerate hunt-grid in `index.html` |
| `SHOPIFY-PHONE-WORKFLOW.md` | One-page guide for Norm/Rob: list a product from the warehouse floor in 90 seconds |
| `shopify-cli/shopify.app.toml` | Shopify CLI app config (NSL-Dev app, client_id only, secret in OS keychain) |

## Resuming the Shopify API session

The `Sync-NSLFeatured.ps1` script depends on a localhost GraphQL proxy that only exists while `shopify app dev` is running. Each run gets a fresh URL with a new session key. To resume:

```powershell
cd northstateliquidators\shopify-cli
shopify app dev --reset    # OAuth into north-state-liquidators-dev
# Look for "GraphiQL URL: http://localhost:3457/graphiql?key=..." in the CLI output
# Convert that URL by inserting "/graphql.json" before the "?key=" — the actual API endpoint is:
#   http://localhost:3457/graphiql/graphql.json?key=<key>
# Save that into ../.shopify-graphiql-url.txt (the sync script reads it from there)
cd ..
.\Sync-NSLFeatured.ps1                        # re-syncs with whatever's in the cache
.\Sync-NSLFeatured.ps1 -CommitAndPush         # one-shot: sync + git commit + push
```

When you're done, Ctrl+C in the `shopify app dev` terminal. The endpoint dies; cached URL becomes stale until next session.

---

## Open Questions

### Business / scope
- Is there a **physical storefront** for retail customers, or online-only?
- What region/state does "North State" refer to? (affects SEO, logistics, shipping radius)
- Primary vs. secondary audience — is wholesale or retail the bigger revenue driver?
- Do they want **e-commerce checkout** (Shopify-style) or just **inquiry-based** sales?

### Inventory
- How often does inventory turn over? (affects CMS choice — weekly updates vs. real-time)
- Who will keep inventory listings current after launch?
- Do they receive manifests from suppliers that could be uploaded directly?
- Photography workflow — phone pics on the warehouse floor, or staged product shots?

### Branding / existing presence
- ✅ Logo confirmed (illustrated cartoon, NC-themed)
- Need: logo source files — vector (SVG/AI/PDF) preferred over JPEG for web scaling
- Need: logo variants — horizontal lockup, stacked, icon-only, light/dark backgrounds
- Domain owned? (`northstateliquidators.com` available?)
- Current Facebook / Instagram / Google Business presence?
- Any existing customer list or email database?
- Confirm exact city the NC flag star is pointing to

### Tech / ops
- Budget for hosting + tools (monthly)?
- Who on the team handles ongoing updates?
- Payment processing requirements (Stripe, Square, in-person only)?
- **Re-evaluate hosting (GitHub Pages → Azure Static Web Apps) when adding wholesale inquiry form.** GitHub Pages is fine for static-only; SWA earns its switch when we need server-side endpoints (form submissions, admin panel auth, etc.). Currently overkill — defer.

---

## Notes / Log

- **2026-04-17** — Project directory created, initial tracker stubbed.
- **2026-04-17** — Business description captured. Dual-audience (B2B bulk + B2C retail), NC-based, inventory sourced from Amazon/big-box overstock/returns.
- **2026-04-17** — Logo received (illustrated cartoon with NC state, pallet jack, two owners). Color palette + design direction drafted. Still need vector source files and variants.
- **2026-04-17** — Owners identified: Norman Turner & Rob TeCarr. Location confirmed: Raleigh, NC. Main phone (919) 526-0112.
- **2026-04-17** — Logo file saved to project dir as `north_state_liquidators_logo.jpeg`.
- **2026-04-17** — Confirmed site must support direct e-commerce (wholesale pallets + retail). Shopify recommended as platform.
- **2026-04-17** — First mockup built at `mockups/v1-loading-dock.html` — "Loading Dock + NC Local" hybrid. Includes live ticker, pallet ledger (B2B), retail "hunt" grid (B2C), owner section, how-it-works. Open in browser to view.
- **2026-04-17** — Domain `northstateliquidators.com` confirmed available. Plan: register at GoDaddy (existing account), host store on Shopify, point DNS at Shopify IP/CNAME. GoDaddy cPanel hosting not used for this project.
- **2026-04-17** — ✅ Domain registered at GoDaddy.
- **2026-04-17** — ✅ GitHub repo created at Brown-Dog-Soup/northstateliquidators (public). Initial commit pushed: mockup as index.html, CNAME, logo, README, .gitignore. GitHub Pages enabled on main/root.
- **2026-04-17** — **Pivot:** dropped Shopify-first hosting plan. Site hosts on free GitHub Pages (matching harpercallahanbooks pattern). Selling via Stripe Payment Links (retail) + inquiry forms (wholesale) for MVP.
- **2026-04-17** — ✅ DNS configured via GoDaddy Domain API. 4 A records (185.199.108-111.153) on apex + CNAME www → brown-dog-soup.github.io. Reusable `Set-GoDaddyDns.ps1` script added to repo. Propagating now; site will be live at https://northstateliquidators.com once DNS clears (usually <30 min).
- **2026-04-18** — ✅ Norm + Rob approved Jeff's recommendations on both open decisions: **Microsoft 365 Business** (email for norm@ + rob@ on northstateliquidators.com) and **Shopify Starter** ($5/mo) for selling. Plan: shared aliases (hello@, wholesale@, sales@) on M365; Shopify phone app for warehouse-floor listings; wholesale stays inquiry-form + draft-order invoice flow. Setup to begin next. *(Pricing revised 2026-04-24 — see below.)*
- **2026-04-21** — In-person working session scheduled **Thursday 2026-04-23 evening** with Norm and Rob (time/location TBD). Purpose: collect EIN, bank info for Shopify payouts, first retail items to list, and admin account owner.
- **2026-04-24** — Services proposal NSL-202604-0001 drafted under **TenantIQ Pro LLC** ($0 labor, friends rate). Hosting model: NSL mailboxes live as a verified custom domain on the `tenantiqpro.com` M365 tenant — no separate tenant for NSL. Proposal saved to `Quote/` as HTML + PDF; to be signed by Norm + Rob before any tenant work begins. Planned DNS additions for email (MX, SPF, DKIM, DMARC, Autodiscover) documented in the Domain & Hosting section; must layer onto existing GitHub Pages records without disturbing them.
- **2026-04-24** — **License pricing corrected.** Original "$4/mo × 2 = $8/mo Microsoft 365 Business" line was a misnomer — $4 is Exchange Online Plan 1 (email-only), not "Business." After confirming with Jeff, plan bumped to **Microsoft 365 Business Basic** at $6/user/mo × 2 = **$12/mo** (includes Outlook desktop + mobile, Teams, 1 TB OneDrive, web/mobile Word/Excel/PowerPoint). Total monthly pass-through now **$17/mo** ($12 M365 + $5 Shopify); first-year ~$224. Microsoft has announced a Business Basic price increase to $7/user effective 2026-07-01 — pass-through will rise to $19/mo at renewal. Tenant `tenantiqpro.com` currently holds 2 × SPB (Business Premium, both assigned) and FLOW_FREE only — 2 × Business Basic licenses must be purchased before provisioning. Proposal HTML updated; PDF re-rendered.
- **2026-04-24** — ✅ **Email provisioned.** Domain verified on TenantIQ Pro M365 tenant (`d9b645c3-3587-4cd4-be9b-1a8d405c92ad`), 2 × Business Basic licenses purchased, `Provision-NSLMailboxes.ps1` created and run. Outputs: `norm@northstateliquidators.com` (ID `55273cc1-69b9-4095-a7f9-5663247c70ee`), `rob@northstateliquidators.com` (ID `ca101cc6-3e5d-467e-abc8-c302abe34cfb`), plus shared mailboxes `hello@`, `wholesale@`, `sales@` with FullAccess + SendAs for both Norm and Rob. Temp passwords captured and prepared for secure delivery via `outbox/norm-rob-welcome-email.txt` (gitignored). All email DNS live at GoDaddy: MX → Outlook protection, CNAME autodiscover → outlook, TXT SPF, TXT MS= verification token, CNAME selector1/2._domainkey → DKIM, TXT _dmarc → p=quarantine (GoDaddy parked DMARC replaced). GitHub Pages A/CNAME records untouched throughout. Last remaining M365 step: flip DKIM toggle in Defender admin once CNAMEs propagate.
- **2026-04-24** — ✅ **Shopify plan gotchas found.** Proposal's "2 staff accounts" and "2.9% + 30¢" lines for Shopify Starter were wrong. Starter allows **0 staff accounts** (only store owner) and charges **5% + 30¢** via Shopify Payments; API access not available on Starter. Break-even with Basic ($39/mo, 2.9% + 30¢, 2 staff, API) is ~$1,620/mo in online card sales. Jeff's plan: launch on Starter using collection buy-button embed (zero per-product site maintenance), Norm + Rob share login via 2FA routed through `sales@` shared mailbox, migrate to Basic + subdomain split (`shop.northstateliquidators.com`) when volume warrants.
- **2026-04-24** — Site outage + fix: `http://northstateliquidators.com` started returning 404 (`Server: GitHub.com`, no content). Root cause: repo had been flipped to **private** after initial setup, which disabled GitHub Pages on the free plan. Fix: flipped repo back to public (`gh api -X PATCH ... -F private=false`), re-enabled Pages on `main` branch root (`gh api -X POST .../pages`), HTTPS + HTTP both 200 within ~2 min. No content or DNS changes needed — CNAME file + GoDaddy A records were both still correct.
- **2026-04-25** — ✅ **Shopify dev store + API POC live.** Created Partner org "North State Liquidators" (id `215502584`) and dev store `north-state-liquidators-dev.myshopify.com` (norm@northstateliquidators.com is admin). Shopify deprecated legacy in-store custom-app creation on 2026-01-01 — modern path is **Dev Dashboard + Shopify CLI**. Walked through several false starts (public app + leaked `shpss_…` secret, partner-org install with example.com OAuth callback, etc.) before landing on the canonical 2026 flow: `npm install -g @shopify/cli` → `shopify app config link` → `shopify app deploy` → `shopify app dev` opens a local GraphiQL proxy at `localhost:3457` that auto-injects the admin session for any GraphQL query. No `shpat_…` token is exposed — Shopify CLI keeps it in the OS keychain. Built `Sync-NSLFeatured.ps1` that pulls the Featured collection through that proxy and rewrites the hunt-grid block in `index.html` between marker comments, so warehouse-floor listings flow onto northstateliquidators.com on demand. Wrote `SHOPIFY-PHONE-WORKFLOW.md` for Norm + Rob covering app install, listing loop, and the `featured` tag convention. POC live on prod URL: 3 sample products (Bella Canvas TShirt, KitchenAid Mixer, Yeti Cooler) all click-through to dev-store product pages. Dev store stays password-gated until transfer to a real merchant account; that transfer is the next gating step before money can actually flow.
