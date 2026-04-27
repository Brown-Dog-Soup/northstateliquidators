# North State Liquidators — Inventory System Buildout

**Project:** Scan-to-Shopify inventory pipeline on Azure
**Owner:** Jeff Blanchard / North State Liquidators
**Tenant:** TenantIQPro.com (Entra ID)
**Status:** Architecture / planning phase
**Last updated:** 2026-04-27

---

## Goal

Build a workflow where pallets are scanned in as they arrive, stored in Azure, enriched automatically with product data via UPC APIs, then either listed on Shopify as a manifested lot **or** individualized into per-item listings — all without recurring software subscriptions beyond Shopify itself.

---

## High-level flow

```
[Bluetooth scanner + tablet/phone]
        ↓
[Web form / PWA] — pallet ID, UPC scan, photo, condition, qty
        ↓
[Azure Function HTTP trigger] — accepts scan, writes raw row
        ↓
[Azure SQL] manifests + line_items     [Blob Storage] photos
        ↓
[Service Bus queue: "enrich-{lineId}"]
        ↓
[Azure Function (queue-triggered)] — calls UPC API + pricing API
        ↓
[Azure SQL] line_item updated with title/desc/image/est_value
        ↓
[Review UI] — manifest dashboard with hit rate, totals, est. value
        ↓
[Decision per pallet]:
   ├─ "Sell as manifested lot" → 1 Shopify product, manifest as description
   └─ "Individualize"          → N Shopify products, one per line item
        ↓
[Azure Function] → Shopify Admin API → product(s) live
```

---

## Hardware

| Item | Model | Qty | Cost | Purpose | Status |
|---|---|---:|---:|---|---|
| Scanner (primary) | Tera HW0009 (2D, tri-mode, LCD) | 2 | ~$140–160 | Receiving + POS counter | **Ordered by Rob (2026-04-27)** |
| Scanner (backup) | Zebra DS2208 | 1 | ~$150 | Damaged-label rescue at receiving | TBD |
| Label printer | Brother QL-820NWB (thermal, no ink) | 1 | ~$150 | In-house barcodes for items missing UPC | TBD |
| Tablet | iPad / Android (existing) | 1+ | reuse | Power Apps scan form | reuse |
| **Total hardware** | | | **~$440–460 one-time** | |

### Why HW0009 over HW0002
- 2D capable (QR, DataMatrix, PDF417) — manifested pallets often carry QR-coded packing slips
- Built-in LCD confirms each scan visually before moving on
- Reads from phone/computer screens
- Offline batch memory ~100k codes for walking pallets before uploading
- Tri-mode connectivity (USB + 2.4GHz + Bluetooth)

### Scanner behavior
All recommended scanners are **keyboard-wedge** — to any device they appear as a keyboard typing the barcode + Enter. Works with Power Apps, Shopify admin, Shopify POS, Google Sheets, web forms — no drivers, no SDK.

---

## Data model (Azure SQL)

### `manifests`

| col | type | notes |
|---|---|---|
| id | uniqueidentifier | PK |
| source | varchar | auction lot #, vendor, truck # |
| received_date | datetime | |
| status | varchar | `receiving`, `enriching`, `ready`, `lotted`, `individualized`, `sold` |
| total_cost | money | what we paid for the pallet |
| sell_mode | varchar | `undecided`, `lot`, `individual` |

### `line_items`

| col | type | notes |
|---|---|---|
| id | uniqueidentifier | PK |
| manifest_id | uniqueidentifier | FK → manifests.id |
| upc | varchar | scanned barcode |
| qty | int | |
| condition | varchar | `new`, `open_box`, `damaged`, `untested` |
| photo_blob_url | varchar | Azure Blob URL |
| enrich_status | varchar | `pending`, `hit`, `miss`, `error` |
| title | nvarchar | from UPC API |
| description | nvarchar(max) | from UPC API |
| brand | nvarchar | from UPC API |
| category | nvarchar | from UPC API or AI Vision |
| est_msrp | money | from Keepa / eBay |
| est_resale | money | from Keepa / eBay |
| shopify_product_id | bigint | nullable until pushed |
| created_at | datetime | |
| enriched_at | datetime | |

