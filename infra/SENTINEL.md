# Microsoft Sentinel Configuration for DDNS Security Monitoring

This directory contains comprehensive Bicep templates for enabling Microsoft Sentinel on the Azure DDNS project, providing advanced security monitoring and threat detection capabilities.

## Architecture Overview

```
infra/
├── sentinel.bicep                           # Main Sentinel workspace configuration
└── modules/
    ├── sentinel-analytics-rules.bicep       # DDNS-specific security rules
    └── sentinel-data-connectors.bicep       # Data connectors and UEBA settings
```

## Deployment

### Quick Start
The Sentinel configuration is automatically deployed as part of the main infrastructure when enabled:

```bash
# Deploy with Sentinel enabled (default)
azd up

# Deploy without Sentinel
azd up --parameters enableSentinel=false
```

### Individual Deployment
You can also deploy just the Sentinel components:

```bash
# Deploy to existing resource group with Log Analytics workspace
az deployment group create \
  --resource-group rg-ddns-dev \
  --template-file sentinel.bicep \
  --parameters logAnalyticsWorkspaceName=log-ddns-dev \
               environmentName=dev \
               location=eastus
```

## Configuration Parameters

| Parameter | Description | Default | Options |
|-----------|-------------|---------|---------|
| `enableSentinel` | Enable Microsoft Sentinel | `true` | `true/false` |
| `enableSentinelAnalyticsRules` | Deploy DDNS threat detection rules | `true` | `true/false` |
| `enableSentinelDataConnectors` | Enable data connectors | `true` | `true/false` |
| `enableSentinelUEBA` | Enable User & Entity Behavior Analytics | `true` | `true/false` |
| `sentinelDataRetentionDays` | Data retention period | `90` | `90-730` |

## Security Analytics Rules

The deployment includes 6 comprehensive analytics rules targeting DDNS-specific threats:

### 1. Credential Stuffing Detection (`DDNS_Credential_Stuffing_Detection`)
- **Severity**: Medium
- **Frequency**: Every 15 minutes  
- **Detects**: Multiple failed authentication attempts from single IP
- **Threshold**: 10+ failed attempts in 15 minutes
- **MITRE ATT&CK**: T1110.003, T1110.004

### 2. DNS Hijacking Detection (`DDNS_DNS_Hijacking_Detection`)
- **Severity**: High
- **Frequency**: Every 1 hour
- **Detects**: Suspicious IP address changes for hostnames
- **Threshold**: 5+ IP changes within 1 hour for same hostname
- **MITRE ATT&CK**: T1584.004, T1565.002

### 3. Service Abuse Detection (`DDNS_Abuse_Pattern_Detection`)
- **Severity**: Medium
- **Frequency**: Every 1 hour
- **Detects**: Rapid-fire updates and service abuse patterns
- **Threshold**: 50+ updates per hour or excessive failures
- **MITRE ATT&CK**: T1498.001, T1499.004

### 4. Failed Authentication Monitoring (`DDNS_Failed_Authentication_Monitoring`)
- **Severity**: Medium
- **Frequency**: Every 30 minutes
- **Detects**: High volume authentication failures
- **Threshold**: 50+ global failures or 15+ per hostname
- **MITRE ATT&CK**: T1110, T1078

### 5. Geographic Anomaly Detection (`DDNS_Geographic_Anomaly_Detection`)
- **Severity**: Medium
- **Frequency**: Every 4 hours
- **Detects**: Suspicious geographic patterns and impossible travel
- **Threshold**: >1000km distance changes or >800km/h speed
- **MITRE ATT&CK**: T1078, T1102

### 6. Suspicious User Agent Detection (`DDNS_Suspicious_UserAgent_Detection`)
- **Severity**: Low
- **Frequency**: Every 1 hour
- **Detects**: Automated tools and security scanners
- **Patterns**: sqlmap, nmap, burp, python-requests, etc.
- **MITRE ATT&CK**: T1595.002, T1590

## Custom Dimensions Usage

The analytics rules leverage structured logging from the TelemetryHelper service:

