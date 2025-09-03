param name string
@description('Primary location for all resources & Flex Consumption Function App')
param location string = resourceGroup().location
param tags object = {}
param applicationInsightsName string = ''
param appServicePlanId string
param appSettings object = {}
param runtimeName string 
param runtimeVersion string 
param serviceName string = 'api'
param storageAccountName string
param deploymentStorageContainerName string
param virtualNetworkSubnetId string = ''
param instanceMemoryMB int = 2048
param maximumInstanceCount int = 100
param identityId string = ''
param identityClientId string = ''
param enableBlob bool = true
param enableQueue bool = false
param enableTable bool = false
param enableFile bool = false

@allowed(['SystemAssigned', 'UserAssigned'])
param identityType string = 'UserAssigned'

var applicationInsightsIdentity = 'ClientId=${identityClientId};Authorization=AAD'
var kind = 'functionapp,linux'

// Create base application settings as array for siteConfig.appSettings
var baseAppSettingsArray = [
  {
    name: 'AzureWebJobsStorage__credential'
    value: 'managedidentity'
  }
  {
    name: 'AzureWebJobsStorage__clientId'
    value: identityClientId
  }
  {
    name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
    value: applicationInsightsIdentity
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: applicationInsights.properties.ConnectionString
  }
]

// Dynamically build storage endpoint settings based on feature flags
var blobSettings = enableBlob ? [{ name: 'AzureWebJobsStorage__blobServiceUri', value: stg.properties.primaryEndpoints.blob }] : []
var queueSettings = enableQueue ? [{ name: 'AzureWebJobsStorage__queueServiceUri', value: stg.properties.primaryEndpoints.queue }] : []
var tableSettings = enableTable ? [{ name: 'AzureWebJobsStorage__tableServiceUri', value: stg.properties.primaryEndpoints.table }] : []
var fileSettings = enableFile ? [{ name: 'AzureWebJobsStorage__fileServiceUri', value: stg.properties.primaryEndpoints.file }] : []

// Convert custom appSettings object to array and merge with base
var customAppSettingsArray = [for key in items(appSettings): { name: key.key, value: key.value }]
var allAppSettingsArray = union(customAppSettingsArray, baseAppSettingsArray, blobSettings, queueSettings, tableSettings, fileSettings)

resource stg 'Microsoft.Storage/storageAccounts@2022-09-01' existing = {
  name: storageAccountName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = if (!empty(applicationInsightsName)) {
  name: applicationInsightsName
}

// Direct declaration of Function App (replaces AVM module for full control)
resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': serviceName })
  kind: kind
  identity: {
    type: identityType
    userAssignedIdentities: identityType == 'UserAssigned' ? { '${identityId}': {} } : null
  }
  properties: {
    serverFarmId: appServicePlanId
    httpsOnly: true
    keyVaultReferenceIdentity: identityType == 'UserAssigned' ? identityId : null  // Critical: Set this for user-assigned identity
    virtualNetworkSubnetId: !empty(virtualNetworkSubnetId) ? virtualNetworkSubnetId : null
    siteConfig: {
      alwaysOn: false
      appSettings: allAppSettingsArray  // Array format for settings including KV refs
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      use32BitWorkerProcess: false
      cors: {
        allowedOrigins: ['https://portal.azure.com']
      }
      functionAppScaleLimit: maximumInstanceCount
      minimumElasticInstanceCount: 0
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${stg.properties.primaryEndpoints.blob}${deploymentStorageContainerName}'
          authentication: {
            type: identityType == 'SystemAssigned' ? 'SystemAssignedIdentity' : 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityType == 'UserAssigned' ? identityId : null
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
      }
      runtime: {
        name: runtimeName
        version: runtimeVersion
      }
    }
  }
}

output SERVICE_API_NAME string = api.name
output SERVICE_API_IDENTITY_PRINCIPAL_ID string = identityType == 'SystemAssigned' ? api.identity.?principalId ?? '' : ''
