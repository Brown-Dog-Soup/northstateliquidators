# North State Liquidators — Inventory System Buildout

**Project:** Scan-to-Shopify inventory pipeline on Azure
**Owner:** Jeff Blanchard / North State Liquidators
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

Azure Function, queue-triggered. For each message:
- Call **Go-UPC** (or UPCitemdb) → title, brand, description, image
- Optional: **Keepa** API → Amazon price history → est_msrp, est_resale
- Optional: **eBay Browse API** → real-world sold listings
- Optional: **Azure AI Vision** on photo → tags/category fallback when UPC misses
- Update `line_items` row, set `enrich_status='hit'` or `'miss'`
- Log API used + raw response into `enrichment_log`

Expected hit rate on liquidation inventory: **~60–70%**. Misses get flagged in the UI for manual entry.

### 4. Review dashboard

Power BI report (live to Azure SQL) or simple Static Web App:
- Per manifest: total items, enrichment hit rate, total est. resale value, total cost, projected margin
- Drill into line items: filter by `enrich_status='miss'` to fix manually
- Top of dashboard: **two big buttons** per manifest — "Sell as Lot" / "Individualize"

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

1. **Subscription decision** — separate Azure subscription under existing Surya/NCMB Entra tenant, clean billing boundary.
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
| 2 | Enrichment Function + Go-UPC integration | 2 days |
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
- **Plays to existing stack** — Powershell-friendly Azure CLI deploys, Entra auth, M365/Power Apps for UI
- **No Shopify lock-in** — same data model can push elsewhere later
- **No subscription scanner software** — Tera/Zebra hardware is one-time spend

---

## Open decisions

- [ ] Single Entra tenant (Surya) vs. separate NSL tenant?
- [ ] Power Apps (faster) vs. PWA (more flexible) for scan UI v1?
- [ ] Service Bus (paid tier) vs. Storage Queue (free) for enrich queue?
- [ ] Power BI (license cost) vs. custom dashboard for review UI?
- [ ] Keepa subscription worthwhile for est_resale accuracy, or start with eBay Browse API only?
- [ ] Margin formula for individualized pricing — flat % or category-based?

---

## Next concrete step

Scaffold `infra/main.bicep` + `db/schema.sql` so the resource group can be deployed and the SQL database has its tables ready. From there, the ingest Function is the first piece of real code.

Jeff to build initial scaffold on home machine. Hardware (HW0009 ×2) ordered by Rob — should arrive before infra is ready, so receiving can dry-run scanning into Google Sheets while Azure side comes online.
