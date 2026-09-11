# NSL Inventory API

.NET 8 isolated worker Azure Functions that get deployed as **Static Web App managed Functions** (the `api/` directory is auto-discovered by SWA's GitHub Action and built/deployed alongside the marketing site).

## Endpoints

| Route | Method | Purpose |
|---|---|---|
| `/api/health` | GET | Liveness probe — returns SQL connectivity status |
| `/api/import-manifest` | POST | Body = XLSX bytes. Headers: `x-filename`, `x-imported-by`. Parses an Amazon B-Stock manifest, upserts into `lpn_catalog`, audits into `manifest_imports`. Idempotent on SHA-256. |
| `/api/pallets` | GET / POST | List (`v_pallets`) / create a box. Rows include `box_size`, `weight_lbs`, `live_at`, `sold_to_inventory_at`, `units_with_cost`, `condition_mix`, `highlight_*`. |
| `/api/pallets/{id}` | GET / PATCH / DELETE | Detail + items; PATCH takes `displayName`, `sellMode`, `publishState`, `listPrice`, `salePrice`, `boxSize`, `weightLbs`, …; changes to status/price/size/sell-mode are written to `manifest_history`. |
| `/api/pallets/{id}/history` | GET | Audit trail (newest first, 200 max) — `changed_at`, `changed_by`, `field`, `old_value`, `new_value`. |
| `/api/pallets/{id}/sold-to-inventory` | POST | "Sold → inventory": original reads SOLD on the site for 48 h, a Draft clone with a new BOX # and the same items is created. Returns `originalId`, `originalPalletNumber`, `cloneId`, `clonePalletNumber`, `cloneDisplayName`, `itemsCopied`. |
| `/api/items/{id}` | PATCH | Edit a line item; `isHighlight: true` features it under the box on the website. |
| `/api/public/pallets` | GET | Anonymous. Live + recently-sold boxes; adds `is_just_dropped` (live within `PalletsFunction.JustDroppedHours` = 48 h), `box_size`, `weight_lbs`, `condition_mix`, `highlight_title/msrp/photo`. Never exposes cost/wholesale/margin/notes or `sold_to_inventory_at`. |
| `/api/public/pallets/{id}/items` | GET | Anonymous manifest for the modal (cost-free; items carry `is_highlight`). |
| `/api/public/register` | POST | Anonymous member signup: `{firstName,lastName,email,phone,city,state,zip,howHeard,website}` (`website` is a honeypot). 5/min per IP. Returns `{memberNumber, alreadyRegistered}`. |
| `/api/members` | GET | Staff list of members, newest first. |
| `/api/members/export.csv` | GET | Staff CSV download (UTF-8 BOM, RFC-4180). |
| `/api/sales-summary?days=N` | GET | Square payments + boxes marked SOLD in admin (`channel: admin`, `source: admin`); `gross_cents = square_cents + admin_cents`. |

## Local development

```powershell
cd api
dotnet build
func start  # requires Azure Functions Core Tools (npm i -g azure-functions-core-tools@4)
```

`local.settings.json` is gitignored; copy from the example values and fill in your own dev SQL connection string.

## Deploy

Just `git push` — the workflow at `.github/workflows/azure-static-web-apps.yml` already deploys this directory as the SWA's managed Functions.

After first deploy, the SWA's managed Function identity needs read/write access to:

- **SQL** — `db_datareader` + `db_datawriter` + `EXECUTE`. Apply `db/grant-swa-sql-access.sql` (TBD — copy of `grant-function-app-sql-access.sql` with the principal name changed to the SWA name).
- **Storage** — `Storage Blob Data Contributor` and `Storage Queue Data Contributor` on `stnslprodoofua53czivdq`.
- **Key Vault** — `Key Vault Secrets User` on `kv-nsl-prod-nc5h2y`.

## Test the manifest importer

```powershell
$file = "C:\Users\jeffr\OneDrive\Scripts\El-heffe\ElHeffe_new\northstateliquidators\Amazon\Order Summary - 26004 - B-Stock - AMZ0N-OJ5-4G8R - 2026-01-15.xlsx"
$endpoint = 'https://northstateliquidators.com/api/import-manifest'   # or the azurestaticapps URL pre-DNS

Invoke-RestMethod -Method Post -Uri $endpoint `
  -InFile $file `
  -ContentType 'application/octet-stream' `
  -Headers @{
    'x-filename'    = (Split-Path $file -Leaf)
    'x-imported-by' = (az ad signed-in-user show --query userPrincipalName -o tsv)
  } | ConvertTo-Json
```

Expected first-run output: ~2,037 rows inserted (Amazon B-Stock manifest), 0 updated. Re-running the same file returns `duplicateOfPriorImport: true` instantly.
