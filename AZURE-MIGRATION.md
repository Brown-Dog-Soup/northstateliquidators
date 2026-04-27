# Azure Migration — Marketing Website

Move `northstateliquidators.com` from GitHub Pages → Azure Static Web Apps, on the **TenantIQpro.com** Entra tenant, in a dedicated resource group `rg-nsl-website`.

This document is the runbook. The Bicep, deploy script, and workflow template live in `azure/website/`.

---

## Why move

- **Future-proofing for the inventory pipeline.** The BuildoutDB.md plan provisions an Azure resource group with Functions, SQL, Storage, etc. Putting the website on Azure now keeps the entire NSL stack on one tenant + one provider.
- **Pages cleared its purpose.** The GitHub Pages stint was free + fast and got us a live brand site in days. We've taken it as far as it goes — adding form endpoints, auth, or APIs requires server-side compute.
- **Single identity boundary.** Both website and inventory pipeline live under TenantIQpro.com; same Entra ID, same Key Vault patterns, same RBAC model.
- **Custom-domain handling stays clean.** SWA does free Let's Encrypt + supports apex domains via Microsoft's published IP.

GitHub Pages stays as the second source of truth (the repo is still public, code is unchanged) — we're just changing where DNS points.

---

## Target architecture

| Item | Value |
|---|---|
| Tenant | TenantIQpro.com (`d9b645c3-3587-4cd4-be9b-1a8d405c92ad`) |
| Subscription | NSL subscription under TenantIQ Pro org (or whichever subscription is already tied to the tenant) |
| Resource group | `rg-nsl-website` (this RG only — inventory pipeline gets `rg-nsl-prod` later) |
| Region | `eastus2` (closest to NC, supports Free SWA) |
| Service | Microsoft.Web/staticSites (Free SKU) |
| Resource name | `stapp-nsl-website` |
| Repo source | Brown-Dog-Soup/northstateliquidators main branch root |
| Custom domain | `northstateliquidators.com` (apex) + `www.northstateliquidators.com` (CNAME) |
| TLS | Free, auto-renewing Let's Encrypt cert provided by SWA |

---

## Prerequisites (one-time)

1. **Azure CLI installed.** `winget install -e --id Microsoft.AzureCLI` if not present.
2. **Sign into the right tenant.** `az login --tenant tenantiqpro.com`. The deploy script aborts loudly if you're signed into anything else.
3. **Azure subscription.** A pay-as-you-go or other commercial subscription tied to the TenantIQ Pro tenant. If one doesn't exist, create one in https://portal.azure.com → Subscriptions → Add. Free tier exists for Static Web Apps; subscription only matters for billing-id correctness.
4. **GitHub CLI authenticated.** `gh auth status` — used to set the deployment-token secret on the repo.

---

## Phase 1 — Provision (low risk, no DNS impact)

```powershell
cd C:\Users\jeffr\OneDrive\Scripts\El-heffe\ElHeffe_new\northstateliquidators\azure\website
.\Deploy-NSLWeb.ps1 -WhatIf       # preview
.\Deploy-NSLWeb.ps1                # commit
```

What this does:
- Verifies tenant ID matches `d9b645c3-…` (TenantIQpro.com); aborts otherwise
- Creates `rg-nsl-website` if missing
- Deploys `stapp-nsl-website` Static Web App (Free SKU)
- Prints the assigned `<random>-<random>-<region>.azurestaticapps.net` hostname
- Prints the deployment token

Cost so far: $0/mo (Free SWA tier, no traffic).

---

## Phase 2 — Wire GitHub Actions (still no DNS impact)

```powershell
# 1. Set the deployment token as a repo secret
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN `
  --body '<token-from-Deploy-NSLWeb-output>' `
  --repo Brown-Dog-Soup/northstateliquidators

# 2. Copy the workflow template to .github/workflows/
mkdir ..\..\.github\workflows -ErrorAction SilentlyContinue
copy .\azure-static-web-apps.yml.template ..\..\.github\workflows\azure-static-web-apps.yml

# 3. Commit + push
cd ..\..
git add .github/workflows/azure-static-web-apps.yml
git commit -m "Wire Azure Static Web Apps deployment workflow"
git push
```

Within ~2 min, the GitHub Action runs and deploys `index.html` to the SWA's azurestaticapps.net hostname. **Verify by hitting that URL in a browser** — should look identical to the current GitHub Pages site.

GitHub Pages is **still serving the production traffic** at this point. Both deployments running in parallel.

---

## Phase 3 — Add custom domain to SWA (still no DNS impact)

Via Azure portal (UI is more reliable than CLI here for cert orchestration):

1. Open the SWA in the portal → **Custom domains** → **Add**
2. Add `northstateliquidators.com` → choose **TXT validation**
3. Azure generates a TXT record like `MS=ms-...` plus instructions
4. Add the TXT record to GoDaddy:

