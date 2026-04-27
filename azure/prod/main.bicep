// ============================================================================
// NSL — Inventory Pipeline (Phase 0 infra)
//
// Subscription-scoped deployment that creates `rg-nsl-prod` and provisions
// the backend pieces of the scan-to-Shopify pipeline:
//   - Log Analytics + Application Insights (observability)
//   - Storage Account (Blob for photos, Queue for enrichment work)
//   - Key Vault (Shopify token, UPC API keys, Keepa key, etc.)
//   - SQL Server + Database Basic 2GB (manifests / line_items / lpn_catalog)
//   - Function App on Consumption plan (.NET 9 isolated worker)
//
// Tenant: TenantIQpro.com (Entra ID).
//   Hard tenant guard enforced at deploy script level (Deploy-NSLProd.ps1).
//
// Companion to azure/website/ (rg-nsl-website is the marketing site only).
// ============================================================================

targetScope = 'subscription'

@description('Azure region. Pick one with cheap Azure SQL Basic + Functions Consumption availability.')
param location string = 'eastus2'

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
var functionAppName    = 'func-${namePrefix}-api'
var functionPlanName   = 'plan-${namePrefix}-prod'

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
    location: location
    sqlServerName: sqlServerName
    sqlDatabaseName: sqlDatabaseName
    tenantId: subscription().tenantId
    sqlAdminEntraObjectId: sqlAdminEntraObjectId
    sqlAdminEntraLogin: sqlAdminEntraLogin
    tags: tags
  }
}

module fn 'modules/function-app.bicep' = {
  scope: rg
  name: 'deploy-function-app'
  params: {
    location: location
    functionAppName: functionAppName
    functionPlanName: functionPlanName
    storageAccountName: storage.outputs.name
    appInsightsConnectionString: logs.outputs.appInsightsConnectionString
    sqlConnectionString: sql.outputs.connectionString
    keyVaultName: kv.outputs.name
    tags: tags
  }
}

// ----------------------------------------------------------------------------
// RBAC: grant the Function App's managed identity access to Storage + Key Vault
// ----------------------------------------------------------------------------
module rbac 'modules/rbac.bicep' = {
  scope: rg
  name: 'deploy-rbac'
  params: {
    storageAccountName: storage.outputs.name
    keyVaultName: kv.outputs.name
    functionAppPrincipalId: fn.outputs.principalId
  }
}

// ----------------------------------------------------------------------------
// Outputs
// ----------------------------------------------------------------------------
output resourceGroupName string = rg.name
output storageAccountName string = storage.outputs.name
output keyVaultName string = kv.outputs.name
output sqlServerName string = sql.outputs.serverName
output sqlServerFqdn string = sql.outputs.fqdn
output sqlDatabaseName string = sql.outputs.databaseName
output functionAppName string = fn.outputs.name
output functionAppHostname string = fn.outputs.defaultHostname
output appInsightsName string = logs.outputs.appInsightsName
output logWorkspaceName string = logs.outputs.workspaceName
