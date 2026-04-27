@description('Region.')
param location string

@description('Key Vault name. 3-24 chars, alphanumeric and dashes.')
param keyVaultName string

@description('Tenant ID for the Key Vault.')
param tenantId string

@description('Tags.')
param tags object = {}

resource kv 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

output name string = kv.name
output resourceId string = kv.id
output uri string = kv.properties.vaultUri