```powershell
$env:GODADDY_KEY    = '<from harpercallahanbooks/CLAUDE.md>'
$env:GODADDY_SECRET = '<from harpercallahanbooks/CLAUDE.md>'

# Read existing TXT @ first so we don't wipe SPF / M365-verification / GitHub-Pages-verification records
.\Set-GoDaddyDns.ps1 -Domain northstateliquidators.com -Type TXT -Name '@' `
  -Values 'MS=ms52758767', `
          'v=spf1 include:spf.protection.outlook.com -all', `
          '<the-azure-validation-TXT>' `
  -Ttl 600
```

5. Click **Verify** in the portal. Once verified, SWA gives you the **apex IP address** to point the A records at.

6. Repeat for `www.northstateliquidators.com` → choose **CNAME validation** → SWA gives a CNAME-validation token. Add and verify.

Production traffic still on GitHub Pages.

---

## Phase 4 — DNS swap-over (the cutover)

This is the one moment that matters. Do this when traffic is low (early morning weekend) and have the rollback steps copy-pasted ready in a second terminal.

### 4a. Pre-swap checklist

- [ ] SWA is serving correctly at the azurestaticapps.net hostname
- [ ] `northstateliquidators.com` and `www.northstateliquidators.com` both verified in SWA portal
- [ ] SWA has issued the apex IP and CNAME target
- [ ] TTL on existing GoDaddy records lowered to 600s **at least 1 hour before swap**

### 4b. The swap

```powershell
# Replace the four GitHub Pages A records on apex with the single Azure SWA IP
.\Set-GoDaddyDns.ps1 -Domain northstateliquidators.com -Type A -Name '@' `
  -Values '<azure-swa-apex-ip>' -Ttl 600

# Switch www CNAME from GitHub Pages to Azure SWA
.\Set-GoDaddyDns.ps1 -Domain northstateliquidators.com -Type CNAME -Name 'www' `
  -Values '<random>.<region>.azurestaticapps.net' -Ttl 600
```

Within 5–15 min, public DNS resolvers pick up the change. The site is now served by Azure.

### 4c. Smoke test

```powershell
# After waiting 5 min, dig from a public resolver
nslookup northstateliquidators.com 8.8.8.8

# Expect: a single Azure IP (NOT the four 185.199.108-111.153 GitHub IPs)

curl -sI https://northstateliquidators.com | Select-Object -First 5
# Expect: HTTP/1.1 200 with `Server: Microsoft-IIS/...` or similar (NOT `Server: GitHub.com`)

curl -sI https://www.northstateliquidators.com | Select-Object -First 5
# Expect: 200 or 301 -> apex
```

### 4d. Rollback (if smoke test fails)

```powershell
# Restore GitHub Pages A records
.\Set-GoDaddyDns.ps1 -Domain northstateliquidators.com -Type A -Name '@' `
  -Values '185.199.108.153','185.199.109.153','185.199.110.153','185.199.111.153' -Ttl 600

# Restore www CNAME
.\Set-GoDaddyDns.ps1 -Domain northstateliquidators.com -Type CNAME -Name 'www' `
  -Values 'brown-dog-soup.github.io' -Ttl 600
```

DNS reverts in 5–15 min. GitHub Pages still active and ready to serve.

---

## Phase 5 — Cleanup (after 48 hours of stable Azure traffic)

- [ ] Disable GitHub Pages on the repo (`gh api -X DELETE repos/Brown-Dog-Soup/northstateliquidators/pages`) — or leave it on as a warm spare
- [ ] Bump the GoDaddy TTL back up to 3600 on apex A and www CNAME
- [ ] Update `PROJECT.md` Domain & Hosting section to reflect Azure as the active host
- [ ] Remove `CNAME` file from the repo (was a GitHub Pages requirement; SWA ignores it)

---

## Things that DO NOT change

- The repo (`Brown-Dog-Soup/northstateliquidators`) — same source code, same workflow
- The marketing site files (`index.html`, `mockups/*`, `Sync-NSLFeatured.ps1`, etc.)
- M365 email DNS records (MX, SPF, DKIM, DMARC, autodiscover) — these stay at GoDaddy and continue routing email to TenantIQ Pro's M365 tenant
- The Shopify dev store + theme — unrelated to this hosting move
- The product-sync workflow — `Sync-NSLFeatured.ps1` works the same against either GitHub Pages or Azure SWA

---

## Cost delta

| Component | GitHub Pages | Azure SWA |
|---|---:|---:|
| Hosting | $0/mo | $0/mo (Free SKU) |
| TLS cert | $0 | $0 |
| Bandwidth | 100GB/mo soft | 100GB/mo Free SKU |
| Build minutes | unlimited | unlimited (build runs on GitHub Actions side) |
| Custom domain | yes | yes |
| Form endpoints | none | available via Functions in `rg-nsl-prod` later |
| Private repo support | requires Pro $4/mo | included on Free |

Net: $0 change today; opens the door to private-repo + serverless API endpoints later without additional spend.
