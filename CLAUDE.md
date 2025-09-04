# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a hybrid Azure Functions project merging:
1. **Legacy PowerShell Azure DDNS Function** (`Get-DDnsUpdate/`): A dynamic DNS updater that modifies Azure DNS records
2. **Modern .NET 8 HTTP Functions** (`http/`): Sample C# isolated process Azure Functions using the latest Flex Consumption plan

## Architecture Assessment & Recommendations

### Current State Analysis

**PowerShell Function (Legacy)**
- Uses in-process PowerShell runtime with Azure Az module
- Implements basic auth validation against environment variables
- Directly manipulates Azure DNS records via Az.Dns module
- No proper error handling or logging infrastructure
- Security concerns: credentials stored in app settings, no managed identity usage

**C# .NET Functions (Template)**
- Modern isolated process model with .NET 8
- Uses Azure Functions Worker SDK v2.0
- Configured for Flex Consumption hosting plan
- Includes Application Insights integration
- Infrastructure-as-code with Bicep templates

### Production Refactoring Recommendation: ✅ Yes, migrate to .NET

The .NET implementation is the superior choice for production because:
1. **Performance**: C# compiled code significantly outperforms PowerShell scripts
2. **Security**: Supports managed identity authentication (already configured in Bicep)
3. **Maintainability**: Strong typing, better IDE support, comprehensive testing capabilities
4. **Monitoring**: Built-in Application Insights with structured logging
5. **Cost**: Flex Consumption plan provides better scaling and cost optimization
6. **DevOps**: Better CI/CD support with `azd` tooling

## Development Commands

```bash
# Local development
cd http
func start

# Run with debugging
# VS Code: Press F5
# Visual Studio: Open http.sln and press F5

# Deploy to Azure
azd up

# Clean up resources
azd down
```

## Key Architecture Components

### DDNS Update Flow (To Be Migrated)

**Hostname Claim Flow (New)**
1. User visits `/manage/{hostname}` for unclaimed hostname
2. MSAL triggers Azure AD authentication (Windows integrated or browser)
3. After successful auth, hostname is registered to user's identity
4. API key is generated for programmatic updates
5. Management dashboard is displayed

**Update Flow (Device/Router)**
1. Client sends GET request to `/nic/update` with API key auth
2. Function validates API key against hostname ownership
3. Extracts hostname and IP from query parameters (supports `myip=auto`)
4. Updates Azure DNS A record in Azure DNS zone
5. Returns DynDNS2 protocol responses (good/nochg/nohost/nofqdn)
6. Logs update history for dashboard display

### Required Migration Tasks for Production

1. **Port DDNS Logic to C#**
   - Create new function at `http/DdnsUpdateFunction.cs`
   - Use Azure.ResourceManager.Dns SDK instead of PowerShell Az module
   - Implement proper async/await patterns

2. **Dual Authentication System**
   
   **Browser/Human Access (MSAL-based)**
   - Implement MSAL.NET SDK for Azure AD authentication
   - Support Windows Integrated Authentication for domain-joined machines
   - Fallback to interactive browser login with organizational MFA
   - Create hostname management dashboard showing:
     - Current IP and update history
     - Generated API key for device updates
     - Update command examples (curl/wget/ddclient)
     - Recent update logs and success/failure metrics
   
   **Device/Programmatic Access (API Key)**
   - Generate unique API keys per hostname/user combination
   - Support DynDNS2 protocol for router compatibility
   - Basic auth header containing username:apikey
   - Rate limiting per API key

3. **Hostname Management System**
   
   **Claim Process**
   - GET request to unclaimed hostname triggers MSAL authentication
   - After successful auth, hostname is bound to user's Azure AD identity
   - Store mapping in Azure Table Storage or Cosmos DB
   - Generate initial API key for updates
   
   **Management Interface** (`/manage/{hostname}`)
   - Requires MSAL authentication matching hostname owner
   - Shows dashboard with:
     ```
     Hostname: mydevice.ddns.example.com
     Current IP: 203.0.113.42
     Last Updated: 2025-01-03 14:23:01 UTC
     Update Count: 1,247
     
     API Key: xxxxxxxx-xxxx-xxxx-xxxxxxxxxxxx
     
     Update Commands:
     curl -u mydevice:apikey "https://ddns.example.com/nic/update?hostname=mydevice&myip=auto"
     wget --auth-no-challenge --user=mydevice --password=apikey "https://..."
     
     Recent Updates:
     2025-01-03 14:23:01 - 203.0.113.42 - Success
     2025-01-03 08:15:33 - 203.0.113.41 - Success
     2025-01-02 22:47:12 - 198.51.100.89 - Success
     ```

4. **Add Operational Features**
   - Implement rate limiting (per API key and per IP)
   - Add request validation and sanitization
   - Create comprehensive logging with correlation IDs
   - Add health check endpoint
   - Implement IP history retention (30-90 days)
   - Auto-detect IP if `myip=auto` parameter used

5. **Infrastructure Updates**
   - Configure RBAC for DNS Zone Contributor role
   - Add Key Vault for API key storage
   - Configure Azure Table Storage for hostname ownership
   - Set up Application Insights custom metrics
   - Configure custom domain with SSL

## Azure DNS Integration

### Domain Configuration
The system expects DNS zones to be hosted in Azure DNS:
- **Primary Domain**: Must be registered through Azure Domains or delegated to Azure DNS
- **Subdomains**: Automatically managed as A records within the Azure DNS zone
- **Zone Structure**: 
  - Root zone (e.g., `example.com`) managed in Azure DNS
  - DDNS subdomains as A records (e.g., `*.ddns.example.com`)
  - Each user can claim multiple hostnames under the DDNS subdomain

