@description('Name of the Log Analytics workspace')
param logAnalyticsWorkspaceName string

// Note: logAnalyticsWorkspaceId was removed as it's not needed - we use the existing resource by name

@description('Enable Sentinel analytics rules for DDNS threat detection')
param enableAnalyticsRules bool = false // Set to false by default, enable after Application Insights is sending data

@description('Enable specific data connectors (Azure Activity, Security Events, etc.)')
param enableDataConnectors bool = true

// Reference the existing Log Analytics workspace (Sentinel is already onboarded via main.bicep)
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Sample Analytics Rule for DDNS Failed Authentication
// Using proper workspace-aware KQL syntax
resource ddnsFailedAuthRule 'Microsoft.SecurityInsights/alertRules@2024-03-01' = if (enableAnalyticsRules) {
  scope: logAnalyticsWorkspace
  name: 'ddns-failed-authentication'
  kind: 'Scheduled'
  properties: {
    displayName: 'DDNS Failed Authentication Monitoring'
    description: 'Detects high volume of failed authentication attempts to DDNS service'
    severity: 'Medium'
    enabled: true
    query: '''
// Query Application Insights traces for DDNS authentication failures
let lookback = 1h;
let threshold = 15;
traces
| where timestamp > ago(lookback)
| where message contains "ddns"
| extend 
    ddns_result_code = tostring(customDimensions["ddns.result_code"]),
    ddns_source_ip = tostring(customDimensions["ddns.source_ip"]),
    ddns_hostname = tostring(customDimensions["ddns.hostname"]),
    ddns_is_success = tobool(customDimensions["ddns.is_success"])
| where ddns_result_code == "badauth" and ddns_is_success == false
| summarize 
    FailedAttempts = count(),
    DistinctHostnames = dcount(ddns_hostname),
    FirstAttempt = min(timestamp),
    LastAttempt = max(timestamp)
    by SourceIP = ddns_source_ip, bin(timestamp, 30m)
| where FailedAttempts >= threshold
| project-rename TimeGenerated = timestamp
| extend 
    AlertDescription = strcat("Multiple failed DDNS authentication attempts detected from IP: ", SourceIP),
    AlertSeverity = case(
        FailedAttempts >= 50, "High",
        FailedAttempts >= 25, "Medium",
        "Low"
    )
'''
    queryFrequency: 'PT30M'
    queryPeriod: 'PT1H'
    triggerOperator: 'GreaterThan'
    triggerThreshold: 0
    suppressionDuration: 'PT1H'
    suppressionEnabled: false
    tactics: [
      'CredentialAccess'
    ]
    techniques: [
      'T1110'
    ]
    alertRuleTemplateName: null
    incidentConfiguration: {
      createIncident: true
      groupingConfiguration: {
        enabled: true
        reopenClosedIncident: false
        lookbackDuration: 'PT1H'
        matchingMethod: 'Selected'
        groupByEntities: [
          'IP'
        ]
        groupByAlertDetails: []
        groupByCustomDetails: []
      }
    }
    eventGroupingSettings: {
      aggregationKind: 'AlertPerResult'
    }
    customDetails: {
      SourceIP: 'SourceIP'
      FailedAttempts: 'FailedAttempts'
      DistinctHostnames: 'DistinctHostnames'
    }
    entityMappings: [
      {
        entityType: 'IP'
        fieldMappings: [
          {
            identifier: 'Address'
            columnName: 'SourceIP'
          }
        ]
      }
    ]
  }
}

// Outputs
output dataConnectorsDeployed bool = enableDataConnectors
output analyticsRulesDeployed bool = enableAnalyticsRules