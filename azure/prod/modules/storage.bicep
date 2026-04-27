@description('Region.')
param location string

@description('Storage account name. Must be 3-24 lowercase alphanumeric, globally unique.')
param storageAccountName string

@description('Tags.')
param tags object = {}

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
    accessTier: 'Hot'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

// Photos uploaded with each scan
resource scanPhotos 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: 'scan-photos'
  properties: { publicAccess: 'None' }
}

// Inbound manifest XLSX drop zone (blob trigger picks these up)
resource manifestsIncoming 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: 'manifests-incoming'
  properties: { publicAccess: 'None' }
}

// Archived processed manifests (audit / re-import)
resource manifestsArchive 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: 'manifests-archive'
  properties: { publicAccess: 'None' }
}

// Queue for enrichment work
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2024-01-01' = {
  parent: storage
  name: 'default'
}

resource enrichQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2024-01-01' = {
  parent: queueService
  name: 'enrich-queue'
  properties: { metadata: {} }
}

resource shopifyPushQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2024-01-01' = {
  parent: queueService
  name: 'shopify-push-queue'
  properties: { metadata: {} }
}

output name string = storage.name
output resourceId string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output queueEndpoint string = storage.properties.primaryEndpoints.queue
