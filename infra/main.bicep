targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the the environment which is used to generate a short unique hash used in all resources.')
param environmentName string

@minLength(1)
@description('Primary location for all resources & Flex Consumption Function App')
@allowed([
  'australiaeast'
  'australiasoutheast'
  'brazilsouth'
  'canadacentral'
  'centralindia'
  'centralus'
  'eastasia'
  'eastus'
  'eastus2'
  'eastus2euap'
  'francecentral'
  'germanywestcentral'
  'italynorth'
  'japaneast'
  'koreacentral'
  'northcentralus'
  'northeurope'
  'norwayeast'
  'southafricanorth'
  'southcentralus'
  'southeastasia'
  'southindia'
  'spaincentral'
  'swedencentral'
  'uaenorth'
  'uksouth'
  'ukwest'
  'westcentralus'
  'westeurope'
  'westus'
  'westus2'
  'westus3'
])
@metadata({
  azd: {
    type: 'location'
  }
})
param location string
param vnetEnabled bool
param apiServiceName string = ''
param apiUserAssignedIdentityName string = ''
param applicationInsightsName string = ''
param appServicePlanName string = ''
param logAnalyticsName string = ''
param resourceGroupName string = ''
param storageAccountName string = ''
param vNetName string = ''
@description('Id of the user identity to be used for testing and debugging. This is not required in production. Leave empty if not needed.')
param principalId string = deployer().objectId

// DNS Configuration for cross-subscription access
@description('Subscription ID containing the DNS zone')
param dnsSubscriptionId string = ''

@description('Resource group containing the DNS zone')
param dnsResourceGroupName string = 'domains-dns'

@description('DNS zone name to update')
param dnsZoneName string = 'title.dev'

// Azure AD Configuration for EasyAuth
@description('Azure AD tenant ID for authentication')
param azureAdTenantId string

@description('Azure AD application (client) ID')
param azureAdClientId string

// Custom Domain Configuration
@description('Custom domain name for the Function App (e.g., ddns-sandbox.title.dev)')
param customDomainName string = ''

// Microsoft Sentinel Configuration (SIEM and SOAR platform for advanced threat detection)
@description('Enable Microsoft Sentinel on the Log Analytics workspace (Note: ~$2.50/GB ingested, provides security analytics and threat intelligence)')
param enableSentinel bool = false // Disabled by default - adds significant cost, enable only if security monitoring is required

@description('Enable Sentinel analytics rules for DDNS threat detection')
param enableSentinelAnalyticsRules bool = false

@description('Enable Sentinel data connectors (Azure Activity, Security Events, etc.)')
param enableSentinelDataConnectors bool = true

// Note: UEBA is configured automatically when Sentinel is onboarded

@description('Retention period for Sentinel data in days (90-730)')
@minValue(90)
@maxValue(730)
param sentinelDataRetentionDays int = 90

// Cost Management Parameters
@description('Monthly budget limit for this environment in USD')
@minValue(10)
@maxValue(10000)
param monthlyBudgetLimit int = 100

@description('Email addresses to notify for budget alerts - comma separated')
param budgetAlertEmails string = ''

@description('Enable Azure spending budget with alerts')
param enableBudget bool = true

// Security Configuration Parameters
@description('Environment type - controls security settings')
@allowed(['dev', 'staging', 'production'])
param environmentType string = 'dev'

// Legacy DDNS basic auth removed - using API key system via /api/manage/{hostname}

@description('Semicolon-separated list of email domains for bootstrap admin access (e.g., "@domain1.com;@domain2.com")')
param bootstrapAdminDomains string = ''

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName }
var functionAppName = !empty(apiServiceName) ? apiServiceName : '${abbrs.webSitesFunctions}api-${resourceToken}'
var deploymentStorageContainerName = 'app-package-${take(functionAppName, 32)}-${take(toLower(uniqueString(functionAppName, resourceToken)), 7)}'

// Organize resources in a resource group
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

// Security Configuration Module - MUST come first for credential generation
module securityConfig './security-config.bicep' = {
  name: 'securityConfig'
  scope: rg
  params: {
    environmentType: environmentType
    // Legacy auth removed - no username/password needed
  }
}

