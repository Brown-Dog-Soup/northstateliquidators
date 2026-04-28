// ============================================================================
// NSL — Marketing Website (Azure Static Web App)
//
// Subscription-scoped deployment that creates a dedicated resource group for
// the public marketing site (north-state-liquidators.com) and provisions a
// Static Web App inside it. Inventory pipeline (Functions, SQL, Storage, etc.)
// goes in a separate resource group later — this RG is just the website.
//
// Tenant: TenantIQpro.com (Entra ID).
//   Tenant ID guard is enforced at the deploy script level
//   (Deploy-NSLWeb.ps1) since Bicep itself runs against whatever subscription
//   the caller is signed into.
//
// Deploy:
//   .\Deploy-NSLWeb.ps1
// ============================================================================

targetScope = 'subscription'

@description('Azure region for the resource group + Static Web App. Free SWA is supported in a limited set of regions; eastus2 is closest to NC.')
param location string = 'eastus2'

@description('Resource group name for NSL website resources.')
param resourceGroupName string = 'rg-nsl-website'

@description('Static Web App resource name. Must be globally unique-ish; lowercase + dashes.')
param staticSiteName string = 'stapp-nsl-website'

@description('Static Web App SKU. Standard ($9/mo) unlocks managed Functions in api/ — needed for the inventory-pipeline backend (scan ingest, enrichment, Shopify push). Free is fine for marketing-only.')
@allowed(['Free', 'Standard'])
param staticSiteSku string = 'Standard'

@description('Tags applied to RG and all resources.')
param tags object = {
  project: 'north-state-liquidators'
  component: 'website'
  tenant: 'TenantIQpro.com'
  owner: 'TenantIQ Pro LLC'
  managed_by: 'Bicep'
}

// ----------------------------------------------------------------------------
// Resource group
// ----------------------------------------------------------------------------
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// ----------------------------------------------------------------------------
// Static Web App module
// ----------------------------------------------------------------------------
module website 'modules/static-web-app.bicep' = {
  scope: rg
  name: 'deploy-${staticSiteName}'
  params: {
    location: location
    name: staticSiteName
    sku: staticSiteSku
    tags: tags
  }
}

// ----------------------------------------------------------------------------
// Outputs
// ----------------------------------------------------------------------------
output resourceGroupName string = rg.name
output staticSiteName string = website.outputs.name
output defaultHostname string = website.outputs.defaultHostname
output staticSiteResourceId string = website.outputs.resourceId
output staticSitePrincipalId string = website.outputs.principalId
