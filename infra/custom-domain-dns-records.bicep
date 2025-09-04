targetScope = 'resourceGroup'

@description('DNS zone name')
param dnsZoneName string

@description('Subdomain name (e.g., ddns-sandbox)')
param subdomain string

@description('Domain verification ID from Function App')
param verificationId string

@description('Target hostname (e.g., func-api-cq3maraez745s.azurewebsites.net)')
param targetHostname string

// Reference existing DNS zone
resource dnsZone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: dnsZoneName
}

// Create asuid.{subdomain} TXT record for domain verification
resource asuidTxtRecord 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  name: 'asuid.${subdomain}'
  parent: dnsZone
  properties: {
    TTL: 300
    TXTRecords: [
      {
        value: [verificationId]
      }
    ]
    metadata: {
      purpose: 'Azure App Service custom domain verification'
      createdFor: '${subdomain}.${dnsZoneName}'
    }
  }
}

// Create {subdomain} CNAME record pointing to Function App
resource subdomainCnameRecord 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  name: subdomain
  parent: dnsZone
  properties: {
    TTL: 300
    CNAMERecord: {
      cname: targetHostname
    }
    metadata: {
      purpose: 'Azure App Service custom domain'
      target: targetHostname
    }
  }
  dependsOn: [asuidTxtRecord] // Ensure verification record is created first
}

output asuidRecordFqdn string = asuidTxtRecord.properties.fqdn
output cnameRecordFqdn string = subdomainCnameRecord.properties.fqdn
output verificationRecordValue string = verificationId