@description('Storage account name.')
param storageAccountName string

@description('Key Vault name.')
param keyVaultName string

@description('Function App managed identity principal (object) ID.')
param functionAppPrincipalId string

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: storageAccountName
}

resource kv 'Microsoft.KeyVault/vaults@2024-04-01-preview' existing = {
  name: keyVaultName
}

// Built-in role IDs
var blobDataContributor    = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') // Storage Blob Data Contributor
var queueDataContributor   = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88') // Storage Queue Data Contributor
var keyVaultSecretsUser    = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppPrincipalId, blobDataContributor)
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource queueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionAppPrincipalId, queueDataContributor)
  properties: {
    roleDefinitionId: queueDataContributor
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource kvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, functionAppPrincipalId, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: functionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
