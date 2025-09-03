targetScope = 'subscription'

@description('Principal ID of the managed identity that needs DNS Zone Contributor access')
param managedIdentityPrincipalId string

@description('DNS Zone name')
param dnsZoneName string

@description('DNS Zone resource group name')
param dnsZoneResourceGroupName string

@description('DNS Zone subscription ID')
param dnsZoneSubscriptionId string

// Deploy at resource group level for the DNS zone
module dnsRoleAssignment 'dns-role-assignment.bicep' = {
  name: 'dnsRoleAssignment'
  scope: resourceGroup(dnsZoneSubscriptionId, dnsZoneResourceGroupName)
  params: {
    managedIdentityPrincipalId: managedIdentityPrincipalId
    dnsZoneName: dnsZoneName
  }
}

output roleAssignmentId string = dnsRoleAssignment.outputs.roleAssignmentId
output roleAssignmentName string = dnsRoleAssignment.outputs.roleAssignmentName