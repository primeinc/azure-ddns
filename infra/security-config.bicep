targetScope = 'resourceGroup'

@description('Environment type - controls security settings')
@allowed(['dev', 'staging', 'production'])
param environmentType string = 'dev'

// Legacy DDNS basic auth parameters removed - using API key system

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

// Output security configuration for use in other modules
output securitySettings object = securityConfig[environmentType]
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