### `enrichment_log`
Audit table — which API gave which answer per line item, for debugging hit rate and tuning.

### `lpn_catalog`

Pre-loaded from Amazon liquidation manifest XLSX files. Amazon LPNs (License Plate Numbers) are internal tracking codes that **do not exist in any public UPC database** — they only appear on the manifest Amazon ships with each pallet. This table is our private lookup layer.

| col | type | notes |
|---|---|---|
| lpn | varchar(20) | PK — Amazon LPN, e.g. `LPNAB123XYZ45` |
| asin | varchar(20) | Amazon Standard ID Number, useful for cross-referencing |
| upc | varchar(20) | sometimes present in manifest, nullable |
| title | nvarchar | from manifest |
| description | nvarchar(max) | from manifest, often sparse |
| category | nvarchar | from manifest |
| msrp | money | manifest unit retail |
| condition | varchar | `customer_return`, `salvage`, `new`, etc. |
| qty_in_manifest | int | how many of this LPN were on the pallet |
| source_manifest | varchar | filename of the XLSX it came from |
| source_pallet_id | varchar | FK-ish reference to which pallet/load |
| imported_at | datetime | for re-import precedence |

### `manifest_imports`

Tracks which XLSX files have been ingested, so we can audit and re-import safely.

| col | type | notes |
|---|---|---|
| id | uniqueidentifier | PK |
| filename | varchar | original XLSX filename |
| pallet_reference | varchar | Amazon load # or buyer reference |
| row_count | int | rows ingested |
| imported_at | datetime | |
| imported_by | varchar | user identity |
| sha256 | varchar | file hash, prevents duplicate import |

---

## Azure resource group

Single resource group, e.g. `rg-nsl-prod`:

```
rg-nsl-prod/
├── stapp-nsl-web        # Static Web App (marketing + scan UI)
├── func-nsl-api         # Function App (ingest + enrichment + Shopify)
├── sql-nsl / sqldb-nsl  # Azure SQL server + database
├── stnslprod            # Storage Account (blobs + queues)
├── kv-nsl               # Key Vault (Shopify token, UPC API keys)
├── sb-nsl               # Service Bus namespace + enrich-queue
├── appi-nsl             # Application Insights
└── log-nsl              # Log Analytics workspace
```

### Estimated monthly cost

| Resource | Tier | Est. cost/mo |
|---|---|---:|
| Azure SQL Database | Basic (2GB) | ~$5 |
| Azure Functions | Consumption | $0–20 |
| Azure Storage (blobs + queues) | Standard LRS | $1–5 |
| Azure Service Bus | Basic | $0 (or use Storage Queue free) |
| Static Web App / Power Apps | Free / per-user | $0–$5 per user |
| Application Insights | Pay-as-you-go | $0–10 |
| Key Vault | Standard | $0–1 |
| **Azure subtotal** | | **~$10–40/mo** |
| Shopify Basic | | $39/mo |
| UPC API (Go-UPC PAYG) | | $5–30/mo |
| **Total** | | **~$55–110/mo** |

---

## Components

### 1. Scan capture (front-end)

- **Option A: Power Apps canvas app** — fastest to build, native to M365 tenant, hooks into Azure SQL via connector. Bluetooth scanner types into the UPC field. Photo from device camera. ~2 days to build.
- **Option B: Static Web App + simple HTML/JS PWA** — more control, cheaper at scale, more work upfront. ~1–2 weeks to build.
- **v1 = Power Apps**, migrate to PWA only if outgrown.

### 2. Ingest API

Azure Function: `POST /api/scan`

```json
{ "manifestId": "...", "upc": "...", "qty": 1, "condition": "new", "photoBase64": "..." }
```

