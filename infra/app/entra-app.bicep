extension graphV1

targetScope = 'tenant'

@description('The unique name of the Azure AD app (e.g., appId or displayName if unique)')
param appUniqueName string

@description('Full list of redirect URIs including existing and new')
param redirectUris array

@description('Homepage URL for the application')
param homepageUrl string = ''

@description('Logout URL for the application')
param logoutUrl string = ''

resource app 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: appUniqueName
  displayName: 'Azure DDNS Service'
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: redirectUris
    homePageUrl: homepageUrl != '' ? homepageUrl : null
    logoutUrl: logoutUrl != '' ? logoutUrl : null
  }
}

// Output updated app details for verification
output appObjectId string = app.id
output updatedRedirectUris array = app.web.redirectUris