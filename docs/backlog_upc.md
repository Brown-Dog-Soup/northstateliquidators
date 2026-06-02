# Backlog — Better UPC / ASIN Product-Data + Pricing Provider

**Status:** Backlog / decision pending
**Logged:** 2026-06-02
**Trigger:** Scanned a 10′ Lightning cable (valid 12-digit UPC). Lookup *found the
item* (title/photo) but returned **no price** — UPCitemdb's free tier has good
identity coverage but sparse pricing on cheap commodity goods.

---

## Problem

`UpcLookupService` (free UPCitemdb trial, 100/day) is fine for *identity*
(title/brand/image) but weak on *price*, especially for low-cost accessories.
For a liquidation business the number that matters is **resale price**, and
inventory comes from **Amazon AND big-box** (Walmart, Target, Best Buy, Lowe's,
Home Depot) — so an Amazon-only tool (e.g. Keepa) is the wrong primary.

**Requirement:** look up by **UPC *or* ASIN**, cover Amazon + big-box, return
good identity **and** multi-retailer pricing.

Current behavior when price is missing: the scan card's **Sell price** field
(`staff/scan.html:61`) can't auto-fill (it multiplies the missing ref price in
`staff/js/scan.js:225` `suggestSellPrice()`), so the receiver just **types the
price manually** — that's the working fallback today.

---

## Options evaluated (June 2026)

| Provider | Inputs | Coverage | Pricing data | Cost | Verdict |
|---|---|---|---|---|---|
| **RetailerAPI** ⭐ | UPC/EAN/ISBN/GTIN/**ASIN**/item_id | Walmart, Amazon, eBay, Target, Best Buy, Lowe's, Home Depot | Per-retailer price + history (`include_cross_retailer=true`) | **Free 1,000/mo**, paid tiers above | **Try first** — exact retailer match; newer/smaller, so validate coverage before relying on it |
| **Barcode Lookup API** | UPC/EAN/ISBN/**ASIN**/MPN/brand | Multi-store | Lowest/highest/avg across retailers | ~**$40/mo** (or $140/mo) | Proven fallback / safe paid choice |
| Go-UPC | UPC/EAN/ISBN | Broad identity | Thin | from $19.95/mo | Identity-only; doesn't solve pricing |
| UPCitemdb (current) | UPC/EAN | Broad identity | Sparse | Free 100/day | Keep as last-resort identity fallback |
| Keepa | ASIN (+ UPC→ASIN) | **Amazon only** | Excellent Amazon price history | €19/mo + token tiers | Rejected as primary — Amazon-only |

---

## Recommendation

1. **Try RetailerAPI first** — free 1,000/mo, takes UPC *or* ASIN, returns
   cross-retailer pricing for exactly the stores NSL sources from. Validate on
   10–20 real scanned items (start with the Lightning cable) before committing.
2. **Barcode Lookup ($40/mo)** as the proven paid fallback if RetailerAPI's
   coverage or reliability disappoints.
3. Keep **UPCitemdb** as the free identity safety net.
4. **Manual price entry** stays available regardless.

---

## Build sketch (when picked up)

- Generalize `api/Services/UpcLookupService.cs` → **`ProductLookupService`**
  that accepts a **UPC or ASIN**, calls the chosen provider first, then falls
  back to UPCitemdb, then manual.
- Wire into `LookupFunction` after the local `sp_LookupCode` catalog hit
  (`api/Functions/LookupFunction.cs:54`), same shape as today.
- Provider API key → **SWA app settings** (e.g. `RetailerApiKey` /
  `BarcodeLookupApiKey`), alongside `SqlConnectionString` — never in repo.
- Stamp `line_items.enrich_source` accordingly — the schema already reserves
  these values: `go_upc | upcitemdb | keepa | ebay | ai_vision | manual`
  (`db/schema.sql:52`). Add the new source name to that list.
- Keep GTIN check-digit validation + the 422 "rescan" signal already in place.

## Desired lookup behavior — manifest price + market price (DUAL SOURCE)

Today `LookupFunction` is *either/or*: a local catalog hit returns immediately and
never calls the external provider; the provider is only tried on a catalog miss
(`api/Functions/LookupFunction.cs:43-79`). The owners want **both prices at once**:

| Source | Fields | Meaning |
|---|---|---|
| **Manifest** (`lpn_catalog`) | `unit_cost`, `wholesale_price`, `est_msrp` | Cost basis + intended resale — what we paid / planned |
| **Market** (ASIN/UPC provider) | live retail / resale price | What it's actually worth right now |

### Required changes

1. **UPC→ASIN bridge.** B-Stock catalog rows are keyed on **LPN/ASIN** and often
   have a **blank UPC**, so scanning a product's retail UPC misses the catalog
   entirely (this is why the 10′ Lightning cable returned no manifest price — its
   price was filed under the LPN/ASIN, not the scanned UPC). Fix: when the catalog
   misses on a UPC, take the **ASIN** the provider returns and **re-query the
   catalog by ASIN** to recover the manifest price.
2. **Merge instead of short-circuit.** Always attempt the external provider (by
   ASIN if known, else UPC) and return a combined result carrying *both* the
   manifest pricing and the market pricing — don't stop at the first hit.
3. **Scan card shows both.** `staff/scan.html` + `staff/js/scan.js` render a
   **Manifest** price block (cost / wholesale / MSRP) and a **Market** price block
   side by side. `suggestSellPrice()` can seed from either (market resale or
   manifest wholesale); receiver overrides as today.
4. **enrich_source** reflects the combined provenance (e.g. `lpn_catalog+retailerapi`).

### Workaround until built
Scan the **LPN sticker** (not the retail UPC) for B-Stock items — it matches the
catalog row directly and pulls the manifest cost/wholesale/MSRP.

## Open questions
- Which provider to commit to after the free RetailerAPI trial?
- Do we want **resale** price (eBay sold comps) in addition to retail price, for
  truer liquidation pricing? (eBay Browse API is ~free; sold-price/Marketplace
  Insights needs approval.)
- Volume: warehouse hand-scan pace vs. the 1,000/mo free cap — may need a paid
  tier once both owners are scanning daily.

## Sources
- RetailerAPI — https://github.com/retailerapi/mcp
- Barcode Lookup API — https://www.barcodelookup.com/api
- Barcode Lookup docs — https://www.barcodelookup.com/api-documentation
- Go-UPC plans — https://go-upc.com/plans/api
- Keepa API — https://keepa.com/#!api
