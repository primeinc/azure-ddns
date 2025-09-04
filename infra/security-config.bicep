targetScope = 'resourceGroup'

@description('Environment type - controls security settings')
@allowed(['dev', 'staging', 'production'])
param environmentType string = 'dev'

@description('Generate secure random password for default accounts')
param generateSecurePassword bool = true

@description('Optional custom username (generated if not provided)')
@secure()
param customUsername string = ''

@description('Optional custom password (generated if not provided)')
@secure()
param customPassword string = ''

// Security configuration based on environment
var securityConfig = {
  dev: {
    enablePurgeProtection: false
    softDeleteRetentionDays: 7
    requireMfa: false
    allowedDomains: ['localhost', '*.azurewebsites.net']
  }
  staging: {
    enablePurgeProtection: true
    softDeleteRetentionDays: 30
    requireMfa: false
    allowedDomains: ['*.azurewebsites.net', '*.staging.yourdomain.com']
  }
  production: {
    enablePurgeProtection: true
    softDeleteRetentionDays: 90
    requireMfa: true
    allowedDomains: ['*.yourdomain.com']
  }
}

// Generate secure credentials if not provided
var finalUsername = !empty(customUsername) ? customUsername : 'admin-${uniqueString(resourceGroup().id)}'
var finalPassword = !empty(customPassword) ? customPassword : generateSecurePassword ? '${uniqueString(resourceGroup().id, deployment().name)}${toUpper(uniqueString(subscription().id))}!@#' : 'MUST-CHANGE-THIS-PASSWORD!'

// Output security configuration for use in other modules
output securitySettings object = securityConfig[environmentType]
output appUsername string = finalUsername
@secure()
output appPassword string = finalPassword
output environment string = environmentType

// Key Vault configuration with environment-appropriate settings
output keyVaultConfig object = {
  enablePurgeProtection: securityConfig[environmentType].enablePurgeProtection
  softDeleteRetentionInDays: securityConfig[environmentType].softDeleteRetentionDays
  enableRbacAuthorization: true
  publicNetworkAccess: environmentType == 'production' ? 'Disabled' : 'Enabled'
}

// Domain configuration
output allowedDomains array = securityConfig[environmentType].allowedDomains