Function:
- Saves photo to Blob Storage
- Inserts row into `line_items` with `enrich_status='pending'`
- Drops message on Service Bus queue `enrich-queue`
- Returns immediately (UI doesn't wait for enrichment)

### 3. Enrichment worker

Azure Function, queue-triggered. For each message, **lookup order matters**:

1. **Detect code type** by pattern:
   - Starts with `LPN` or matches Amazon LPN format → treat as LPN
   - 12–13 digit numeric → UPC/EAN
   - Otherwise → unknown, mark for manual review
2. **If LPN** → query `lpn_catalog` table (private Azure SQL data)
   - Hit → fill title/description/category/msrp from our manifest catalog, set `enrich_status='hit'`, `enrich_source='lpn_catalog'`
   - Miss → fall through to ASIN lookup if scanner picked up an ASIN, else flag for manual
3. **If UPC/EAN** → call **Go-UPC** (or UPCitemdb) → title, brand, description, image
4. **Optional pricing layer** (any code type, after primary hit):
   - **Keepa** API → Amazon price history → `est_msrp`, `est_resale`
   - **eBay Browse API** → real-world sold listings
5. **Optional vision fallback** when nothing hit:
   - **Azure AI Vision** on photo → tags/category
6. Update `line_items` row, set `enrich_status='hit' | 'miss' | 'partial'`
7. Log API used + raw response into `enrichment_log`

Expected hit rate on liquidation inventory:
- **Amazon pallets with LPN manifests pre-loaded: ~95%+** (LPN catalog covers nearly everything Amazon shipped)
- Generic UPC pallets: ~60–70%
- Estate/random goods: ~30–40%

Misses get flagged in the UI for manual entry.

### 4. Review dashboard

Power BI report (live to Azure SQL) or simple Static Web App:
- Per manifest: total items, enrichment hit rate, total est. resale value, total cost, projected margin
- Drill into line items: filter by `enrich_status='miss'` to fix manually
- Top of dashboard: **two big buttons** per manifest — "Sell as Lot" / "Individualize"

### 4a. Manifest XLSX importer (Amazon LPN ingest)

Separate Azure Function (HTTP-triggered or Blob-triggered): `POST /api/import-manifest`

**Trigger options:**
- Drop XLSX file into an Azure Blob container `manifests-incoming/` → Blob trigger fires automatically
- Or admin uploads via web UI → HTTP trigger

**Function behavior:**
1. Compute SHA-256 of file; if already in `manifest_imports`, skip (idempotent)
2. Parse XLSX (use `ClosedXML` or `EPPlus` in .NET, or `openpyxl` in Python — Function runtime choice)
3. Map columns flexibly — Amazon manifests vary:
   - LPN column may be labeled `LPN`, `License Plate`, `Item ID`, `Pallet ID`
   - Title may be `Title`, `Product Name`, `Description`, `ASIN Title`
   - MSRP may be `Unit Retail`, `MSRP`, `Retail Price`, `Extended Retail`
   - Build a column-name dictionary; surface unmapped columns to admin
4. UPSERT into `lpn_catalog` keyed on `lpn` — newer `imported_at` wins
5. Insert audit row into `manifest_imports`
6. Report: rows ingested, rows skipped, unmapped columns, duplicate LPNs across manifests

**Powershell helper for bulk import** (pre-MVP, run from home machine):
```powershell
# Bulk-load all existing manifest XLSX files into Azure SQL
Get-ChildItem -Path "C:\NSL\Manifests\*.xlsx" | ForEach-Object {
    Invoke-RestMethod -Uri "https://func-nsl-api.azurewebsites.net/api/import-manifest" `
        -Method Post -InFile $_.FullName -ContentType "application/octet-stream" `
        -Headers @{ "x-functions-key" = $env:NSL_FUNCTION_KEY }
}
```

This lets Jeff load all existing XLSX manifests in one batch before going live.

### 5. Shopify push

Azure Function, HTTP-triggered from dashboard buttons.

**Sell as Lot:**
- Create ONE Shopify product
- Title: `"Manifested Pallet — {source} — {item count} items, est. retail ${X}"`
- Description: HTML table of every line item (title, qty, condition)
- Price: asking price
- Qty: 1
- Update `manifests.status='lotted'`, `sell_mode='lot'`

**Individualize:**
- Loop over `line_items` where `manifest_id={id}`
- For each: create Shopify product with enriched title/description/photo, price = `est_resale * margin_factor`, qty
- Save `shopify_product_id` back to row
- Update `manifests.status='individualized'`, `sell_mode='individual'`

### 6. Auth & secrets

- **Entra ID** for app login
- **Azure Key Vault** for: Shopify Admin API token, Go-UPC key, Keepa key, eBay key
- Functions use **Managed Identity** to read Key Vault — no secrets in code

---

## Repo layout (target)

```
North State Liquidators/
├── web/                       # marketing site + scan PWA
│   ├── index.html
│   ├── mockup.html
│   └── scan/                  # scan UI
├── api/                       # Azure Functions
│   ├── ingest/
│   ├── enrich/
│   └── shopify-push/
├── infra/                     # Bicep IaC
│   ├── main.bicep
│   └── modules/
├── db/                        # SQL schema + migrations
│   └── schema.sql
└── .github/workflows/         # CI/CD: deploy on push
    ├── web.yml
    ├── api.yml
    └── infra.yml
```

---

## Move-to-Azure steps

1. **Subscription decision** — separate Azure subscription under the **TenantIQPro.com Entra tenant** (not Surya). Clean billing boundary, identity ties to the TenantIQPro org.
2. **Provision infra** via Bicep:
   ```powershell
   az deployment group create --resource-group rg-nsl-prod --template-file ./infra/main.bicep
   ```
3. **Migrate marketing site** (optional) — point Static Web App at this repo, update GoDaddy DNS `CNAME www → <stapp>.azurestaticapps.net`, retire GitHub Pages.
4. **Build inventory system** in phases below.
5. **Wire DNS:**
   - `northstateliquidators.com` → Static Web App (marketing + scan UI)
   - `api.northstateliquidators.com` → Function App (custom domain + cert)
   - `scan.northstateliquidators.com` → Static Web App scan route

---

## Build phases

| Phase | Scope | Time |
|---|---|---|
| 0 | Provision SQL, Storage, Functions, Key Vault, Power Apps env (Bicep) | 1 day |
| 1 | Scan capture → SQL → Blob (no enrichment yet) | 2–3 days |
| 2 | Enrichment Function + Go-UPC integration + LPN catalog lookup | 2–3 days |
| 2a | Manifest XLSX importer + bulk-load existing Amazon manifests | 1–2 days |
| 3 | Review dashboard (Power BI or simple web) | 2–3 days |
| 4 | Shopify push — Lot mode | 1–2 days |
| 5 | Shopify push — Individualize mode | 2 days |
| 6 | Polish: photo capture, condition picker, manual override flow | 2–3 days |
| | **Total to MVP** | **~2–3 weeks focused work** |

---

## Smart additions (future)

- **AI Vision fallback** — when UPC misses, send photo to Azure AI Vision; get tags/category/text-extraction. Catches half the misses automatically.
- **Profitability scoring** — for each enriched item, compute `(est_resale − cost_share − fees) / cost_share`. Auto-flag pallets where it's smarter to lot vs. individualize.
- **Smart split** — partial individualization. UI lets you keep low-value items as a lot and pull only high-margin items out as individuals. One pallet → one lot listing + N individual listings.
- **Sold-velocity feedback** — Shopify webhook fires when items sell, writes back to `line_items.sold_at`. After a few months you have real resale data per category and can tune `est_resale` formulas.
- **Multi-marketplace** — same data model can push to eBay, Amazon, Facebook Marketplace by adding additional Functions.

---

## Why this architecture

- **Fast scan-in** — UI never blocks on slow API calls; enrichment is async
- **Cheap** — under $40/mo Azure, scales to thousands of pallets
- **Resilient** — enrichment fails? Item sits in `pending`; retry safely
- **Audit-friendly** — every row has timestamps, every API call logged
- **Plays to existing stack** — Powershell-friendly Azure CLI deploys, Entra auth (TenantIQPro tenant), M365/Power Apps for UI
- **No Shopify lock-in** — same data model can push elsewhere later
- **No subscription scanner software** — Tera/Zebra hardware is one-time spend

---

## Scanner + DB display capability

**Question:** Can the HW0009 scan a code and display product info from Azure SQL on its own LCD?

**Answer:** No, not on the scanner itself. The HW0009's built-in display is a "dumb" LCD — it only shows the raw scanned code, scan count, battery, and connection status. The scanner has no WiFi, no cellular, no network stack; it cannot query Azure SQL or any other database directly. It's a keyboard-wedge peripheral.

### How we deliver "scan → DB lookup → on-screen display"

Pair the scanner with a network-capable screen (iPad, tablet, or PC) running the Power Apps scan form. From the user's perspective it works identically to a smart scanner.

**Flow with HW0009 + iPad:**
1. Receiver scans an LPN/UPC with the HW0009
2. Code types into a "Lookup" field in Power Apps on the iPad
3. Power Apps queries Azure SQL (`lpn_catalog` first, then `line_items`, then public APIs as fallback) — sub-second over WiFi
4. iPad displays: title, image, MSRP, condition, qty in manifest, our cost
5. Receiver confirms or flags discrepancy

The iPad is the "screen" — much bigger, color, touch-capable, far more useful than the 1-inch LCD on the scanner.

### All-in-one alternatives (future upgrade option)

If receiving staff outgrow the scanner+tablet combo, professional Android handheld computers integrate scanner + screen + WiFi into one device:

| Device | Type | Approx cost | Notes |
|---|---|---:|---|
| Zebra TC22 / TC52 | Android handheld | $1,000–1,800 | Industry standard, runs Power Apps directly |
| Honeywell CT40 / CT45 | Android handheld | $1,200–2,000 | Zebra competitor |
| Datalogic Memor 11/20 | Android handheld | $900–1,400 | Cheaper Zebra alternative |
| iPhone + Socket Mobile sled | iOS + sled | $300–500 sled + phone | Lightweight |
| Unitech EA630 | Android handheld | $700–1,000 | Budget pro handheld |

Same Power Apps form runs on these unchanged — no architecture change required to upgrade later.

### Decision for NSL v1

**Stick with HW0009 + iPad.** Reasons:
- HW0009s already ordered (Rob, 2026-04-27)
- iPad is cheaper, multi-purpose (also Shopify POS, photos, email)
- Larger screen for product info + decision buttons
- Receiving cart mount (~$30) handles mobility

**Upgrade path:** add 1× Zebra TC22 for heavy receiving role if needed; keep HW0009s for POS counter and floor scanning.

## Amazon LPN — what they are

Yes, **LPN really stands for "License Plate Number"** — it's Amazon warehouse jargon for the unique tracking code applied to each unit moving through their fulfillment system. Used internally for receiving, putaway, picking, returns processing, and (relevantly for us) liquidation manifests.

- Format: typically `LPN` + 9–12 alphanumeric characters, e.g. `LPNAB123XYZ45`
- Generated per-unit (not per-SKU) — every individual physical item gets its own LPN even if 100 of them are the same product
- Not searchable on the public internet — they only exist in Amazon's internal systems and the manifests Amazon ships with liquidation pallets
- Manifest XLSX files map LPN → ASIN → product details (title, MSRP, condition, category)

This is why the Azure flow needs the `lpn_catalog` table — it's our private mirror of every LPN we've ever bought, built up by ingesting the manifests Amazon sends with each pallet. Once an LPN is in our catalog, scanning that exact item later gives us instant high-quality data with no API call.

> Sometimes confused with: Amazon FNSKU (Fulfillment Network SKU, label sellers print for FBA), or ASIN (the public product identifier). LPN is unit-level and internal-only. ASIN is product-level and public.

## Open decisions

- [x] ~~Single Entra tenant (Surya) vs. separate NSL tenant?~~ — **Decided: TenantIQPro.com tenant** (2026-04-27)
- [ ] Power Apps (faster) vs. PWA (more flexible) for scan UI v1?
- [ ] Service Bus (paid tier) vs. Storage Queue (free) for enrich queue?
- [ ] Power BI (license cost) vs. custom dashboard for review UI?
- [ ] Keepa subscription worthwhile for est_resale accuracy, or start with eBay Browse API only?
- [ ] Margin formula for individualized pricing — flat % or category-based?

---

## Next concrete step

Scaffold `infra/main.bicep` + `db/schema.sql` so the resource group can be deployed and the SQL database has its tables ready. From there, the ingest Function is the first piece of real code.

Jeff to build initial scaffold on home machine. Hardware (HW0009 ×2) ordered by Rob — should arrive before infra is ready, so receiving can dry-run scanning into Google Sheets while Azure side comes online.
