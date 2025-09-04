# Multi-Tenant Azure DDNS Service Refactoring Plan

## 1.0 Executive Summary

This document outlines the strategic refactoring of an existing PowerShell-based Azure Functions dynamic DNS (DDNS) service into a modern, multi-tenant .NET 8 cloud-native architecture. The refactored service will enable authenticated users to claim hostnames under `ddns.title.dev` and receive API keys for their network devices (UniFi, UBNT, etc.) to update DNS records automatically.

### Key Objectives

- **Multi-Tenant Architecture**: Enable any MSAL-authenticated user within the organization to claim and manage multiple hostnames
- **Dual Authentication**: MSAL for web management interface, API keys for DynDNS2 protocol compatibility
- **Cross-Subscription Deployment**: Deploy to 4PP Sandbox Lab subscription with cross-subscription access to DNS zones in Azure subscription 1
- **Modern Tech Stack**: Migrate from PowerShell to .NET 8 Isolated Worker model with managed identities
- **Production Ready**: Implement proper error handling, logging, and monitoring with Application Insights

### Critical Risks Addressed

1. **End of Support**: In-process function model support ends November 10, 2026
2. **Security Vulnerabilities**: Current Basic Auth with credentials in app settings
3. **Operational Debt**: Manual configuration and lack of Infrastructure as Code

## 2.0 Architecture Overview

### 2.1 Current State (PowerShell)

- In-process PowerShell Azure Function
- Basic Authentication with credentials in app settings
- Direct Azure DNS manipulation via Az.Dns module
- Single-user focused design
- No hostname ownership tracking

### 2.2 Target State (.NET 8 Multi-Tenant)

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   User Browser  │────▶│  MSAL Auth Flow  │────▶│  Management UI  │
└─────────────────┘     └──────────────────┘     └─────────────────┘
                                                           │
┌─────────────────┐     ┌──────────────────┐            ▼
│  UniFi Device   │────▶│  DynDNS2 + API   │     ┌─────────────────┐
└─────────────────┘     │      Key Auth     │────▶│ Function App    │
                        └──────────────────┘     │   (.NET 8)      │
                                                  └─────────────────┘
                                                           │
                                ┌──────────────────────────┼──────────────────┐
                                ▼                          ▼                  ▼
                        ┌──────────────┐     ┌──────────────────┐   ┌──────────────┐
                        │Table Storage │     │  Azure DNS       │   │ App Insights │
                        │(Ownership)   │     │  (title.dev)     │   │              │
                        └──────────────┘     └──────────────────┘   └──────────────┘
```

### 2.3 Key Architectural Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Runtime | .NET 8 Isolated Worker | Future-proof, better isolation, modern SDK support |
| Authentication | Dual: MSAL + API Keys | User-friendly web management, device compatibility |
| Data Storage | Azure Table Storage | Simple, cost-effective for hostname ownership data |
| Deployment | Cross-subscription | Sandbox for testing, production DNS zones separate |
| Identity | User-Assigned Managed Identity | Flexible, shareable across resources |
| Hostname Scope | Users enter "mydevice" only | System constructs full FQDN automatically |

## 3.0 Multi-Tenant MVP Scope

### 3.1 Phase 1: MVP Features

| Feature Category | In Scope | Out of Scope (Phase 2+) |
|-----------------|----------|-------------------------|
| **Core Functionality** | • A record updates for `*.ddns.title.dev`<br>• Single hostname per request<br>• IPv4 only | • AAAA records (IPv6)<br>• Bulk updates<br>• Multiple DNS zones |
| **Authentication** | • MSAL for management UI<br>• API key generation per hostname<br>• Basic auth header for DynDNS2 | • Key Vault integration<br>• OAuth2 for devices<br>• API key rotation |
| **Multi-Tenancy** | • Any authenticated user can claim hostnames<br>• Unlimited hostnames per user<br>• Table Storage for ownership | • User quotas<br>• Hostname transfers<br>• Admin override capabilities |
| **Management UI** | • `/manage/{hostname}` endpoint<br>• Show current IP and API key<br>• Basic update history | • Full dashboard<br>• Analytics<br>• Bulk management |
| **Protocol Support** | • DynDNS2: `myip`, `hostname` params<br>• Response codes: `good`, `nochg`, `badauth`, `nohost` | • Full DynDNS2 spec<br>• Alternative protocols<br>• Webhook notifications |
| **Security** | • Reserved hostname blocking<br>• IP address validation<br>• HTTPS only | • Rate limiting per key<br>• MFA for management<br>• Audit logging |
| **Deployment** | • Sandbox subscription<br>• Cross-subscription RBAC<br>• Single region | • Multi-region<br>• Custom domains<br>• CDN integration |

### 3.2 Data Model

#### Table Storage Entities

**HostnameOwnership Table**
```csharp
public class HostnameOwnership : TableEntity
{
    public string Hostname { get; set; }           // PartitionKey
    public string UserId { get; set; }             // RowKey (Azure AD Object ID)
    public string UserPrincipalName { get; set; }  // user@domain.com
    public string ApiKey { get; set; }             // Generated UUID
    public DateTime ClaimedAt { get; set; }
    public DateTime LastUpdated { get; set; }
    public string CurrentIp { get; set; }
    public int UpdateCount { get; set; }
}
```

**UpdateHistory Table**
```csharp
public class UpdateHistory : TableEntity
{
    public string Hostname { get; set; }           // PartitionKey
    public string Timestamp { get; set; }          // RowKey (reverse ticks)
    public string IpAddress { get; set; }
    public string UpdateSource { get; set; }       // "API" or "WebUI"
    public bool Success { get; set; }
    public string ResponseCode { get; set; }       // good/nochg/badauth/etc
}
```

## 4.0 Implementation Plan

### 4.1 Phase 1: MVP Implementation

#### 4.1.1 Infrastructure Setup

**Bicep Template Structure**
```bicep
// main.bicep
param location string = 'eastus2'
param environment string = 'sandbox'
param dnsSubscriptionId string
param dnsResourceGroupName string = 'domains-dns'
param dnsZoneName string = 'title.dev'

