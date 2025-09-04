param siteName string
param customDomainName string

resource site 'Microsoft.Web/sites@2023-12-01' existing = {
  name: siteName
}

resource hostName 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = {
  parent: site
  name: customDomainName
  properties: {
    customHostNameDnsRecordType: 'CName'
    hostNameType: 'Verified'
  }
}