// User assigned managed identity to be used by the function app to reach storage and other dependencies
// Assign specific roles to this identity in the RBAC module
module apiUserAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.1' = {
  name: 'apiUserAssignedIdentity'
  scope: rg
  params: {
    location: location
    tags: tags
    name: !empty(apiUserAssignedIdentityName) ? apiUserAssignedIdentityName : '${abbrs.managedIdentityUserAssignedIdentities}api-${resourceToken}'
  }
}

// Create an App Service Plan to group applications under the same payment plan and SKU
module appServicePlan 'br/public:avm/res/web/serverfarm:0.1.1' = {
  name: 'appserviceplan'
  scope: rg
  params: {
    name: !empty(appServicePlanName) ? appServicePlanName : '${abbrs.webServerFarms}${resourceToken}'
    sku: {
      name: 'FC1'
      tier: 'FlexConsumption'
    }
    reserved: true
    location: location
    tags: tags
  }
}

module api './app/api.bicep' = {
  name: 'api'
  scope: rg
  // Ensure DNS RBAC is configured before function starts (if DNS parameters provided)
  dependsOn: !empty(dnsSubscriptionId) && !empty(dnsZoneName) ? [dnsRbac] : []
  params: {
    name: functionAppName
    location: location
    tags: tags
    applicationInsightsName: monitoring.outputs.name
    appServicePlanId: appServicePlan.outputs.resourceId
    runtimeName: 'dotnet-isolated'
    runtimeVersion: '8.0'
    serviceName: 'ddns'
    storageAccountName: storage.outputs.name
    enableBlob: storageEndpointConfig.enableBlob
    enableQueue: storageEndpointConfig.enableQueue
    enableTable: storageEndpointConfig.enableTable
    deploymentStorageContainerName: deploymentStorageContainerName
    identityId: apiUserAssignedIdentity.outputs.resourceId
    identityClientId: apiUserAssignedIdentity.outputs.clientId
    appSettings: {
      AZURE_CLIENT_ID: apiUserAssignedIdentity.outputs.clientId
      DNS_SUBSCRIPTION_ID: dnsSubscriptionId
      DNS_RESOURCE_GROUP: dnsResourceGroupName
      DNS_ZONE_NAME: dnsZoneName
      DDNS_SUBDOMAIN: 'ddns-sandbox'  // SECURITY: This is the FULL subdomain that can be updated
      // Legacy auth - only for local dev/testing, production should use API keys via /api/manage/{hostname}
      // DDNS_USERNAME: '@Microsoft.KeyVault(VaultName=${keyVault.outputs.name};SecretName=ddns-username)'
      // DDNS_PASSWORD: '@Microsoft.KeyVault(VaultName=${keyVault.outputs.name};SecretName=ddns-password)'
      KEY_VAULT_URI: keyVault.outputs.uri
      AZURE_AD_TENANT_ID: azureAdTenantId
      AZURE_AD_CLIENT_ID: azureAdClientId
      AZURE_STORAGE_ACCOUNT_NAME: storage.outputs.name
      LOG_ANALYTICS_WORKSPACE_ID: logAnalytics.outputs.logAnalyticsWorkspaceId
      BOOTSTRAP_ADMIN_DOMAINS: bootstrapAdminDomains  // Configurable admin domains for bootstrap access
    }
    virtualNetworkSubnetId: vnetEnabled ? serviceVirtualNetwork.outputs.appSubnetID : ''
    customDomainName: customDomainName
  }
}

// Key Vault for storing secrets and API keys
module keyVault 'br/public:avm/res/key-vault/vault:0.9.0' = {
  name: 'keyvault'
  scope: rg
  params: {
    name: take('kv${resourceToken}', 24) // Key Vault names limited to 24 chars
    location: location
    tags: tags
    enableRbacAuthorization: securityConfig.outputs.keyVaultConfig.enableRbacAuthorization
    enablePurgeProtection: securityConfig.outputs.keyVaultConfig.enablePurgeProtection
    softDeleteRetentionInDays: securityConfig.outputs.keyVaultConfig.softDeleteRetentionInDays
    sku: 'standard' // COST: Standard SKU is more cost-effective than Premium
    secrets: []  // No secrets needed - using API key system via /api/manage/{hostname}
    roleAssignments: [
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' // Key Vault Secrets Officer (read/write)
        principalType: 'ServicePrincipal'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: '00482a5a-887f-4fb3-b363-3b7fe8e74483' // Key Vault Administrator
        principalType: 'User'
      }
    ]
  }
}

