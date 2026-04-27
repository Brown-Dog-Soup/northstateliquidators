<#
.SYNOPSIS
    Deploy the NSL marketing-website Azure infra (resource group + Static Web App).

.DESCRIPTION
    Runs `az deployment sub create` against main.bicep. Hard-aborts if the
    Azure CLI is not signed into the TenantIQpro.com Entra tenant — this
    prevents accidentally deploying NSL resources into a Surya or other
    customer subscription.

    After successful deployment, prints:
      - Resource group name
      - Static Web App default hostname (e.g. happy-flower-1234.azurestaticapps.net)
      - The deployment token (use this as the GitHub Actions secret AZURE_STATIC_WEB_APPS_API_TOKEN)
      - DNS records you need to add at GoDaddy to wire northstateliquidators.com

.PARAMETER Location
    Azure region. Default eastus2 (closest to NC, supports Free SWA).

.PARAMETER ResourceGroupName
    Resource group name. Default rg-nsl-website.

.PARAMETER StaticSiteName
    Static Web App resource name. Default stapp-nsl-website.

.PARAMETER WhatIf
    Preview the deployment without executing.

.EXAMPLE
    .\Deploy-NSLWeb.ps1
    .\Deploy-NSLWeb.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Location = 'eastus2',
    [string]$ResourceGroupName = 'rg-nsl-website',
    [string]$StaticSiteName = 'stapp-nsl-website'
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# CONFIGURATION
# ----------------------------------------------------------------------------
$EXPECTED_TENANT_ID = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'   # TenantIQpro.com
$EXPECTED_TENANT_DOMAIN = 'tenantiqpro.com'

# ----------------------------------------------------------------------------
# TENANT GUARD — fail loudly if signed into the wrong tenant
# ----------------------------------------------------------------------------
Write-Host "=== Tenant safety check ===" -ForegroundColor Cyan

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not signed into Azure CLI." -ForegroundColor Red
    Write-Host "  Run: az login --tenant $EXPECTED_TENANT_DOMAIN" -ForegroundColor Yellow
    throw 'Azure CLI not authenticated.'
}

Write-Host "Subscription: $($account.name) ($($account.id))"
Write-Host "Tenant ID:    $($account.tenantId)"
Write-Host "User:         $($account.user.name)"

if ($account.tenantId -ne $EXPECTED_TENANT_ID) {
    Write-Host ""
    Write-Host "TENANT GUARD: signed into the wrong tenant." -ForegroundColor Red
    Write-Host "  Expected: $EXPECTED_TENANT_ID ($EXPECTED_TENANT_DOMAIN)" -ForegroundColor Yellow
    Write-Host "  Got:      $($account.tenantId)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Switch with: az login --tenant $EXPECTED_TENANT_DOMAIN" -ForegroundColor Yellow
    throw 'Aborting to prevent deploying NSL into the wrong tenant.'
}
Write-Host "OK — TenantIQpro.com tenant confirmed." -ForegroundColor Green

# ----------------------------------------------------------------------------
# DEPLOY
# ----------------------------------------------------------------------------
$bicep = Join-Path $PSScriptRoot 'main.bicep'
if (-not (Test-Path $bicep)) {
    throw "main.bicep not found at $bicep"
}

$deploymentName = "nsl-website-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

Write-Host ""
Write-Host "=== Deploying Bicep ===" -ForegroundColor Cyan
Write-Host "  Deployment:    $deploymentName"
Write-Host "  Location:      $Location"
Write-Host "  Resource group: $ResourceGroupName"
Write-Host "  Static site:   $StaticSiteName"
Write-Host ""

if ($PSCmdlet.ShouldProcess("$ResourceGroupName / $StaticSiteName", 'Deploy NSL website infra')) {
    $params = @(
        "location=$Location"
        "resourceGroupName=$ResourceGroupName"
        "staticSiteName=$StaticSiteName"
    )

    az deployment sub create `
        --name $deploymentName `
        --location $Location `
        --template-file $bicep `
        --parameters @params `
        --output json | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Bicep deployment failed (exit $LASTEXITCODE)"
    }

    Write-Host "Deployment succeeded." -ForegroundColor Green
}

# ----------------------------------------------------------------------------
# POST-DEPLOY OUTPUTS
# ----------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Static Web App details ===" -ForegroundColor Cyan

$swa = az staticwebapp show --name $StaticSiteName --resource-group $ResourceGroupName --output json | ConvertFrom-Json
Write-Host "  Default hostname: $($swa.defaultHostname)"
Write-Host "  Resource ID:      $($swa.id)"

Write-Host ""
Write-Host "=== Deployment token (for GitHub Actions) ===" -ForegroundColor Cyan
$token = az staticwebapp secrets list --name $StaticSiteName --resource-group $ResourceGroupName --query 'properties.apiKey' -o tsv
Write-Host "  $token"
Write-Host ""
Write-Host "Add this as a GitHub Actions secret named AZURE_STATIC_WEB_APPS_API_TOKEN:" -ForegroundColor Yellow
Write-Host "  gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body '$token' --repo Brown-Dog-Soup/northstateliquidators" -ForegroundColor White

Write-Host ""
Write-Host "=== Next steps ===" -ForegroundColor Cyan
Write-Host "  1. Set the GitHub Actions secret (command above)"
Write-Host "  2. Copy azure-static-web-apps.yml.template into .github/workflows/azure-static-web-apps.yml and commit"
Write-Host "  3. Push: GitHub Action will deploy index.html to https://$($swa.defaultHostname)"
Write-Host "  4. In Azure portal: SWA -> Custom domains -> add northstateliquidators.com (apex) and www"
Write-Host "  5. Add the TXT verification records at GoDaddy via Set-GoDaddyDns.ps1"
Write-Host "  6. After verification, swap apex A records (currently 185.199.108-111.153) to the SWA-provided IP"
Write-Host "  7. Switch www CNAME from brown-dog-soup.github.io -> $($swa.defaultHostname)"
Write-Host ""
Write-Host "See AZURE-MIGRATION.md for the full DNS-swap runbook." -ForegroundColor Gray
