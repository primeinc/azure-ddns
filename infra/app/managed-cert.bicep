param location string
param customDomainName string
param appServicePlanId string

resource managedCert 'Microsoft.Web/certificates@2023-12-01' = {
  name: 'cert-${replace(customDomainName, '.', '-')}'
  location: location
  properties: {
    serverFarmId: appServicePlanId
    canonicalName: customDomainName
  }
}

output thumbprint string = managedCert.properties.thumbprint