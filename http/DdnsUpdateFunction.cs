using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;

namespace Company.Function
{
    public class DdnsUpdateFunction
    {
        private readonly ILogger<DdnsUpdateFunction> _logger;
        private static int _invocationCount = 0;

        public DdnsUpdateFunction(ILogger<DdnsUpdateFunction> logger)
        {
            _logger = logger;
            _logger.LogInformation("====== DdnsUpdateFunction CONSTRUCTOR called ======");
        }

        [Function("ddnsupdate")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "nic/update")] HttpRequest req)
        {
            var invocationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var invocationNumber = System.Threading.Interlocked.Increment(ref _invocationCount);
            
            _logger.LogInformation("====================================================");
            _logger.LogInformation($"[{invocationId}] DDNS UPDATE FUNCTION INVOKED");
            _logger.LogInformation($"[{invocationId}] Invocation Number: {invocationNumber}");
            _logger.LogInformation($"[{invocationId}] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
            
            try
            {
                // Parse Basic Auth header (DynDNS2 protocol)
                var authHeader = req.Headers["Authorization"].FirstOrDefault();
                _logger.LogInformation($"[{invocationId}] Authorization header present: {!string.IsNullOrEmpty(authHeader)}");
                
                string? username = null;
                string? password = null;
                
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Basic "))
                {
                    try
                    {
                        var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                        var decodedBytes = Convert.FromBase64String(encodedCredentials);
                        var credentials = Encoding.UTF8.GetString(decodedBytes);
                        var parts = credentials.Split(':');
                        
                        if (parts.Length >= 2)
                        {
                            username = parts[0];
                            password = string.Join(":", parts.Skip(1)); // In case password contains ':'
                        }
                        
                        _logger.LogInformation($"[{invocationId}] Basic Auth decoded - Username: {username}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[{invocationId}] Error decoding Basic Auth: {ex.Message}");
                    }
                }
                
                // Get query parameters (DynDNS2 protocol)
                var hostname = req.Query["hostname"].FirstOrDefault();
                var myip = req.Query["myip"].FirstOrDefault();
                
                _logger.LogInformation($"[{invocationId}] Query Parameters:");
                _logger.LogInformation($"[{invocationId}]   hostname = {hostname ?? "null"}");
                _logger.LogInformation($"[{invocationId}]   myip = {myip ?? "null"}");
                
                // Check if provided IP is private/internal - if so, treat as "auto"
                bool needAutoDetect = string.IsNullOrEmpty(myip) || 
                                      myip.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                      IsPrivateOrInternalIP(myip);
                
                if (needAutoDetect)
                {
                    if (!string.IsNullOrEmpty(myip) && IsPrivateOrInternalIP(myip))
                    {
                        _logger.LogWarning($"[{invocationId}] Provided IP {myip} is private/internal, switching to auto-detection");
                    }
                }
                
                // Auto-detect IP if myip is not provided, is "auto", or is a private IP
                if (needAutoDetect)
                {
                    _logger.LogInformation($"[{invocationId}] ====== IP AUTO-DETECTION START ======");
                    
                    // Log ALL headers for debugging
                    _logger.LogInformation($"[{invocationId}] All request headers:");
                    foreach (var header in req.Headers)
                    {
                        var headerValues = req.Headers[header.Key];
                        if (headerValues.Any())
                        {
                            var values = string.Join(", ", headerValues);
                            _logger.LogInformation($"[{invocationId}]   {header.Key}: {values}");
                        }
                    }

                    // Try to get real client IP from various sources
                    var forwardedFor = req.Headers["X-Forwarded-For"].FirstOrDefault();
                    var azureClientIp = req.Headers["X-Azure-ClientIP"].FirstOrDefault();
                    var realIp = req.Headers["X-Real-IP"].FirstOrDefault();
                    var originalFor = req.Headers["X-Original-For"].FirstOrDefault();
                    var clientIp = req.HttpContext?.Connection?.RemoteIpAddress?.ToString();

                    _logger.LogInformation($"[{invocationId}] Header values after middleware fix:");
                    _logger.LogInformation($"[{invocationId}]   X-Forwarded-For = {forwardedFor ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   X-Azure-ClientIP = {azureClientIp ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   X-Real-IP = {realIp ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   X-Original-For = {originalFor ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   HttpContext.RemoteIpAddress = {clientIp ?? "null"}");

                    // Try headers in order of preference (Azure-specific first)
                    myip = azureClientIp?.Trim() ??
                           forwardedFor?.Split(',')[0].Trim() ??
                           realIp?.Trim() ??
                           originalFor?.Split(',')[0].Trim() ??
                           clientIp;

                    // Clean up the IP address
                    if (!string.IsNullOrEmpty(myip))
                    {
                        _logger.LogInformation($"[{invocationId}] Raw IP before cleanup: {myip}");
                        
                        // Remove IPv6 brackets and port if present: [::1]:41380 -> ::1
                        if (myip.StartsWith("[") && myip.Contains("]:"))
                        {
                            var endBracket = myip.IndexOf("]:");
                            myip = myip.Substring(1, endBracket - 1);
                        }
                        // Remove brackets from IPv6: [::1] -> ::1
                        else if (myip.StartsWith("[") && myip.EndsWith("]"))
                        {
                            myip = myip.Substring(1, myip.Length - 2);
                        }
                        // Remove port from IPv4: 1.2.3.4:80 -> 1.2.3.4
                        else if (myip.Contains(':') && myip.Split('.').Length == 4)
                        {
                            myip = myip.Substring(0, myip.LastIndexOf(':'));
                        }

                        _logger.LogInformation($"[{invocationId}] IP after cleanup: {myip}");

                        // Reject loopback and private addresses
                        if (myip == "::1" || myip == "127.0.0.1" || myip.StartsWith("127.") || 
                            myip == "localhost" || myip.StartsWith("10.") || myip.StartsWith("192.168.") ||
                            (myip.StartsWith("172.") && int.Parse(myip.Split('.')[1]) >= 16 && int.Parse(myip.Split('.')[1]) <= 31))
                        {
                            _logger.LogWarning($"[{invocationId}] Detected private/loopback address: {myip}, cannot use for DDNS");
                            myip = null;
                        }
                    }

                    _logger.LogInformation($"[{invocationId}] ====== IP AUTO-DETECTION RESULT: {myip ?? "FAILED"} ======");
                }
                
                // Validate required parameters
                if (string.IsNullOrEmpty(authHeader))
                {
                    _logger.LogWarning($"[{invocationId}] No authorization provided");
                    return CreateDynDnsResponse("badauth", invocationId);
                }
                
                if (string.IsNullOrEmpty(hostname))
                {
                    _logger.LogWarning($"[{invocationId}] No hostname provided");
                    return CreateDynDnsResponse("notfqdn", invocationId);
                }
                
                if (string.IsNullOrEmpty(myip))
                {
                    _logger.LogWarning($"[{invocationId}] Failed to determine IP address - auto-detection found only loopback or no valid IP");
                    _logger.LogWarning($"[{invocationId}] Please specify IP explicitly with myip parameter instead of using 'auto'");
                    return CreateDynDnsResponse("911", invocationId);
                }
                
                // Validate IP address format
                if (!IPAddress.TryParse(myip, out var ipAddress))
                {
                    _logger.LogWarning($"[{invocationId}] Invalid IP address format: {myip}");
                    return CreateDynDnsResponse("911", invocationId);
                }
                
                // TODO: Validate API key against hostname ownership
                // For now, we'll use environment variables for basic auth
                var expectedUsername = Environment.GetEnvironmentVariable("DDNS_USERNAME") ?? "admin";
                var expectedPassword = Environment.GetEnvironmentVariable("DDNS_PASSWORD") ?? "password";
                
                if (username != expectedUsername || password != expectedPassword)
                {
                    _logger.LogWarning($"[{invocationId}] Invalid credentials for user: {username}");
                    return CreateDynDnsResponse("badauth", invocationId);
                }
                
                // Parse hostname to extract subdomain and zone
                var dnsZoneName = Environment.GetEnvironmentVariable("DNS_ZONE_NAME") ?? "title.dev";
                var ddnsSubdomain = Environment.GetEnvironmentVariable("DDNS_SUBDOMAIN") ?? "ddns";
                
                // Support multiple patterns:
                // 1. *.ddns.title.dev (e.g., wan1-mro-tru.ddns.title.dev)
                // 2. *.*.ddns.title.dev (e.g., wan1.mro.tru.ddns.title.dev)
                // 3. *.*.*.ddns.title.dev (e.g., wan1.stg.tru.ddns.title.dev)
                // 4. Direct under zone (e.g., wan1.ftl.cts.title.dev)
                
                string recordName = string.Empty;
                bool isValidHostname = false;
                
                // Check if it ends with .ddns.title.dev pattern
                var ddnsPattern = $".{ddnsSubdomain}.{dnsZoneName}";
                if (hostname.EndsWith(ddnsPattern))
                {
                    // Extract everything before .ddns.title.dev
                    var prefix = hostname.Substring(0, hostname.Length - ddnsPattern.Length);
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        // Record name in DNS is prefix.ddns (e.g., wan1.mro.tru.ddns)
                        recordName = $"{prefix}.{ddnsSubdomain}";
                        isValidHostname = true;
                        _logger.LogInformation($"[{invocationId}] DDNS pattern matched: {prefix} under {ddnsSubdomain}.{dnsZoneName}");
                    }
                }
                // Check if it's directly under the zone (e.g., wan1.ftl.cts.title.dev)
                else if (hostname.EndsWith($".{dnsZoneName}"))
                {
                    // Extract everything before .title.dev
                    var prefix = hostname.Substring(0, hostname.Length - $".{dnsZoneName}".Length);
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        // Record name in DNS is just the prefix (e.g., wan1.ftl.cts)
                        recordName = prefix;
                        isValidHostname = true;
                        _logger.LogInformation($"[{invocationId}] Direct zone pattern matched: {prefix} under {dnsZoneName}");
                    }
                }
                else
                {
                    _logger.LogWarning($"[{invocationId}] Invalid hostname format: {hostname}");
                    _logger.LogWarning($"[{invocationId}] Expected: *.{ddnsSubdomain}.{dnsZoneName} or *.{dnsZoneName}");
                    return CreateDynDnsResponse("nohost", invocationId);
                }
                
                if (!isValidHostname)
                {
                    _logger.LogWarning($"[{invocationId}] Empty or invalid prefix in hostname: {hostname}");
                    return CreateDynDnsResponse("nohost", invocationId);
                }
                
                // Validate DNS name (alphanumeric, dots, and hyphens only)
                if (!System.Text.RegularExpressions.Regex.IsMatch(recordName, @"^[a-zA-Z0-9\.\-]+$"))
                {
                    _logger.LogWarning($"[{invocationId}] Invalid characters in DNS record name: {recordName}");
                    return CreateDynDnsResponse("nohost", invocationId);
                }
                
                _logger.LogInformation($"[{invocationId}] DNS record name to update: {recordName}");
                
                // Update DNS record
                _logger.LogInformation($"[{invocationId}] Attempting DNS update:");
                _logger.LogInformation($"[{invocationId}]   Zone: {dnsZoneName}");
                _logger.LogInformation($"[{invocationId}]   Record: {recordName}");
                _logger.LogInformation($"[{invocationId}]   Type: A");
                _logger.LogInformation($"[{invocationId}]   IP: {myip}");
                
                var updateResult = await UpdateDnsRecord(invocationId, dnsZoneName, recordName, myip);
                
                _logger.LogInformation($"[{invocationId}] DNS update result: {updateResult}");
                _logger.LogInformation($"[{invocationId}] DDNS UPDATE FUNCTION COMPLETED");
                _logger.LogInformation("====================================================");
                
                Console.WriteLine($"[CONSOLE] DdnsUpdate {invocationId} returned: {updateResult}");
                
                return CreateDynDnsResponse(updateResult, invocationId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{invocationId}] Unhandled exception: {ex}");
                _logger.LogError($"[{invocationId}] Stack trace: {ex.StackTrace}");
                Console.WriteLine($"[CONSOLE] DdnsUpdate {invocationId} ERROR: {ex.Message}");
                
                return CreateDynDnsResponse("911", invocationId);
            }
        }
        
        private async Task<string> UpdateDnsRecord(string invocationId, string zoneName, string recordName, string ipAddress)
        {
            try
            {
                _logger.LogInformation($"[{invocationId}] Initializing Azure Resource Manager client");
                
                // Get configuration from environment variables
                var subscriptionId = Environment.GetEnvironmentVariable("DNS_SUBSCRIPTION_ID");
                var resourceGroupName = Environment.GetEnvironmentVariable("DNS_RESOURCE_GROUP") ?? "domains-dns";
                
                if (string.IsNullOrEmpty(subscriptionId))
                {
                    _logger.LogWarning($"[{invocationId}] DNS_SUBSCRIPTION_ID not configured");
                    _logger.LogWarning($"[{invocationId}] Simulating DNS update for development");
                    return "good"; // Simulate success for local development
                }
                
                _logger.LogInformation($"[{invocationId}] Target subscription: {subscriptionId}");
                _logger.LogInformation($"[{invocationId}] Target resource group: {resourceGroupName}");
                
                // Use DefaultAzureCredential with ManagedIdentityClientId for user-assigned identity
                var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
                _logger.LogInformation($"[{invocationId}] AZURE_CLIENT_ID from environment: {clientId ?? "not set"}");
                
                TokenCredential credential;
                if (!string.IsNullOrEmpty(clientId))
                {
                    _logger.LogInformation($"[{invocationId}] Using DefaultAzureCredential with ManagedIdentityClientId: {clientId}");
                    credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ManagedIdentityClientId = clientId
                    });
                }
                else
                {
                    _logger.LogInformation($"[{invocationId}] Using DefaultAzureCredential without specific client ID");
                    credential = new DefaultAzureCredential();
                }
                var armClient = new ArmClient(credential);
                
                // Get the DNS zone
                var subscription = armClient.GetSubscriptionResource(new Azure.Core.ResourceIdentifier($"/subscriptions/{subscriptionId}"));
                var resourceGroup = await subscription.GetResourceGroupAsync(resourceGroupName);
                var dnsZone = await resourceGroup.Value.GetDnsZoneAsync(zoneName);
                
                _logger.LogInformation($"[{invocationId}] DNS zone found: {dnsZone.Value.Data.Name}");
                
                // Get or create the A record
                var recordSets = dnsZone.Value.GetDnsARecords();
                DnsARecordResource? aRecord;
                
                try
                {
                    aRecord = await recordSets.GetAsync(recordName);
                    _logger.LogInformation($"[{invocationId}] Existing A record found");
                    
                    // Check if IP is already the same
                    var currentIp = aRecord.Data.DnsARecords.FirstOrDefault()?.IPv4Address?.ToString();
                    if (currentIp == ipAddress)
                    {
                        _logger.LogInformation($"[{invocationId}] IP unchanged: {ipAddress}");
                        return "nochg";
                    }
                }
                catch
                {
                    _logger.LogInformation($"[{invocationId}] A record not found, will create new");
                    aRecord = null;
                }
                
                // Create or update the record
                var recordData = new DnsARecordData
                {
                    TtlInSeconds = 60, // 1 minute TTL for dynamic records
                    DnsARecords = { new DnsARecordInfo { IPv4Address = IPAddress.Parse(ipAddress) } }
                };
                
                if (aRecord == null)
                {
                    _logger.LogInformation($"[{invocationId}] Creating new A record");
                    await recordSets.CreateOrUpdateAsync(
                        Azure.WaitUntil.Completed,
                        recordName,
                        recordData
                    );
                }
                else
                {
                    _logger.LogInformation($"[{invocationId}] Updating existing A record");
                    aRecord.Data.DnsARecords.Clear();
                    aRecord.Data.DnsARecords.Add(new DnsARecordInfo { IPv4Address = IPAddress.Parse(ipAddress) });
                    aRecord.Data.TtlInSeconds = 60;
                    await aRecord.UpdateAsync(aRecord.Data);
                }
                
                _logger.LogInformation($"[{invocationId}] DNS record updated successfully");
                return "good";
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{invocationId}] DNS update failed: {ex.Message}");
                _logger.LogError($"[{invocationId}] Stack trace: {ex.StackTrace}");
                return "911";
            }
        }
        
        private IActionResult CreateDynDnsResponse(string response, string invocationId)
        {
            _logger.LogInformation($"[{invocationId}] Returning DynDNS2 response: {response}");
            
            // Return plain text response as per DynDNS2 protocol
            return new ContentResult
            {
                Content = response,
                ContentType = "text/plain",
                StatusCode = response == "badauth" ? 401 : 200
            };
        }
        
        private bool IsPrivateOrInternalIP(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;
                
            // Check for loopback
            if (IPAddress.IsLoopback(ip))
                return true;
                
            // Check for IPv4 private ranges
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10)
                    return true;
                    
                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;
                    
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;
                    
                // 127.0.0.0/8 (loopback)
                if (bytes[0] == 127)
                    return true;
                    
                // 169.254.0.0/16 (link-local)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;
            }
            
            return false;
        }
    }
}