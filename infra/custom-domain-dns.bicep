targetScope = 'subscription'

@description('Function App name to get verification ID from')
param functionAppName string

@description('Function App resource group name')
param functionAppResourceGroup string

@description('Function App subscription ID')
param functionAppSubscriptionId string

@description('Custom domain name (e.g., ddns-sandbox.title.dev)')
param customDomainName string

@description('DNS zone name (e.g., title.dev)')
param dnsZoneName string

@description('DNS zone resource group name')
param dnsZoneResourceGroup string

@description('DNS zone subscription ID')
param dnsZoneSubscriptionId string

// Reference existing Function App to get verification ID
resource functionApp 'Microsoft.Web/sites@2023-12-01' existing = {
  name: functionAppName
  scope: resourceGroup(functionAppSubscriptionId, functionAppResourceGroup)
}

// Extract subdomain from custom domain (e.g., ddns-sandbox from ddns-sandbox.title.dev)
var subdomain = split(customDomainName, '.')[0]

// Deploy DNS records to the DNS zone subscription
module dnsRecords 'custom-domain-dns-records.bicep' = {
  name: 'custom-domain-dns-records'
  scope: resourceGroup(dnsZoneSubscriptionId, dnsZoneResourceGroup)
  params: {
    dnsZoneName: dnsZoneName
    subdomain: subdomain
    verificationId: functionApp.properties.customDomainVerificationId
    targetHostname: '${functionAppName}.azurewebsites.net'
  }
}

output verificationId string = functionApp.properties.customDomainVerificationId
output asuidRecordName string = 'asuid.${subdomain}'
output cnameRecordName string = subdomain
output targetHostname string = '${functionAppName}.azurewebsites.net'