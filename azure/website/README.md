# azure/website

Bicep + deploy script for the NSL marketing-website resource group.

## Files

| File | Purpose |
|---|---|
| `main.bicep` | Subscription-scoped Bicep that creates `rg-nsl-website` and deploys the Static Web App via the module |
| `modules/static-web-app.bicep` | The actual `Microsoft.Web/staticSites` resource definition |
| `Deploy-NSLWeb.ps1` | Wrapper script. Asserts TenantIQpro.com tenant, runs `az deployment sub create`, prints the deployment token + DNS instructions |
| `azure-static-web-apps.yml.template` | GitHub Actions workflow template — copy to `.github/workflows/` after first deploy and add the deployment token as a repo secret |

## Usage

```powershell
# One-time prereq
az login --tenant tenantiqpro.com

# Deploy
cd azure\website
.\Deploy-NSLWeb.ps1 -WhatIf      # preview
.\Deploy-NSLWeb.ps1               # provision

# Wire GitHub Actions
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body '<token from output>' --repo Brown-Dog-Soup/northstateliquidators
copy .\azure-static-web-apps.yml.template ..\..\.github\workflows\azure-static-web-apps.yml
git add .github/workflows/azure-static-web-apps.yml
git commit -m "Wire Azure Static Web Apps deployment workflow"
git push
```

See [../../AZURE-MIGRATION.md](../../AZURE-MIGRATION.md) for the full DNS-swap runbook and rollback steps.
