# Scan UI v1 — Power Apps vs PWA

Decision doc for the inventory-pipeline scan UI (the receiver-facing form that gets typed into when an HW0009 scanner reads an LPN/UPC).

**Decision:** **Power Apps for v1**, plan migration to PWA on Azure Static Web Apps when triggered by one of the criteria below.

---

## Context

- **Tenant:** TenantIQpro.com (Entra ID) — both the receivers' identities (norm@, rob@) and the back-end Azure SQL live here, so SSO is free.
- **Hardware:** Tera HW0009 ×2 (ordered 2026-04-27) + iPad/tablet running the scan form.
- **Backend:** Azure SQL `manifests` / `line_items` / `lpn_catalog` tables; ingest behind an Azure Function POST endpoint.
- **Users at start:** Norm + Rob + occasional helper. ≤3 active users for the foreseeable.

---

## Option A — Power Apps canvas app

| | |
|---|---|
| Build time | **2 days** (canvas controls, SQL connector, scan field, photo, save) |
| License model | Power Apps Premium per-user $20/user/mo OR per-app $10/app/user (which works for our usage pattern) |
| Cost @ 3 users | **~$30/mo** (3 × $10 per-app) |
| Auth | Entra ID native — users sign in with norm@, rob@ |
| Connector to Azure SQL | Premium connector, included in the per-app license |
| Photo capture | Native camera control |
| Offline | **Limited** — Power Apps offline supports cached records but not a full disconnected workflow |
| Mobile deploy | Power Apps mobile app (download from App Store / Play Store) |
| Form designer | Drag-drop, no JS needed |
| Iteration speed | Fast — instant publish, no build pipeline |

**Strengths:** validated workflow in 2 days, Entra integration is one click, photo + scan + save UI is mostly drag-drop. Norm and Rob already have to install the Outlook + Shopify apps; "install one more app" is fine.

**Weaknesses:** licensing cost grows linearly with users. Limited offline. Not in the same git repo as everything else (Power Apps lives in M365 Power Platform, exported as a `.msapp` file). Migrating away later means rewriting in PWA.

---

## Option B — PWA on Azure Static Web App

| | |
|---|---|
| Build time | **1–2 weeks** (form, scan field, camera capture, IndexedDB, service worker, Entra auth, SQL via Function) |
| License model | $0 — runs on the same Static Web App we're already standing up |
| Cost @ 3 users | **$0/mo** |
| Auth | Entra ID via Static Web App built-in auth + custom Function endpoint validation |
| Connector to Azure SQL | Custom Azure Function (managed identity → SQL) |
| Photo capture | `<input type="file" capture="camera">` or MediaDevices.getUserMedia |
| Offline | Full — service worker + IndexedDB queue, syncs on reconnect |
| Mobile deploy | Add to home screen; no app store |
| Form designer | Hand-rolled HTML/JS |
| Iteration speed | Push-to-deploy via GitHub Actions |

**Strengths:** $0 incremental, full offline, source-controlled in git alongside everything else. No per-user license tax as the team grows. Better polish potential — full HTML/CSS control matching the marketing site's branding.

**Weaknesses:** ~10× the build time vs Power Apps. Need to write Entra-auth flow, SQL Function endpoints, and the service-worker offline queue from scratch. UI design is on us.

---

## Decision matrix

| Factor | Weight | Power Apps | PWA |
|---|---:|---:|---:|
| Time-to-validate (days to first scan-in flow working) | High | **A** (2d) | B (10d) |
| Recurring cost at 3 users | Med | A ($30/mo) | **B ($0/mo)** |
| Recurring cost at 10 users | Med | A ($100/mo) | **B ($0/mo)** |
| Offline capability | High | B (limited) | **B (full)** |
| Time to migrate workflow if requirements change | Med | **A** (drag-drop) | B (code change) |
| Source-control + audit | Low | B (.msapp export) | **B (git native)** |
| Branding control | Low | A (Power Apps theme limited) | **B (full CSS)** |

Verdict for v1: **Power Apps wins on the only thing that matters right now — speed to validation.** Inventory pipeline doesn't exist yet; pricing and category enrichment logic is unproven. Fastest path to "scan a real pallet, see what breaks" is Power Apps. The $30/mo penalty during the validation phase is fine.

---

## Migration triggers (when to switch v1 → PWA)

Migrate to PWA when ANY of the following becomes true:

1. **User count grows past 3.** A 4th user (warehouse helper, additional admin) makes Power Apps licensing crossover painful.
2. **Offline becomes mission-critical.** If receivers report they can't scan when WiFi flakes, the PWA's IndexedDB queue beats Power Apps' partial offline.
3. **Workflow has stabilized.** Once we've shipped 5+ pallets through Power Apps and the categories/conditions/edge-cases are mapped, the PWA can be a faithful clone with low risk of rework.
4. **Multi-channel push needs serverless work anyway.** When the inventory pipeline grows to push to eBay/Facebook Marketplace, we'll be writing Functions regardless — adding a PWA front-end is incremental.

The PWA migration itself is ~1 sprint of focused work. Plan for it as a known future cost, not a surprise.

---

## v1 build outline (Power Apps)

Single canvas screen:

```
┌─────────────────────────────────────────────────┐
│ NSL Receiving · Manifest: AMZ_3PL_20251121_020 │
├─────────────────────────────────────────────────┤
│  Scan input (autofocus)  [_______________]     │
│                                                 │
│  Lookup result:                                 │
│    Title:      Madam Uniq Sequin Dress         │
│    LPN:        LPNNE5DFW396S                    │
│    Condition:  USED_GOOD                        │
│    MSRP:       $77.39                           │
│    From mfst:  ✓                                │
│                                                 │
│  Override condition: [ As manifest ▼ ]         │
│  Photo:              [ 📷 Tap to capture ]     │
│  Notes:              [ ____________ ]           │
│                                                 │
│       [ SKIP ]      [ CONFIRM SCAN ]           │
└─────────────────────────────────────────────────┘
```

Workflow:
1. Scanner fires → barcode types into Scan input → onChange triggers
2. Power Apps queries `lpn_catalog` (premium SQL connector) → if hit, populates Lookup result fields
3. If miss, queries `line_items` for partial match by UPC → if still miss, marks as new with `enrich_status='pending'`
4. Receiver hits CONFIRM → POST to Azure Function `/api/scan` with payload + photo
5. Form clears, ready for next scan

Estimated build: 2 days, including connecting to the SQL connector, designing the screen, and testing on the iPad with a real HW0009.

---

## Open question

The Power Apps canvas app should be exported (`.msapp`) and committed to this repo so the design isn't trapped in M365 Power Platform alone. Add a step to the build process to do that export weekly or after major edits.
