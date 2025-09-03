targetScope = 'tenant'

// Use the GA Graph Bicep dynamic types (v1.0)
extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0'

@description('Unique name for the app registration - must be set once on existing app')
param appUniqueName string = 'ddns-management-app'

@description('Display name for the app registration')
param displayName string = 'DDNS Management App'

@description('Function App hostname')
param functionAppHostname string

// Build the redirect URIs
var redirectUris = [
  'https://${functionAppHostname}/.auth/login/aad/callback'
  'http://localhost:7071/api/manage/callback' // For local development
  'http://localhost:7071/.auth/login/aad/callback' // For local EasyAuth testing
]

var defaultRedirectUri = 'https://${functionAppHostname}/.auth/login/aad/callback'

// Create or update the app registration with redirect URIs
resource ddnsApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: appUniqueName
  displayName: displayName
  signInAudience: 'AzureADMyOrg'
  web: {
    homePageUrl: 'https://${functionAppHostname}/'
    redirectUris: redirectUris
    implicitGrantSettings: {
      enableIdTokenIssuance: true
      enableAccessTokenIssuance: false
    }
    logoutUrl: 'https://${functionAppHostname}/.auth/logout'
  }
  defaultRedirectUri: defaultRedirectUri
  requiredResourceAccess: [
    {
      // Microsoft Graph
      resourceAppId: '00000003-0000-0000-c000-000000000000'
      resourceAccess: [
        {
          // User.Read
          id: 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
          type: 'Scope'
        }
      ]
    }
  ]
}

// Ensure a service principal exists for the app
resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: ddnsApp.appId
}

output appId string = ddnsApp.appId
output appObjectId string = ddnsApp.id
output servicePrincipalId string = servicePrincipal.id