### DNS Zone Requirements
- DNS Zone must exist in Azure subscription (can be different from function app subscription)
- Function app's managed identity requires TWO roles:
  - **Reader** role on the DNS resource group (for cross-subscription resource group access)
  - **DNS Zone Contributor** role on the DNS zone itself (for modifying DNS records)
- Support for wildcard subdomain delegation (e.g., `*.ddns.example.com`)
- Automatic TTL management (60 seconds for dynamic records)

### Cross-Subscription Deployment Requirements
- **Deploying user** must have Owner or User Access Administrator role in BOTH:
  - Function app subscription (to create resources)
  - DNS zone subscription (to assign roles to managed identity)
- Role assignments may take up to 10 minutes to propagate after deployment
- If deployment fails with permission errors, verify you have role assignment permissions in the DNS subscription

### DNS Permission Verification
The cross-subscription DNS permissions are managed via deterministic GUID-based role assignments in Bicep:
- `infra/app/dns-rbac.bicep` deploys to the DNS subscription scope
- `infra/app/dns-role-assignment.bicep` creates two role assignments:
  - **Reader** role on the DNS resource group (`domains-dns`)  
  - **DNS Zone Contributor** role on the specific DNS zone (`title.dev`)
- Role assignment names use `guid(scope, principalId, roleDefinitionId)` pattern for deterministic deployment
- To verify permissions: `az role assignment list --all --assignee {managed-identity-principal-id} --query "[?contains(scope, 'title.dev')]"`

**Current Status**: Managed identity `c0ba60e6-cf9e-4687-bdfe-ae567b5c11d3` has DNS Zone Contributor access to `title.dev` zone ✅

## Environment Variables Required

For DDNS function (current PowerShell, future C#):
- `AppUsername`: Basic auth username (legacy, to be replaced)
- `AppPassword`: Basic auth password (legacy, to be replaced)
- `DnsZoneRGName`: Resource group containing Azure DNS zones
- `DnsZoneName`: Azure DNS zone name (e.g., `example.com`)
- `DdnsSubdomain`: Subdomain for DDNS records (e.g., `ddns` for `*.ddns.example.com`)

## Authentication Requirements

**CRITICAL**: All browser-accessible endpoints MUST implement manual EasyAuth redirect logic because:

1. EasyAuth is configured with `globalValidation.requireAuthentication: false` and `unauthenticatedClientAction: 'AllowAnonymous'`
2. This means EasyAuth headers are only provided AFTER a manual redirect to `/.auth/login/aad`
3. Without this redirect, functions receive no authentication headers and must handle unauthenticated requests

**Required pattern for all browser endpoints:**
```csharp
var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);

if (string.IsNullOrEmpty(userId))
{
    // No authentication, redirect to Azure AD login via EasyAuth
    var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
    var encodedRedirect = Uri.EscapeDataString("/api/your/route");
    var redirectUrl = $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}";
    response.Headers.Add("Location", redirectUrl);
    return response;
}

// User is now authenticated, proceed with function logic
```

This pattern is implemented in:
- ✅ `HostnameManagementFunction.cs` - `/api/manage/{hostname}`
- ✅ `AdminFunction.cs` - `/api/management` (route changed from `admin/panel` to `management`)
- ❌ **Any new browser endpoints must follow this pattern**

### Azure AD App Configuration (Current State)
**Azure AD App ID**: `3109db3d-f7e7-4d55-ae7d-ac2170d1335c`

**Current Redirect URIs**:
- `http://localhost:7071/.auth/login/aad/callback` (local dev)
- `http://localhost:7071/api/manage/callback` (local dev)
- `https://func-api-cq3maraez745s.azurewebsites.net/.auth/login/aad/callback` (production)

**Current URLs**:
- **Homepage URL**: `https://func-api-cq3maraez745s.azurewebsites.net/`
- **Logout URL**: `https://func-api-cq3maraez745s.azurewebsites.net/.auth/logout`

## Testing

```bash
# Test hostname claim (browser-based, will redirect to Azure AD login)
curl "http://localhost:7071/manage/mydevice.ddns.example.com"

# Test DDNS update with API key (after claiming hostname)
curl -u mydevice:your-api-key "http://localhost:7071/nic/update?hostname=mydevice.ddns.example.com&myip=auto"

# Legacy format (to be deprecated)
curl -u username:password "http://localhost:7071/nic/update?hostname=test.example.com&myip=1.2.3.4"

# Expected responses:
# "good" - IP updated successfully
# "nochg" - IP unchanged  
# "nohost" - Hostname not found or not owned by user
# "nofqdn" - DNS record is an alias
# "401" - Authentication required (for unclaimed hostnames)
```

## Deployment Configuration

The project uses Azure Developer CLI (`azd`) with:
- Bicep templates in `infra/`
- Flex Consumption hosting plan
- Optional VNet integration
- Managed identity for secure resource access
- Application Insights for monitoring

Target regions must support Flex Consumption plan (see `infra/main.bicep` for allowed list).

## Custom Domain Implementation Status

### Phase 1: Pre-Flight Validation ✅ COMPLETED
- **Target Environment**: `ddns-sandbox.title.dev` (sandbox), production will use `ddns.title.dev`
- **Function App**: `https://func-api-cq3maraez745s.azurewebsites.net/` responds correctly
- **EasyAuth**: Working properly - admin panel accessible with Azure AD authentication
- **DNS Permissions**: Verified - managed identity has DNS Zone Contributor access to `title.dev`
- **DNS Conflicts**: None - `ddns-sandbox` subdomain not in use

### Phase 2-6: Implementation Pending
Following comprehensive 6-phase deployment plan with validation gates to prevent common deployment failures.