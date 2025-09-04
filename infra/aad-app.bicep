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
          // User.Read (delegated)
          id: 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
          type: 'Scope'
        }
        {
          // User.Read.All (application) - for admin panel to look up users
          id: 'df021288-bdef-4463-88db-98f22de89214'
          type: 'Role'
        }
        {
          // AppRoleAssignment.ReadWrite.All (application) - for admin panel to manage role assignments
          id: '06b708a9-e830-4db3-a914-8e69da51d44f'
          type: 'Role'
        }
      ]
    }
  ]
  appRoles: [
    {
      id: guid('DDNSAdmin', appUniqueName, 'role')
      displayName: 'DDNS Administrator'
      value: 'DDNSAdmin'
      description: 'Can manage all hostnames, API keys, and user permissions'
      allowedMemberTypes: ['User']
      isEnabled: true
    }
    {
      id: guid('HostnameOwner', appUniqueName, 'role')
      displayName: 'Hostname Owner'
      value: 'HostnameOwner'
      description: 'Can manage their own claimed hostnames and API keys'
      allowedMemberTypes: ['User']
      isEnabled: true
    }
  ]
  optionalClaims: {
    idToken: [
      {
        name: 'wids'
        source: null
        essential: false
      }
      {
        name: 'groups'
        source: null
        essential: false
      }
    ]
    accessToken: [
      {
        name: 'wids'
        source: null
        essential: false
      }
      {
        name: 'groups'
        source: null
        essential: false
      }
    ]
  }
  groupMembershipClaims: 'All'
}

// Ensure a service principal exists for the app
resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: ddnsApp.appId
}

output appId string = ddnsApp.appId
output appObjectId string = ddnsApp.id
output servicePrincipalId string = servicePrincipal.id