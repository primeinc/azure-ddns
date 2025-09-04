param siteName string
param customDomainName string
param certThumbprint string

resource site 'Microsoft.Web/sites@2023-12-01' existing = {
  name: siteName
}

resource hostNameSsl 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = {
  parent: site
  name: customDomainName
  properties: {
    customHostNameDnsRecordType: 'CName'
    hostNameType: 'Verified'
    sslState: 'SniEnabled'
    thumbprint: certThumbprint
  }
}