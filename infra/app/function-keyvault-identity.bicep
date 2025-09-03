@description('Name of the Function App to update')
param functionAppName string

@description('Resource ID of the user-assigned managed identity to use for Key Vault references')
param identityResourceId string

@description('Location of the Function App')
param location string = resourceGroup().location

// Reference the existing Function App and update the keyVaultReferenceIdentity property
resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  properties: {
    keyVaultReferenceIdentity: identityResourceId
  }
}

output functionAppName string = functionApp.name
output keyVaultReferenceIdentity string = functionApp.properties.keyVaultReferenceIdentity