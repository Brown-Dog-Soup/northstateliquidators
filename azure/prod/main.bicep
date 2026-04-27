// ============================================================================
// NSL — Inventory Pipeline (Phase 0 infra)
//
// Subscription-scoped deployment that creates `rg-nsl-prod` and provisions
// the BACKEND DATA-PLANE pieces of the scan-to-Shopify pipeline:
//   - Log Analytics + Application Insights (observability)
//   - Storage Account (Blob for photos, Queue for enrichment work)
//   - Key Vault (Shopify token, UPC API keys, Keepa key, etc.)
//   - SQL Server + Database Basic 2GB (manifests / line_items / lpn_catalog)
//
// API COMPUTE LIVES IN THE STATIC WEB APP (rg-nsl-website / stapp-nsl-website
// Standard SKU) as managed Functions out of the repo's api/ directory.
// This avoids the App Service Y1/B1 quota wall on the "Application"
// subscription. The SWA's managed Function identity gets RBAC to the
// resources here (granted post-deploy by Grant-SwaAccess.ps1).
//
// Tenant: TenantIQpro.com (Entra ID).
//   Hard tenant guard enforced at deploy script level (Deploy-NSLProd.ps1).
// ============================================================================

targetScope = 'subscription'

@description('Azure region for the resource group + most resources.')
param location string = 'eastus2'

@description('Override region for Azure SQL Server. Some regions throttle new SQL Server creates; use this to pin SQL to a different region without moving everything else. Default: same as location.')
param sqlLocation string = location

@description('Resource group name for backend pipeline.')
param resourceGroupName string = 'rg-nsl-prod'

@description('Short prefix used in derived resource names.')
param namePrefix string = 'nsl'

@description('Object ID of the Entra user/group that should be SQL Server Entra Admin. Look up: az ad user show --id jeffrey.blanchard@tenantiqpro.com --query id -o tsv')
param sqlAdminEntraObjectId string

@description('UPN/display name of the Entra SQL admin (shown in portal).')
param sqlAdminEntraLogin string = 'jeffrey.blanchard@tenantiqpro.com'

@description('Tags applied to RG and resources.')
param tags object = {
  project: 'north-state-liquidators'
  component: 'inventory-pipeline'
  tenant: 'TenantIQpro.com'
  owner: 'TenantIQ Pro LLC'
  managed_by: 'Bicep'
}

// Derived names — kept short, lowercase, alphanum only where Azure requires it
var storageAccountName = toLower(replace('st${namePrefix}prod${uniqueString(subscription().id, namePrefix)}', '-', ''))
var keyVaultName       = 'kv-${namePrefix}-prod-${take(uniqueString(subscription().id), 6)}'
var sqlServerName      = 'sql-${namePrefix}-prod-${take(uniqueString(subscription().id), 6)}'
var sqlDatabaseName    = 'sqldb-${namePrefix}-prod'
var logWorkspaceName   = 'log-${namePrefix}-prod'
var appInsightsName    = 'appi-${namePrefix}-prod'

// ----------------------------------------------------------------------------
// Resource group
// ----------------------------------------------------------------------------
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// ----------------------------------------------------------------------------
// Modules
// ----------------------------------------------------------------------------

module logs 'modules/log-analytics.bicep' = {
  scope: rg
  name: 'deploy-log-analytics'
  params: {
    location: location
    workspaceName: logWorkspaceName
    appInsightsName: appInsightsName
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'deploy-storage'
  params: {
    location: location
    storageAccountName: storageAccountName
    tags: tags
  }
}

module kv 'modules/key-vault.bicep' = {
  scope: rg
  name: 'deploy-key-vault'
  params: {
    location: location
    keyVaultName: keyVaultName
    tenantId: subscription().tenantId
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  scope: rg
  name: 'deploy-sql'
  params: {
    location: sqlLocation
    sqlServerName: sqlServerName
    sqlDatabaseName: sqlDatabaseName
    tenantId: subscription().tenantId
    sqlAdminEntraObjectId: sqlAdminEntraObjectId
    sqlAdminEntraLogin: sqlAdminEntraLogin
    tags: tags
  }
}

// ----------------------------------------------------------------------------
// Outputs — used by Grant-SwaAccess.ps1 to wire SWA managed Function identity
// ----------------------------------------------------------------------------
output resourceGroupName string = rg.name
output storageAccountName string = storage.outputs.name
output keyVaultName string = kv.outputs.name
output sqlServerName string = sql.outputs.serverName
output sqlServerFqdn string = sql.outputs.fqdn
output sqlDatabaseName string = sql.outputs.databaseName
output sqlConnectionString string = sql.outputs.connectionString
output appInsightsName string = logs.outputs.appInsightsName
output appInsightsConnectionString string = logs.outputs.appInsightsConnectionString
output logWorkspaceName string = logs.outputs.workspaceName
