<#
.SYNOPSIS
    Deploy the NSL inventory-pipeline Azure backend (rg-nsl-prod).

.DESCRIPTION
    Subscription-scoped Bicep deployment that creates rg-nsl-prod and provisions
    Log Analytics + App Insights + Storage Account + Key Vault + Azure SQL +
    Function App with system-assigned managed identity. RBAC for the Function
    App's managed identity is set on Storage and Key Vault.

    Hard-aborts if the Azure CLI is not signed into the TenantIQpro.com Entra
    tenant — same guard as the website deploy script.

.PARAMETER Location
    Azure region. Default eastus2.

.PARAMETER ResourceGroupName
    Resource group name. Default rg-nsl-prod.

.PARAMETER WhatIf
    Preview without executing.

.EXAMPLE
    .\Deploy-NSLProd.ps1 -WhatIf
    .\Deploy-NSLProd.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Location = 'eastus2',
    [string]$ResourceGroupName = 'rg-nsl-prod'
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# CONFIGURATION
# ----------------------------------------------------------------------------
$EXPECTED_TENANT_ID = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'   # TenantIQpro.com
$EXPECTED_TENANT_DOMAIN = 'tenantiqpro.com'

# ----------------------------------------------------------------------------
# TENANT GUARD
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
    Write-Host "TENANT GUARD: signed into the wrong tenant." -ForegroundColor Red
    Write-Host "  Expected: $EXPECTED_TENANT_ID ($EXPECTED_TENANT_DOMAIN)" -ForegroundColor Yellow
    Write-Host "  Got:      $($account.tenantId)" -ForegroundColor Yellow
    Write-Host "  Switch with: az login --tenant $EXPECTED_TENANT_DOMAIN" -ForegroundColor Yellow
    throw 'Aborting to prevent deploying NSL into the wrong tenant.'
}
Write-Host "OK — TenantIQpro.com tenant confirmed." -ForegroundColor Green

# ----------------------------------------------------------------------------
# Look up the Entra Object ID for the SQL admin (current signed-in user)
# ----------------------------------------------------------------------------
$sqlAdminUpn = $account.user.name
Write-Host ""
Write-Host "=== SQL admin Entra principal ===" -ForegroundColor Cyan
$sqlAdminOid = az ad signed-in-user show --query id -o tsv 2>$null
if (-not $sqlAdminOid) {
    throw "Could not resolve current Entra user object ID. Are you signed in interactively (not as a service principal)?"
}
Write-Host "  UPN:       $sqlAdminUpn"
Write-Host "  Object ID: $sqlAdminOid"

# ----------------------------------------------------------------------------
# DEPLOY
# ----------------------------------------------------------------------------
$bicep = Join-Path $PSScriptRoot 'main.bicep'
if (-not (Test-Path $bicep)) {
    throw "main.bicep not found at $bicep"
}

$deploymentName = "nsl-prod-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

Write-Host ""
Write-Host "=== Deploying Bicep ===" -ForegroundColor Cyan
Write-Host "  Deployment:       $deploymentName"
Write-Host "  Location:         $Location"
Write-Host "  Resource group:   $ResourceGroupName"
Write-Host "  SQL Entra admin:  $sqlAdminUpn"
Write-Host ""

if ($PSCmdlet.ShouldProcess($ResourceGroupName, 'Deploy NSL inventory-pipeline backend')) {
    $params = @(
        "location=$Location"
        "resourceGroupName=$ResourceGroupName"
        "sqlAdminEntraObjectId=$sqlAdminOid"
        "sqlAdminEntraLogin=$sqlAdminUpn"
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
if (-not $PSCmdlet.WhatIf -and -not $WhatIfPreference) {
    Write-Host ""
    Write-Host "=== Resource summary ===" -ForegroundColor Cyan
    $outputs = az deployment sub show --name $deploymentName --query 'properties.outputs' -o json | ConvertFrom-Json
    foreach ($k in ($outputs | Get-Member -MemberType NoteProperty).Name) {
        $v = $outputs.$k.value
        Write-Host ("  {0,-22} {1}" -f $k, $v)
    }

    Write-Host ""
    Write-Host "=== Next steps ===" -ForegroundColor Cyan
    Write-Host "  1. Apply the SQL schema (db/schema.sql) to the new database:" -ForegroundColor White
    Write-Host "       sqlcmd -S $($outputs.sqlServerFqdn.value) -d $($outputs.sqlDatabaseName.value) -G -i ..\..\db\schema.sql" -ForegroundColor DarkGray
    Write-Host "  2. Grant the Function App's managed identity SQL data-reader/writer access:" -ForegroundColor White
    Write-Host "       (see post-deploy step in db/grant-function-app-sql-access.sql)" -ForegroundColor DarkGray
    Write-Host "  3. Add secrets to Key Vault: Shopify token, Go-UPC API key, etc." -ForegroundColor White
    Write-Host "  4. Build + deploy the Function App code (separate step, not in this Bicep)." -ForegroundColor White
}