// Function App with Flex Consumption Plan
module functionApp 'modules/function-app.bicep' = {
  name: 'ddns-function-app'
  params: {
    location: location
    environment: environment
    sku: 'FC1'
    runtime: 'dotnet-isolated|8.0'
  }
}

// User-Assigned Managed Identity
module identity 'modules/managed-identity.bicep' = {
  name: 'ddns-identity'
  params: {
    location: location
  }
}

// Cross-subscription DNS Zone Contributor role
module dnsRoleAssignment 'modules/cross-sub-rbac.bicep' = {
  name: 'dns-zone-contributor'
  params: {
    principalId: identity.outputs.principalId
    targetSubscriptionId: dnsSubscriptionId
    targetResourceGroupName: dnsResourceGroupName
    roleDefinitionId: 'befefa01-2a29-4197-83a8-272ff33ce314' // DNS Zone Contributor
  }
}

// Table Storage for hostname ownership
module storage 'modules/storage.bicep' = {
  name: 'ddns-storage'
  params: {
    location: location
    tables: ['HostnameOwnership', 'UpdateHistory']
  }
}

// Application Insights
module monitoring 'modules/app-insights.bicep' = {
  name: 'ddns-monitoring'
  params: {
    location: location
    functionAppId: functionApp.outputs.id
  }
}
```

#### 4.1.2 Authentication Implementation

**MSAL Configuration (Program.cs)**
```csharp
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.UseMiddleware<DualAuthMiddleware>();
    })
    .ConfigureServices(services =>
    {
        // MSAL for web management interface
        services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(options =>
            {
                options.Instance = "https://login.microsoftonline.com/";
                options.TenantId = Environment.GetEnvironmentVariable("AzureAd__TenantId");
                options.ClientId = Environment.GetEnvironmentVariable("AzureAd__ClientId");
                options.CallbackPath = "/signin-oidc";
            });
        
        // Services
        services.AddSingleton<ITableStorageService, TableStorageService>();
        services.AddSingleton<IDnsUpdateService, DnsUpdateService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();
    })
    .Build();

