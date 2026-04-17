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
| Norman Turner | Owner & Operator | — |
| Rob TeCarr | Owner & Operator | — |
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
| Email (future) | TBD — Google Workspace or M365 on `@northstateliquidators.com` |

### DNS records to set at GoDaddy

| Type | Host | Value |
|---|---|---|
| `A` | `@` | `185.199.108.153` |
| `A` | `@` | `185.199.109.153` |
| `A` | `@` | `185.199.110.153` |
| `A` | `@` | `185.199.111.153` |
| `CNAME` | `www` | `Brown-Dog-Soup.github.io` |

Delete any GoDaddy "parked" A record on `@` before adding.

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
| | Requirements gathered | ⏳ |
| | Shopify store created | ⏳ |
| | Theme installed + branded | ⏳ |
| | DNS pointed at Shopify | ⏳ |
| | Initial product listings | ⏳ |
| | Payment + shipping configured | ⏳ |
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