// Backing storage for Azure functions backend API
module storage 'br/public:avm/res/storage/storage-account:0.8.3' = {
  name: 'storage'
  scope: rg
  params: {
    name: !empty(storageAccountName) ? storageAccountName : '${abbrs.storageStorageAccounts}${resourceToken}'
    // COST OPTIMIZATION: Storage account settings
    skuName: 'Standard_LRS' // COST: LRS is cheapest, suitable for non-critical data
    kind: 'StorageV2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Disable local authentication methods as per policy
    dnsEndpointType: 'Standard'
    publicNetworkAccess: vnetEnabled ? 'Disabled' : 'Enabled'
    networkAcls: vnetEnabled ? {
      defaultAction: 'Deny'
      bypass: 'None'
    } : {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    blobServices: {
      containers: [{name: deploymentStorageContainerName}]
    }
    minimumTlsVersion: 'TLS1_2'  // Enforcing TLS 1.2 for better security
    location: location
    tags: tags
  }
}

// Define the configuration object locally to pass to the modules
var storageEndpointConfig = {
  enableBlob: true  // Required for AzureWebJobsStorage, .zip deployment, Event Hubs trigger and Timer trigger checkpointing
  enableQueue: false  // Required for Durable Functions and MCP trigger
  enableTable: true  // Required for API key and hostname ownership storage
  enableFiles: false   // Not required, used in legacy scenarios
  allowUserIdentityPrincipal: true   // Allow interactive user identity to access for testing and debugging
}

// Consolidated Role Assignments
module rbac 'app/rbac.bicep' = {
  name: 'rbacAssignments'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    appInsightsName: monitoring.outputs.name
    managedIdentityPrincipalId: apiUserAssignedIdentity.outputs.principalId
    userIdentityPrincipalId: principalId
    enableBlob: storageEndpointConfig.enableBlob
    enableQueue: storageEndpointConfig.enableQueue
    enableTable: storageEndpointConfig.enableTable
    allowUserIdentityPrincipal: storageEndpointConfig.allowUserIdentityPrincipal
  }
}

// Virtual Network & private endpoint to blob storage
module serviceVirtualNetwork 'app/vnet.bicep' =  if (vnetEnabled) {
  name: 'serviceVirtualNetwork'
  scope: rg
  params: {
    location: location
    tags: tags
    vNetName: !empty(vNetName) ? vNetName : '${abbrs.networkVirtualNetworks}${resourceToken}'
  }
}

module storagePrivateEndpoint 'app/storage-PrivateEndpoint.bicep' = if (vnetEnabled) {
  name: 'servicePrivateEndpoint'
  scope: rg
  params: {
    location: location
    tags: tags
    virtualNetworkName: !empty(vNetName) ? vNetName : '${abbrs.networkVirtualNetworks}${resourceToken}'
    subnetName: vnetEnabled ? serviceVirtualNetwork.outputs.peSubnetName : '' // Keep conditional check for safety, though module won't run if !vnetEnabled
    resourceName: storage.outputs.name
    enableBlob: storageEndpointConfig.enableBlob
    enableQueue: storageEndpointConfig.enableQueue
    enableTable: storageEndpointConfig.enableTable
  }
}