host.Run();
```

**Dual Authentication Middleware**
```csharp
public class DualAuthMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ITableStorageService _tableStorage;
    
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var request = await context.GetHttpRequestDataAsync();
        var path = request.Url.AbsolutePath;
        
        // Management UI routes require MSAL authentication
        if (path.StartsWith("/manage/"))
        {
            // MSAL auth is handled by the authentication middleware
            await next(context);
            return;
        }
        
        // DynDNS2 update routes require API key authentication
        if (path.StartsWith("/nic/update"))
        {
            var authHeader = request.Headers.GetValues("Authorization")?.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic "))
            {
                await RespondWithError(context, HttpStatusCode.Unauthorized, "badauth");
                return;
            }
            
            // Decode Basic auth header
            var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
            var decodedBytes = Convert.FromBase64String(encodedCredentials);
            var credentials = Encoding.UTF8.GetString(decodedBytes);
            var parts = credentials.Split(':');
            
            if (parts.Length != 2)
            {
                await RespondWithError(context, HttpStatusCode.Unauthorized, "badauth");
                return;
            }
            
            var hostname = parts[0];
            var apiKey = parts[1];
            
            // Validate API key against hostname ownership
            var ownership = await _tableStorage.GetHostnameOwnership(hostname);
            if (ownership == null || ownership.ApiKey != apiKey)
            {
                await RespondWithError(context, HttpStatusCode.Unauthorized, "badauth");
                return;
            }
            
            // Store ownership info in context for the function to use
            context.Items["HostnameOwnership"] = ownership;
            await next(context);
        }
    }
}
```

#### 4.1.3 Core Function Implementations

**Hostname Claim Function**
```csharp
[Function("ClaimHostname")]
public async Task<HttpResponseData> ClaimHostname(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/{hostname}")] 
    HttpRequestData req,
    string hostname,
    FunctionContext context)
{
    var logger = context.GetLogger("ClaimHostname");
    
    // Get authenticated user from MSAL
    var user = context.Features.Get<ClaimsPrincipal>();
    if (user == null || !user.Identity.IsAuthenticated)
    {
        // Redirect to MSAL login
        var response = req.CreateResponse(HttpStatusCode.Redirect);
        response.Headers.Add("Location", $"/signin-oidc?returnUrl=/manage/{hostname}");
        return response;
    }
    
    // Validate hostname format and check reserved names
    if (!IsValidHostname(hostname))
    {
        return await CreateErrorResponse(req, "Invalid hostname format");
    }
    
    if (IsReservedHostname(hostname))
    {
        return await CreateErrorResponse(req, "Hostname is reserved");
    }
    
    // Check if hostname is already claimed
    var existingOwnership = await _tableStorage.GetHostnameOwnership(hostname);
    if (existingOwnership != null)
    {
        // Check if current user owns it
        var userId = user.FindFirst("oid")?.Value;
        if (existingOwnership.UserId != userId)
        {
            return await CreateErrorResponse(req, "Hostname already claimed by another user");
        }
        
        // User already owns it, show management page
        return await CreateManagementPage(req, hostname, existingOwnership);
    }
    
    // Claim the hostname for this user
    var ownership = new HostnameOwnership
    {
        PartitionKey = hostname,
        RowKey = user.FindFirst("oid")?.Value,
        Hostname = hostname,
        UserId = user.FindFirst("oid")?.Value,
        UserPrincipalName = user.Identity.Name,
        ApiKey = Guid.NewGuid().ToString(),
        ClaimedAt = DateTime.UtcNow,
        LastUpdated = DateTime.UtcNow,
        UpdateCount = 0
    };
    
    await _tableStorage.CreateHostnameOwnership(ownership);
    logger.LogInformation($"Hostname {hostname} claimed by {user.Identity.Name}");
    
    return await CreateManagementPage(req, hostname, ownership);
}
```

**DNS Update Function**
```csharp
[Function("UpdateDns")]
public async Task<HttpResponseData> UpdateDns(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "nic/update")] 
    HttpRequestData req,
    FunctionContext context)
{
    var logger = context.GetLogger("UpdateDns");
    
    // Get ownership info from middleware
    var ownership = context.Items["HostnameOwnership"] as HostnameOwnership;
    if (ownership == null)
    {
        return await CreateDynDnsResponse(req, "badauth");
    }
    
    // Parse query parameters
    var query = HttpUtility.ParseQueryString(req.Url.Query);
    var hostname = query["hostname"];
    var myip = query["myip"];
    
    // Validate hostname matches authenticated hostname
    if (!hostname.Equals($"{ownership.Hostname}.ddns.title.dev", StringComparison.OrdinalIgnoreCase))
    {
        return await CreateDynDnsResponse(req, "nohost");
    }
    
    // Handle auto IP detection
    if (myip == "auto" || string.IsNullOrEmpty(myip))
    {
        myip = GetClientIpAddress(req);
    }
    
    // Validate IP address
    if (!IPAddress.TryParse(myip, out var ipAddress))
    {
        return await CreateDynDnsResponse(req, "notfqdn");
    }
    
    // Check if IP has changed
    if (ownership.CurrentIp == myip)
    {
        logger.LogInformation($"No change for {hostname} - IP remains {myip}");
        return await CreateDynDnsResponse(req, "nochg");
    }
    
    // Update DNS record
    try
    {
        await _dnsService.UpdateARecord(ownership.Hostname, ipAddress);
        
        // Update ownership record
        ownership.CurrentIp = myip;
        ownership.LastUpdated = DateTime.UtcNow;
        ownership.UpdateCount++;
        await _tableStorage.UpdateHostnameOwnership(ownership);
        
        // Log update history
        await _tableStorage.LogUpdate(new UpdateHistory
        {
            PartitionKey = hostname,
            RowKey = DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks.ToString(),
            Hostname = hostname,
            IpAddress = myip,
            UpdateSource = "API",
            Success = true,
            ResponseCode = "good",
            Timestamp = DateTime.UtcNow
        });
        
        logger.LogInformation($"Updated {hostname} to {myip}");
        return await CreateDynDnsResponse(req, "good");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, $"Failed to update DNS for {hostname}");
        return await CreateDynDnsResponse(req, "dnserr");
    }
}
```

#### 4.1.4 DNS Service Implementation

```csharp
public class DnsUpdateService : IDnsUpdateService
{
    private readonly ArmClient _armClient;
    private readonly string _dnsZoneName = "title.dev";
    private readonly string _dnsResourceGroup = "domains-dns";
    private readonly string _dnsSubscriptionId;
    
