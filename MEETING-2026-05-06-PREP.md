# Meeting prep — Rob, 2026-05-06 ~7pm

Three topics Rob raised, plus open items that'll likely come up.

---

## 1. UPC vs LPN pricing — the duplicate-row ambiguity is real

**Live data (2026-05-05):**
- 362 distinct UPCs in `lpn_catalog`
- **29 UPCs appear on more than one row** (max 8 rows for one UPC)
- **15 of those have different costs** across rows
- **15 have different wholesale prices** across rows

**Why it happens:** Same item arrives on multiple manifests/lots over time, at different costs/prices. Today's `sp_LookupCode` does `SELECT TOP 1 ... WHERE upc = @c` with **no ORDER BY** — SQL Server picks whatever the index hands back first. Effectively random.

**Three fixes to put in front of Rob, ordered by complexity:**

| # | Approach | Behavior | Effort |
|---|---|---|---|
| A | **Most-recent wins** | `ORDER BY last_seen_at DESC, imported_at DESC` — picks freshest manifest | 1-line SQL change |
| B | **Active-pallet's lot wins** | Prefer rows whose `lot_id` matches the receiver's selected pallet; fall back to most-recent | Medium |
| C | **Receiver picks** | When >1 match, surface a chooser in the lookup card | UI work |

**For UPC scans hitting the public fallback (UPCitemdb)**: no fix — that source genuinely doesn't carry cost or wholesale. Either accept blank PRICE/COST for off-manifest UPCs, or pay for a richer provider (Keepa, Go-UPC) that has wholesale-ish prices.

**My recommendation to walk in with:** A as the default behavior, with B as a quick follow-up once we have lot tagging cleaner. C is overkill given only 29 UPCs are affected.

---

## 2. CSV / XLSX self-serve upload — already 80% built

**The Function endpoint already exists:** `POST /api/import-manifest`
(`api/Functions/ImportManifestFunction.cs`)

What it does today:
- Accepts an XLSX as the request body (`x-filename` header for the original name)
- Computes SHA-256 of the file → idempotent: same file uploaded twice returns the prior import id without re-processing
- Parses, upserts into `lpn_catalog`, writes audit row to `manifest_imports`
- Returns `{ ImportId, Filename, Sha256, RowCount, RowsInserted, RowsUpdated, UnmappedColumns }`

**What's missing:**
- **A UI page** — small `/staff/import.html` with drag-and-drop, progress bar, results readout. ~half a day of work.
- **Format coverage check** — `ManifestParser` (in `api/Services/ManifestParser.cs`) was originally written for the Amazon B-Stock format. The Master Inventory format Rob's been sending uses a different schema (no LPN column, `Item #` is LP-prefixed, etc.). The PowerShell `Import-NSLMaster.ps1` was built specifically because the C# parser couldn't handle Master format. Need to either (a) port that mapping into ManifestParser, or (b) detect format on upload and route accordingly.

**My recommendation:** Yes, doable, mostly already there. Quick win to ship the upload page once the parser handles Master format.

---

## 3. Receiving → frontend → Shopify flow — there's a real gap

**Current state:**

```
[Master XLSX from Norm/Rob]
      |
      v  (Jeff runs Import-NSLMaster.ps1 manually on laptop)
      |
[lpn_catalog]                              [Shopify dev store]
      |                                          ^
      v                                          | (Norm/Rob hand-list
[Receive on truck]                               |  via Shopify mobile app,
      |                                          |  tag "featured")
      v                                          |
[Scan into pallet (line_items)]                  |
      |                                          |
      X — no automation here ——————————————→ ???
                                                 |
                                                 v
                                         [Sync-NSLFeatured.ps1
                                          Jeff runs manually,
                                          rewrites index.html
                                          on GitHub Pages]
                                                 |
                                                 v
                                  [northstateliquidators.com]
                                         (static HTML)
```

**Gaps:**
- **Pallets and items in the staff portal aren't in Shopify.** Receiving and selling are disconnected systems today.
- **Sync-NSLFeatured is manual** — requires `shopify app dev` running on Jeff's laptop. Doesn't run on a schedule.
- **No sale → backend wiring.** When a Shopify sale fires, nothing flips a `line_items` row to sold.
- **No real "go live" gate.** Sell Mode (lot/individual/mixed) is purely an internal label; doesn't trigger anything.

**The natural bridge: DRAFT / LIVE / SOLD lifecycle that Rob sketched.**
- DRAFT → still being built, not visible publicly
- LIVE → flip-the-switch trigger that auto-creates a Shopify product (or pushes the pallet manifest as a Shopify draft order template, depending on whether they want pallet-as-listing or item-as-listing)
- SOLD → set by a Shopify webhook when the cart checks out, OR manually for in-person/local sales

**To wire this up we need:**
1. A real Shopify Admin API token outside the CLI dev proxy (CLI proxy only works on Jeff's laptop). Shopify Custom App with admin scope.
2. A Function in `/api` that pushes pallet → Shopify on LIVE flip.
3. A Shopify webhook handler that pulls SOLD events back.

**Decisions to walk through with Rob:**
- Pallet-as-listing, item-as-listing, or both? (Wholesale buyers want pallet; retail wants individual item.)
- What goes in the listing? Photo, title, condition, price (Wholesale Price? Sell Price?), inventory count.
- Who has authority to flip LIVE? Both Rob and Norm? With a confirmation step?

---

## Other items likely to come up

- **REAL / GHOST** display mode (from the original xlsx annotations). Mechanically simple, but it's a marketing-positioning call worth their input — when does GHOST cross over into misleading? Show on storefront as "sold" with no real inventory.
- **Three-tier pricing per pallet** (Pallet Price = auto-rolled-up; Starting Price = override; Sale Price = crossed-out). Already in the open queue from the prior session. Easy build once DRAFT/LIVE/SOLD lands.
- **Bulk lot tagging.** Receiver scans a pallet of 50 items — does each get individually tagged, or does the pallet itself become the unit of sale? Tied to the pallet-vs-item Shopify question.
- **Friends-rate contract reminder** if they ask for cost: per signed proposal NSL-202604-0001, all labor is $0. Only third-party pass-throughs (Microsoft, Shopify, GoDaddy, Apify) carry a real number.

---

## What to come ready with

- [ ] Recommendation on UPC duplicate behavior (A: most-recent wins, by default)
- [ ] Upload-page mockup or sketch (5 minutes whiteboard)
- [ ] Shopify token plan — switch from `shopify app dev` proxy to a real Custom App / Admin API token before any auto-publish work can land
- [ ] Question for Rob: pallet-as-listing or item-as-listing on the public site?
- [ ] Question for Rob: who can flip LIVE — both owners, or one with the other notified?
