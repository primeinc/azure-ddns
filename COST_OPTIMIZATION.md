# Azure DDNS Cost Optimization Guide

## Overview
This document outlines the cost optimization measures implemented in the Azure DDNS solution.

## Cost Controls Implemented

### 1. Azure Budget Alerts
- **Monthly Budget**: Default $50/month (configurable via `MONTHLY_BUDGET_LIMIT`)
- **Alert Thresholds**: 50%, 75%, 90%, 100%, 110% (forecasted)
- **Email Notifications**: Configure via `BUDGET_ALERT_EMAILS` environment variable
- **Deployment**: Set `ENABLE_BUDGET=true` and provide email addresses

### 2. Function App Optimization
- **Flex Consumption Plan**: Pay-per-execution model
- **Memory**: Reduced to 512MB (sufficient for DDNS operations)
- **Max Instances**: Limited to 10 (DDNS shouldn't need massive scale)
- **Benefit**: ~75% cost reduction vs. default 2GB/100 instances

### 3. Storage Account
- **SKU**: Standard_LRS (Locally Redundant Storage)
- **Rationale**: Cheapest tier, suitable for non-critical API keys/hostname data
- **Cost Savings**: ~50% vs. GRS (Geo-Redundant Storage)

### 4. Key Vault
- **SKU**: Standard (not Premium)
- **Soft Delete**: 7 days minimum retention
- **Purge Protection**: Disabled in dev (enable in production)

### 5. Log Analytics & Application Insights
- **Data Retention**: 90 days (minimum for Sentinel)
- **Recommendation**: Reduce to 30 days if Sentinel not needed

### 6. Microsoft Sentinel
- **Status**: Disabled by default (DDNS telemetry integration not yet implemented)
- **Cost Impact**: ~$2.50/GB ingested when enabled
- **Note**: Enable only after implementing DDNS-specific security analytics

## Environment Variables

```bash
# Budget Configuration
MONTHLY_BUDGET_LIMIT=50  # Monthly budget in USD
BUDGET_ALERT_EMAILS='["admin@example.com"]'  # JSON array of emails
ENABLE_BUDGET=true  # Enable/disable budget alerts

# Disable Sentinel to save costs (if not needed)
enableSentinel=false
```

## Estimated Monthly Costs

### Minimal Setup (No Sentinel) - RECOMMENDED
- Function App (Flex Consumption): ~$5-10/month
- Storage Account (LRS): ~$1/month
- Key Vault: ~$0.03/operation
- Application Insights: ~$2.30/GB
- Log Analytics Workspace: ~$0.12/GB retained
- **Total**: ~$10-20/month

### With Sentinel Enabled
- Add: ~$2.50/GB for Sentinel data ingestion
- With typical app logging: Additional $25-50/month
- **Total**: ~$40-80/month

Note: Sentinel integration with DDNS telemetry not yet implemented. Enable only after adding DDNS-specific threat detection rules.

## Cost Monitoring

### Azure Portal
1. Navigate to Cost Management + Billing
2. View Cost Analysis for resource group `rg-azure-ddns-dev`
3. Set up custom alerts in addition to budget alerts

### CLI Commands
```bash
# Check current month's cost
az consumption usage list \
  --start-date $(date +%Y-%m-01) \
  --end-date $(date +%Y-%m-%d) \
  --query "[?contains(instanceId, 'azure-ddns')].{Resource:instanceName, Cost:pretaxCost}" \
  --output table

# View budget status
az consumption budget show \
  --budget-name "budget-dev-monthly"
```

## Optimization Tips

1. **Development Environment**
   - Set `MONTHLY_BUDGET_LIMIT=20` for dev/test
   - Disable Sentinel during development
   - Use minimum instance memory (512MB)

2. **Production Environment**
   - Enable purge protection on Key Vault
   - Consider GRS storage for critical data
   - Enable Sentinel analytics rules
   - Set appropriate budget based on expected traffic

3. **Further Cost Reduction**
   - Implement caching to reduce DNS API calls
   - Use Azure Front Door for static content
   - Consider Reserved Instances for predictable workloads

## Alerts and Actions

When budget thresholds are exceeded:
1. Email notifications sent to configured addresses
2. Review Cost Analysis in Azure Portal
3. Consider scaling down non-critical resources
4. Investigate unexpected usage spikes

## Decommissioning

To completely remove all resources and stop charges:
```bash
azd down --purge
```

This will delete all resources and purge soft-deleted items.