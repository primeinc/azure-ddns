@description('Principal ID of the managed identity that needs DNS Zone Contributor access')
param managedIdentityPrincipalId string

@description('DNS Zone name')
param dnsZoneName string

// Reference to the DNS Zone in current resource group
resource dnsZone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: dnsZoneName
}

// Role assignment for DNS Zone Contributor
resource dnsZoneRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dnsZone.id, managedIdentityPrincipalId, 'DNS Zone Contributor')
  scope: dnsZone
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'befefa01-2a29-4197-83a8-272ff33ce314') // DNS Zone Contributor
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output roleAssignmentId string = dnsZoneRoleAssignment.id
output roleAssignmentName string = dnsZoneRoleAssignment.name