    public DnsUpdateService(IConfiguration configuration)
    {
        // Use managed identity for authentication
        _armClient = new ArmClient(new DefaultAzureCredential());
        _dnsSubscriptionId = configuration["DnsSubscriptionId"];
    }
    
    public async Task<bool> UpdateARecord(string hostname, IPAddress ipAddress)
    {
        // Get the subscription and resource group
        var subscription = _armClient.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{_dnsSubscriptionId}"));
        var resourceGroup = await subscription.GetResourceGroupAsync(_dnsResourceGroup);
        
        // Get DNS zone
        var dnsZone = await resourceGroup.Value.GetDnsZoneAsync(_dnsZoneName);
        
        // Prepare A record data
        var recordSetName = $"{hostname}.ddns";
        var aRecordData = new DnsARecordData
        {
            TtlInSeconds = 60, // Low TTL for dynamic DNS
            DnsARecords = { new DnsARecordInfo { IPv4Address = ipAddress } }
        };
        
        // Create or update the record
        var aRecordCollection = dnsZone.Value.GetDnsARecords();
        await aRecordCollection.CreateOrUpdateAsync(
            WaitUntil.Completed, 
            recordSetName, 
            aRecordData);
        
        return true;
    }
}
```

### 4.2 Configuration Settings

| Setting Name | Purpose | Example Value |
|-------------|---------|---------------|
| `AzureAd__TenantId` | MSAL tenant ID | `your-tenant-id` |
| `AzureAd__ClientId` | MSAL application ID | `your-app-id` |
| `DnsSubscriptionId` | Target subscription for DNS zone | `subscription-id` |
| `DnsResourceGroup` | Resource group containing DNS zone | `domains-dns` |
| `DnsZoneName` | DNS zone name | `title.dev` |
| `StorageConnection` | Connection string for Table Storage | `DefaultEndpointsProtocol=...` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights connection | `InstrumentationKey=...` |

### 4.3 Reserved Hostnames

```csharp
private static readonly HashSet<string> ReservedHostnames = new()
{
    "www", "mail", "ftp", "admin", "administrator", "root",
    "api", "app", "portal", "console", "dashboard", "manage",
    "test", "staging", "prod", "production", "dev", "development",
    "localhost", "ns1", "ns2", "mx", "smtp", "pop", "imap",
    "vpn", "ssh", "rdp", "backup", "monitor", "status"
};
```

## 5.0 Testing Strategy

### 5.1 Local Development Testing

```bash
# Start Azure Storage Emulator or Azurite
azurite --silent --location ./azurite --debug ./azurite/debug.log

# Run function locally
func start

# Test hostname claim (browser)
open http://localhost:7071/manage/mydevice

# Test DNS update with API key
curl -u "mydevice:generated-api-key" \
  "http://localhost:7071/nic/update?hostname=mydevice.ddns.title.dev&myip=auto"