```csharp
// Core dimensions used in queries
customDimensions["ddns.event_type"]     // "ddns_update", "authentication", etc.
customDimensions["ddns.hostname"]       // Target hostname
customDimensions["ddns.source_ip"]      // Client IP address  
customDimensions["ddns.result_code"]    // "good", "badauth", "nohost", etc.
customDimensions["ddns.auth_method"]    // "ApiKey", "AzureAD", "BasicAuth"
customDimensions["ddns.user_agent"]     // Client user agent string
customDimensions["ddns.is_success"]     // Boolean success flag
customDimensions["ddns.target_ip"]      // DNS record IP value
```

## Data Connectors Enabled

- Azure Activity Logs
- Azure Active Directory Identity Protection
- Azure Security Center/Microsoft Defender
- Microsoft Cloud App Security
- Microsoft Threat Intelligence
- Office 365 (Exchange, SharePoint, Teams)
- Threat Intelligence (TAXII feeds)
- Azure Firewall (if present)
- DNS Analytics

## Watchlists

Two watchlists are automatically created:

### TrustedDdnsIPs
- Purpose: Allowlist for known good IP addresses
- Format: IP address, description, trusted flag
- Usage: Exclude trusted IPs from alerts

### SuspiciousLomains
- Purpose: Known malicious domains and user agents
- Format: Indicator, type, description, severity
- Usage: Enhance threat detection accuracy

## UEBA (User and Entity Behavior Analytics)

When enabled, UEBA analyzes:
- User authentication patterns
- IP address behavior
- Hostname access patterns  
- Geographic access anomalies
- Time-based usage patterns

## Monitoring & Alerting

### Built-in Dashboards
Navigate to Microsoft Sentinel > Workbooks for:
- DDNS Security Overview
- Authentication Analytics
- Geographic Access Patterns
- Threat Intelligence Integration

### Custom Metrics
Available in Application Insights and Sentinel:
- Authentication success/failure rates
- IP geolocation changes
- Update frequency patterns
- User agent analysis

## Cost Considerations

Sentinel pricing is based on data ingestion volume:

| Component | Estimated Daily Volume | Monthly Cost (approx.) |
|-----------|----------------------|------------------------|
| Application Traces | 50-500 MB | $15-150 |
| Security Events | 10-100 MB | $3-30 |
| Analytics Rules | Minimal | $1-5 |
| **Total Estimate** | **60-600 MB** | **$19-185** |

> **Note**: Costs vary by region and actual usage. Consider 90-day retention vs. longer periods based on compliance needs.

## Security Benefits

1. **Real-time Threat Detection**: Immediate alerts for credential stuffing, DNS hijacking, and service abuse
2. **Geographic Monitoring**: Detect impossible travel and suspicious location changes  
3. **Behavioral Analytics**: ML-based detection of unusual patterns
4. **Threat Intelligence**: Integration with Microsoft's global threat intelligence feeds
5. **Incident Response**: Automated case creation and investigation workflows
6. **Compliance**: Centralized logging and retention for audit requirements

## Troubleshooting

### Common Issues

**Analytics rules not triggering:**
- Verify Log Analytics workspace contains Application Insights data
- Check custom dimensions are being populated by TelemetryHelper
- Ensure `ddns.*` properties are present in log entries

**High false positive rates:**
- Adjust thresholds in analytics rules
- Update TrustedDdnsIPs watchlist with known good sources
- Tune geographic detection sensitivity

**Data connectors failing:**
- Verify required permissions for service principals
- Check subscription has required licenses (P1/P2 for some connectors)
- Confirm connectors are supported in deployment region

### Validation Queries

Test your deployment with these KQL queries:

```kusto
// Verify DDNS events are flowing
AppTraces
| where customDimensions has "ddns.event_type"
| summarize count() by tostring(customDimensions["ddns.event_type"])
| order by count_ desc

// Check authentication patterns
AppTraces 
| where customDimensions has "ddns.auth_method"
| summarize count() by 
    tostring(customDimensions["ddns.auth_method"]),
    tostring(customDimensions["ddns.is_success"])

// Geographic distribution
AppTraces
| where customDimensions has "ddns.source_ip"
| extend SourceIP = tostring(customDimensions["ddns.source_ip"])
| extend GeoInfo = geo_info_from_ip_address(SourceIP)
| summarize count() by tostring(GeoInfo.country)
| order by count_ desc
```

## Support

For issues with:
- **Bicep templates**: Check compilation with `az bicep build`
- **Analytics rules**: Review KQL syntax in Log Analytics
- **Data connectors**: Verify permissions and licensing
- **Performance**: Monitor ingestion rates and costs in Cost Management