// Monitor application with Azure Monitor - Log Analytics and Application Insights
module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.11.1' = {
  name: '${uniqueString(deployment().name, location)}-loganalytics'
  scope: rg
  params: {
    name: !empty(logAnalyticsName) ? logAnalyticsName : '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    location: location
    tags: tags
    dataRetention: sentinelDataRetentionDays // COST: 90 days minimum for Sentinel, adjust for cost
    // Enable Microsoft Sentinel on the Log Analytics workspace
    onboardWorkspaceToSentinel: enableSentinel
    // Add SecurityInsights solution for Sentinel
    gallerySolutions: enableSentinel ? [
      {
        name: 'SecurityInsights(${!empty(logAnalyticsName) ? logAnalyticsName : '${abbrs.operationalInsightsWorkspaces}${resourceToken}'})'
        plan: {
          product: 'OMSGallery/SecurityInsights'
          publisher: 'Microsoft'
        }
      }
    ] : []
  }
}
 
module monitoring 'br/public:avm/res/insights/component:0.6.0' = {
  name: '${uniqueString(deployment().name, location)}-appinsights'
  scope: rg
  params: {
    name: !empty(applicationInsightsName) ? applicationInsightsName : '${abbrs.insightsComponents}${resourceToken}'
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
    disableLocalAuth: true
  }
}

// DNS RBAC Configuration for cross-subscription access
// Assigns both Reader (on resource group) and DNS Zone Contributor (on DNS zone) roles
// Both roles are required: Reader to access the resource group, DNS Zone Contributor to modify DNS records
// Only deploy if DNS configuration parameters are provided
module dnsRbac './app/dns-rbac.bicep' = if (!empty(dnsSubscriptionId) && !empty(dnsZoneName)) {
  name: '${uniqueString(deployment().name, location)}-dnsrbac'
  scope: subscription(dnsSubscriptionId) // Deploy to the DNS subscription
  params: {
    managedIdentityPrincipalId: apiUserAssignedIdentity.outputs.principalId
    dnsZoneName: dnsZoneName
    dnsZoneResourceGroupName: dnsResourceGroupName
    dnsZoneSubscriptionId: dnsSubscriptionId
  }
}

// Microsoft Sentinel Data Connectors and Analytics Rules
// Deploy additional Sentinel components after workspace is onboarded
module sentinelComponents './sentinel.bicep' = if (enableSentinel) {
  name: '${uniqueString(deployment().name, location)}-sentinel'
  scope: rg
  params: {
    logAnalyticsWorkspaceName: logAnalytics.outputs.name
    enableAnalyticsRules: enableSentinelAnalyticsRules
    enableDataConnectors: enableSentinelDataConnectors
  }
  dependsOn: [
    monitoring
    api
  ]
}

// Parse comma-separated emails
var budgetEmails = empty(budgetAlertEmails) ? [] : split(budgetAlertEmails, ',')

// Azure Cost Management Budget - RESOURCE GROUP SCOPED
// This deploys directly into the resource group so it appears in RG > Budgets view
module budgetAlert './budget.bicep' = if (enableBudget && length(budgetEmails) > 0) {
  name: '${uniqueString(deployment().name, location)}-budget'
  scope: rg // Deploy TO the resource group, not at subscription level
  params: {
    budgetName: 'budget-${rg.name}-monthly'
    budgetAmount: monthlyBudgetLimit
    contactEmails: budgetEmails
    // No resourceGroupName parameter needed - it's inherently scoped
  }
}

// App outputs
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output SERVICE_API_NAME string = api.outputs.SERVICE_API_NAME
output AZURE_FUNCTION_NAME string = api.outputs.SERVICE_API_NAME
output KEY_VAULT_NAME string = keyVault.outputs.name
output KEY_VAULT_URI string = keyVault.outputs.uri
output STORAGE_ACCOUNT_NAME string = storage.outputs.name
output LOG_ANALYTICS_WORKSPACE_NAME string = logAnalytics.outputs.name
output LOG_ANALYTICS_WORKSPACE_ID string = logAnalytics.outputs.resourceId

// Sentinel outputs
output SENTINEL_ENABLED bool = enableSentinel
output SENTINEL_WORKSPACE_NAME string = logAnalytics.outputs.name
output SENTINEL_WORKSPACE_ID string = logAnalytics.outputs.resourceId
output SENTINEL_ANALYTICS_RULES_ENABLED bool = enableSentinelAnalyticsRules
output SENTINEL_DATA_CONNECTORS_ENABLED bool = enableSentinelDataConnectors