```

### 5.2 UniFi Configuration

```yaml
# UniFi Dream Machine DDNS Settings
Service: custom
Hostname: mydevice.ddns.title.dev
Username: mydevice
Password: [API-KEY-FROM-MANAGEMENT-UI]
Server: ddns-sandbox.azurewebsites.net/nic/update?hostname=%h&myip=%i
```

### 5.3 Integration Tests

```csharp
[TestClass]
public class DdnsIntegrationTests
{
    [TestMethod]
    public async Task ClaimHostname_NewHostname_Success()
    {
        // Arrange
        var hostname = $"test-{Guid.NewGuid():N}";
        
        // Act
        var response = await AuthenticatedClient.GetAsync($"/manage/{hostname}");
        
        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Content.Contains("API Key:"));
    }
    
    [TestMethod]
    public async Task UpdateDns_ValidApiKey_ReturnsGood()
    {
        // Arrange
        var hostname = await ClaimTestHostname();
        var apiKey = await GetApiKeyForHostname(hostname);
        
        // Act
        var response = await UpdateDnsWithApiKey(hostname, apiKey, "1.2.3.4");
        
        // Assert
        Assert.AreEqual("good", response);
    }
}
```

## 6.0 Deployment Process

### 6.1 Azure Developer CLI Deployment

```bash
# Initialize azd environment
azd init -e ddns-sandbox

# Set required parameters
azd env set DNS_SUBSCRIPTION_ID "your-dns-subscription-id"
azd env set AZURE_AD_TENANT_ID "your-tenant-id"
azd env set AZURE_AD_CLIENT_ID "your-app-registration-id"

# Deploy to Sandbox
azd up --subscription "4PP Sandbox Lab"

# Verify deployment
azd monitor --live
```

### 6.2 Production Migration Checklist

- [ ] Backup existing PowerShell function configuration
- [ ] Deploy to Sandbox and complete testing
- [ ] Configure production managed identity
- [ ] Set up cross-subscription RBAC for production
- [ ] Migrate any existing hostname mappings
- [ ] Update UniFi devices with new endpoints
- [ ] Monitor for 24-48 hours before decommissioning old service

## 7.0 Security Considerations

### 7.1 Threat Model

| Threat | Mitigation |
|--------|------------|
| API key compromise | Keys are per-hostname, limiting blast radius |
| Subdomain takeover | Managed identity ensures only authorized updates |
| Brute force attacks | Rate limiting in Phase 2, monitoring in Phase 1 |
| DNS cache poisoning | Low TTL (60s) limits impact duration |
| Unauthorized claiming | MSAL authentication required |

### 7.2 Compliance

- **Data Residency**: All data stored in East US 2 region
- **Encryption**: TLS for all communications, encryption at rest for Table Storage
- **Audit Trail**: All updates logged with timestamp and source
- **Access Control**: RBAC for Azure resources, MSAL for user access

## 8.0 Monitoring and Operations

### 8.1 Key Metrics

```kusto
// Application Insights KQL Queries

// Successful updates per hour
requests
| where name == "UpdateDns"
| where resultCode == 200
| summarize Updates = count() by bin(timestamp, 1h)

// Failed authentications
requests
| where name == "UpdateDns" and resultCode == 401
| summarize FailedAuth = count() by bin(timestamp, 1h)

// Most active hostnames
customEvents
| where name == "DnsUpdated"
| extend hostname = tostring(customDimensions.hostname)
| summarize Updates = count() by hostname
| top 10 by Updates desc
```

### 8.2 Operational Runbook

| Issue | Detection | Resolution |
|-------|-----------|------------|
| High update failures | Alert on >10% failure rate | Check DNS zone permissions |
| API key brute force | >100 auth failures/hour from same IP | Implement IP blocking |
| Storage throttling | Table Storage exceptions | Implement retry logic |
| Cross-sub RBAC failure | DNS update exceptions | Verify managed identity permissions |

## 9.0 Future Enhancements (Phase 2+)

- **IPv6 Support**: AAAA record updates
- **Advanced Management UI**: React SPA with real-time updates
- **API Key Rotation**: Automated rotation with device notification
- **Webhook Support**: Notify external systems on IP changes
- **Geographic Redundancy**: Multi-region deployment with Traffic Manager
- **Premium Features**: Custom domains, multiple zones, SLA guarantees

## 10.0 Conclusion

This refactoring plan provides a clear path from the current single-user PowerShell implementation to a modern, multi-tenant .NET 8 DDNS service. The phased approach ensures we can deliver a working MVP quickly while maintaining flexibility for future enhancements. The dual authentication system (MSAL + API keys) ensures both user-friendly management and device compatibility, making this solution ideal for UniFi and other DDNS-compatible network equipment.