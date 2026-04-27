@description('Region.')
param location string

@description('Function App name.')
param functionAppName string

@description('Consumption plan name.')
param functionPlanName string

@description('Storage account name (used for AzureWebJobsStorage).')
param storageAccountName string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('SQL connection string (Active Directory Default auth via the Function App managed identity).')
param sqlConnectionString string

@description('Key Vault name (Function App reads secrets from here via managed identity).')
param keyVaultName string

@description('Tags.')
param tags object = {}

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: storageAccountName
}

// B1 Basic Linux App Service Plan instead of Y1 Consumption — dedicated
// compute (~$13/mo) avoids the per-region Dynamic-VMs quota wall that new
// Application subscriptions hit.
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: functionPlanName
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: { reserved: true }
}

resource fn 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|9.0'
      alwaysOn: true
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storage.listKeys().keys[0].value}' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'SqlConnectionString', value: sqlConnectionString }
        { name: 'KeyVaultUri', value: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}' }
        { name: 'EnrichQueueName', value: 'enrich-queue' }
        { name: 'ShopifyPushQueueName', value: 'shopify-push-queue' }
        { name: 'ScanPhotosContainer', value: 'scan-photos' }
        { name: 'ManifestsIncomingContainer', value: 'manifests-incoming' }
        { name: 'ManifestsArchiveContainer', value: 'manifests-archive' }
      ]
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
  }
}

output name string = fn.name
output defaultHostname string = fn.properties.defaultHostName
output principalId string = fn.identity.principalId
