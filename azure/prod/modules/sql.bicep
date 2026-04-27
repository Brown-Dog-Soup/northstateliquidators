@description('Region.')
param location string

@description('SQL logical server name. Globally unique, lowercase alphanumeric + dashes.')
param sqlServerName string

@description('SQL database name.')
param sqlDatabaseName string

@description('Entra tenant ID.')
param tenantId string

@description('Object ID (oid) of the Entra principal that becomes SQL Server Entra admin. From: az ad user show --id jeffrey.blanchard@tenantiqpro.com --query id -o tsv')
param sqlAdminEntraObjectId string

@description('Display name (UPN) of the Entra admin shown in the portal.')
param sqlAdminEntraLogin string

@description('Tags.')
param tags object = {}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminEntraLogin
      sid: sqlAdminEntraObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
  }
}

// Allow Azure services and resources within Azure to access this server
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = sqlServer.name
output fqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = sqlDatabase.name
// Use Active Directory Default authentication; Function App's managed identity handles the auth at runtime.
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabase.name};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;'
