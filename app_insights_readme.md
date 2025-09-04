# App Insights Debugging Guide

## The Problem We Just Solved

The admin panel was returning 404 because of TWO issues:
1. **Route conflict**: `Route = "admin"` conflicts with built-in Azure Functions routes
2. **Wrong auth level**: Had `AuthorizationLevel.Anonymous` which bypasses EasyAuth

## Key Lessons Learned

### 1. Git Bash Path Mangling Issue
**Problem**: Azure CLI commands with resource IDs like `/subscriptions/...` get mangled by Git Bash into `C:/Program Files/Git/subscriptions/...`

**Solutions**:
```bash
# Option A: Disable MSYS path conversion
MSYS2_ARG_CONV_EXCL='*' az monitor log-analytics query --workspace '/full/resource/id' --analytics-query "..."

# Option B: Use customerId GUID instead of resource ID (RECOMMENDED)
CID=$(az monitor log-analytics workspace show -g myResourceGroup -n myWorkspace --query customerId -o tsv)
az monitor log-analytics query --workspace "$CID" --analytics-query "..."

# Option C: Use PowerShell instead of Git Bash (no path mangling)
```

### 2. App Insights Table Names
**Don't use**: `requests`, `traces`, `exceptions` (these are classic App Insights)
**Use instead**: `AppRequests`, `AppTraces`, `AppExceptions` (workspace-based)

### 3. Useful KQL Queries for Azure Functions Debugging

#### Find function startup errors:
```kql
AppTraces
| where Message contains "function is in error" or Message contains "route conflicts"
| project TimeGenerated, Message, Properties
| order by TimeGenerated desc
```

#### Find requests to specific endpoints:
```kql
AppRequests
| where Url endswith "/api/admin-panel"
| project TimeGenerated, ResultCode, DurationMs, Url, OperationId, Name
| order by TimeGenerated desc
```

#### Correlate failing requests with traces:
```kql
let bad = AppRequests
| where Url contains "admin" and toint(ResultCode) >= 400
| top 10 by TimeGenerated desc
| project OperationId;
AppTraces
| where OperationId in (bad)
| order by TimeGenerated asc
```

#### Check EasyAuth issues:
```kql
AppTraces
| where Message has_any ("EasyAuth", "X-MS-CLIENT-PRINCIPAL", "401", "403")
| project TimeGenerated, Message, SeverityLevel, OperationId
| order by TimeGenerated desc
```

### 4. Azure Functions Route Conflicts
**Avoid these route names** (they conflict with built-in routes):
- `admin` ❌
- `api` ❌  
- `runtime` ❌
- `health` (might conflict)

**Use instead**:
- `admin-panel` ✅
- `management` ✅
- `dashboard` ✅

### 5. EasyAuth vs Anonymous Authorization
**Wrong way** (bypasses EasyAuth):
```csharp
[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin")]
```

**Right way** (uses EasyAuth):
```csharp
[HttpTrigger(AuthorizationLevel.Function, "get", Route = "admin-panel")]
```

With `AuthorizationLevel.Function`:
- EasyAuth handles authentication automatically
- Unauthenticated users get redirected to Azure AD login
- Function only executes for authenticated users
- User info available in `X-MS-CLIENT-PRINCIPAL` header

## Step-by-Step Debugging Process

### 1. Get App Insights workspace info:
```bash
az monitor app-insights component show \
  -g myResourceGroup \
  -a myAppInsightsName \
  --query "{appId:appId, workspaceId:workspaceResourceId, kind:kind}"
```

### 2. Query logs (workspace-based):
```bash
# Get the customerId
CID=$(az monitor log-analytics workspace show -g rg-name -n workspace-name --query customerId -o tsv)

# Query with customerId (avoids path mangling)
az monitor log-analytics query \
  --workspace "$CID" \
  --analytics-query "AppRequests | order by TimeGenerated desc | take 20"
```

### 3. Check for function errors:
```kql
AppTraces
| where Message contains "error" or SeverityLevel >= 3
| project TimeGenerated, Message, SeverityLevel
| order by TimeGenerated desc
| take 20
```

### 4. Test the endpoint:
```bash
# Test with verbose curl to see actual response
curl -v https://your-function-app.azurewebsites.net/api/your-route

# Check if it's EasyAuth (302 redirect) or function error (404 from Kestrel)
```

## Common Issues & Solutions

| Issue | Symptoms | Solution |
|-------|----------|----------|
| Route conflict | Function shows as deployed but returns 404, startup errors in logs | Change route name |
| EasyAuth bypassed | Function executes for unauthenticated users | Use `AuthorizationLevel.Function` |
| Path mangling | CLI commands fail with "path does not exist" | Use customerId GUID instead of resource ID |
| Wrong table names | KQL queries return "table not found" | Use App* prefixed tables for workspace-based |

## Testing Your Admin Panel

1. **Deploy**: `azd deploy`
2. **Check logs**: Look for startup errors immediately after deployment
3. **Test unauthenticated**: `curl https://your-app.net/api/admin-panel` (should redirect to login)
4. **Test authenticated**: Visit in browser (should show admin panel if you have admin roles)
5. **Check App Insights**: Verify requests are logging properly

Remember: With proper EasyAuth setup, your functions should NEVER handle authentication manually - let Azure do the heavy lifting!