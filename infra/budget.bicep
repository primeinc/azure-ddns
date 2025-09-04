// THIS IS A RESOURCE GROUP SCOPED BUDGET - Deploys directly to the RG
targetScope = 'resourceGroup'

@description('The name of the budget')
param budgetName string

@description('The total amount of cost to track with the budget (in USD)')
param budgetAmount int

@description('Contact emails for budget alerts')
param contactEmails array

@description('Start date for the budget (defaults to first day of current month)')
param startDate string = '${utcNow('yyyy-MM')}-01T00:00:00Z'

// Deploy budget as direct child of resource group - shows in RG > Budgets
resource budget 'Microsoft.Consumption/budgets@2024-08-01' = {
  name: budgetName
  properties: {
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    category: 'Cost'
    // NO FILTER NEEDED - inherently scoped to this resource group
    notifications: {
      NotificationForExceededBudget1: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
      NotificationForExceededBudget2: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 75
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
      NotificationForExceededBudget3: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 90
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
      NotificationForExceededBudget4: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
      NotificationForExceededBudget5: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 110
        contactEmails: contactEmails
        thresholdType: 'Forecasted'
      }
    }
  }
}

output budgetId string = budget.id
output budgetName string = budget.name