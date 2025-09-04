# Research Task: Azure Resource Group Scoped Budget Implementation

## Critical Requirements
Create an Azure budget that:
1. **Shows up in the RESOURCE GROUP's Budgets view** in Azure Portal (NOT the subscription's budget list)
2. Navigate to: Resource Group > Cost Management > Budgets - the budget MUST appear here
3. Only tracks costs for resources in `rg-azure-ddns-dev` resource group
4. Does NOT track any other resources in the subscription

## Current Failed Approach (DO NOT USE)
```bicep
// THIS IS WRONG - Creates subscription-level budget with filter
targetScope = 'subscription'
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  // This creates at subscription level with a filter
  // Budget shows in subscription view, NOT in resource group view
}
```

## What We Need
- Budget that is actually scoped to the resource group level
- Must appear when navigating to: rg-azure-ddns-dev > Cost Management > Budgets
- Currently that view shows "You do not have any budgets"
- The subscription has tons of other resources/costs that should NOT be included

## Research Requirements
1. Find the correct Azure resource type for resource group scoped budgets
2. Determine if Microsoft.Consumption/budgets can be deployed at resourceGroup scope
3. If not, identify the alternative approach (possibly Microsoft.CostManagement resources?)
4. Provide working Bicep code that creates a budget visible in the resource group's budget view
5. Verify the solution with actual Azure documentation links

## Context
- Environment: Azure subscription with multiple resource groups
- Target: Only track costs for `rg-azure-ddns-dev` 
- Budget: $50/month with alerts at 50%, 75%, 90%, 100%, 110% thresholds
- Notification emails: will@4pp.dev, shelby@4pp.dev

## Expected Solution Format
Provide:
1. Complete working Bicep module for resource group scoped budget
2. Explanation of why this approach works vs the failed subscription-level approach
3. Azure documentation links confirming this is the correct method
4. Portal navigation path where the budget will appear

## Important Notes
- User has explicitly stated they do NOT want a subscription budget
- The budget MUST appear in the resource group's own budget view
- Previous attempts using subscription-level budgets with filters have failed to meet requirements
- This is a critical cost control feature for production deployment