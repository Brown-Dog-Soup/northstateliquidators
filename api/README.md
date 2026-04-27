# NSL Inventory API

.NET 8 isolated worker Azure Functions that get deployed as **Static Web App managed Functions** (the `api/` directory is auto-discovered by SWA's GitHub Action and built/deployed alongside the marketing site).

## Endpoints

| Route | Method | Purpose |
|---|---|---|
| `/api/health` | GET | Liveness probe — returns SQL connectivity status |
| `/api/import-manifest` | POST | Body = XLSX bytes. Headers: `x-filename`, `x-imported-by`. Parses an Amazon B-Stock manifest, upserts into `lpn_catalog`, audits into `manifest_imports`. Idempotent on SHA-256. |

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
