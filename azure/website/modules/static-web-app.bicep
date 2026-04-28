// ============================================================================
// Static Web App module
// Deploys a single Microsoft.Web/staticSites resource WITHOUT a GitHub repo
// binding. The repo binding is managed via a GitHub Actions workflow (see
// .github/workflows/azure-static-web-apps.yml) which uses the deployment
// token retrieved post-deploy with:
//
//   az staticwebapp secrets list --name <name> --query "properties.apiKey" -o tsv
// ============================================================================

@description('Region for the Static Web App. Free SWA is currently supported in a limited region set.')
param location string

@description('Static Web App resource name.')
param name string

@description('Static Web App SKU.')
@allowed(['Free', 'Standard'])
param sku string = 'Free'

@description('Tags applied to the Static Web App.')
param tags object = {}

resource swa 'Microsoft.Web/staticSites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
  }
  // System-assigned managed identity is the principal that managed Functions
  // (api/) use when authenticating to Azure SQL, Storage, and Key Vault.
  identity: { type: 'SystemAssigned' }
  // Intentionally empty properties — no GitHub repo binding via Bicep so we
  // can deploy from a workflow file in the repo using the SWA's deployment
  // token. Azure auto-selects "manual upload" mode when no repository URL
  // is provided. Setting `provider: 'None'` along with `branch` here causes
  // ARM to reject the create with "RepositoryUrl cannot be empty".
  properties: {
    stagingEnvironmentPolicy: 'Enabled'
    allowConfigFileUpdates: true
  }
}

output name string = swa.name
output resourceId string = swa.id
output defaultHostname string = swa.properties.defaultHostname
output principalId string = swa.identity.principalId
