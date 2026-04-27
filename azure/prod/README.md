# azure/prod

Bicep + deploy script for the NSL inventory-pipeline backend resource group `rg-nsl-prod`.

## What gets deployed

| Resource | Type | SKU / Plan | Approx $/mo |
|---|---|---:|---:|
| `log-nsl-prod` | Log Analytics workspace | PerGB2018, 30d retention | $0–5 |
| `appi-nsl-prod` | Application Insights | workspace-based | $0–10 |
| `st<unique>` | Storage account | Standard_LRS, StorageV2 | $1–5 |
| `kv-nsl-prod-xxxxxx` | Key Vault | Standard, RBAC auth | $0–1 |
| `sql-nsl-prod-xxxxxx` | Azure SQL Server | Entra-only auth | $0 (server is free) |
| `sqldb-nsl-prod` | Azure SQL Database | Basic, 2GB | $5 |
| `plan-nsl-prod` | App Service plan | Y1 Consumption (Functions) | $0–20 (free grant covers low volume) |
| `func-nsl-api` | Function App | .NET 9 isolated worker | included |
| **Subtotal** | | | **~$10–40/mo** |

System-assigned managed identity on the Function App with RBAC to:
- Storage Blob Data Contributor (scan-photos, manifests-incoming, manifests-archive containers)
- Storage Queue Data Contributor (enrich-queue, shopify-push-queue)
- Key Vault Secrets User (Shopify token, Go-UPC API key, Keepa key)
- SQL Server `db_datareader` + `db_datawriter` + `EXECUTE` (granted via T-SQL post-deploy, see `db/grant-function-app-sql-access.sql`)

## Files

| File | Purpose |
|---|---|
| `main.bicep` | Subscription-scoped — creates RG + calls modules |
| `modules/log-analytics.bicep` | Log Analytics workspace + App Insights |
| `modules/storage.bicep` | Storage account, blob containers, queues |
| `modules/key-vault.bicep` | Key Vault (RBAC auth) |
| `modules/sql.bicep` | Azure SQL Server (Entra-only) + database (Basic 2GB) |
| `modules/function-app.bicep` | Consumption plan + Function App with managed identity |
| `modules/rbac.bicep` | Role assignments for Function App MI on Storage + Key Vault |
| `Deploy-NSLProd.ps1` | Wrapper script. Tenant guard, looks up signed-in user as SQL admin, runs `az deployment sub create` |

## Deploy

```powershell
# One-time prereq
az login --tenant tenantiqpro.com

# Preview
cd azure\prod
.\Deploy-NSLProd.ps1 -WhatIf

# Provision
.\Deploy-NSLProd.ps1
```

Deploy time: ~5–10 min (SQL Server + Database take the longest).

## Post-deploy

After deploy succeeds:

```powershell
# 1. Apply DB schema
$server = az sql server list -g rg-nsl-prod --query '[0].fullyQualifiedDomainName' -o tsv
sqlcmd -S $server -d sqldb-nsl-prod -G -i ..\..\db\schema.sql

# 2. Grant Function App MI access to SQL
sqlcmd -S $server -d sqldb-nsl-prod -G -i ..\..\db\grant-function-app-sql-access.sql

# 3. Add secrets to Key Vault (only what's needed initially)
$kv = az keyvault list -g rg-nsl-prod --query '[0].name' -o tsv
az keyvault secret set --vault-name $kv --name 'shopify-admin-token' --value '<shpat_...>'
az keyvault secret set --vault-name $kv --name 'go-upc-api-key'      --value '<key>'

# 4. Build and deploy Function App code
#    (separate step — Function App code lives in azure/api/, deployed via
#     `func azure functionapp publish func-nsl-api` once we write it)
```

## Tenant guard

The deploy script aborts hard if the Azure CLI is not signed into the TenantIQpro.com tenant
(`d9b645c3-3587-4cd4-be9b-1a8d405c92ad`). Same pattern as `azure/website/Deploy-NSLWeb.ps1`.

Every resource is tagged `tenant: TenantIQpro.com`.
