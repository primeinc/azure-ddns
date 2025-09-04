@description('Principal ID of the managed identity that needs DNS Zone Contributor access')
param managedIdentityPrincipalId string

@description('DNS Zone name')
param dnsZoneName string

// Reference to the DNS Zone in current resource group
resource dnsZone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: dnsZoneName
}

// Role assignment for Reader on resource group (required for ARM to read resource group)
// Using the canonical pattern: guid(scope, principal, role)
resource readerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, managedIdentityPrincipalId, 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7') // Reader
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Reader access for DDNS function app managed identity'
  }
}

// Role assignment for DNS Zone Contributor
// Using the canonical pattern: guid(scope, principal, role)
resource dnsZoneRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dnsZone.id, managedIdentityPrincipalId, 'befefa01-2a29-4197-83a8-272ff33ce314')
  scope: dnsZone
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'befefa01-2a29-4197-83a8-272ff33ce314') // DNS Zone Contributor
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
    description: 'DNS Zone Contributor access for DDNS function app managed identity'
  }
}

output roleAssignmentId string = dnsZoneRoleAssignment.id
output roleAssignmentName string = dnsZoneRoleAssignment.name
output readerRoleAssignmentId string = readerRoleAssignment.id