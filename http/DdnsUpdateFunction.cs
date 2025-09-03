using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
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
                
                // Auto-detect IP if myip is not provided or is "auto"
                if (string.IsNullOrEmpty(myip) || myip.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to get real client IP
                    var clientIp = req.HttpContext.Connection.RemoteIpAddress?.ToString();
                    var forwardedFor = req.Headers["X-Forwarded-For"].FirstOrDefault();
                    var originalFor = req.Headers["X-Original-For"].FirstOrDefault();
                    
                    _logger.LogInformation($"[{invocationId}] Auto-detecting IP:");
                    _logger.LogInformation($"[{invocationId}]   RemoteIpAddress = {clientIp ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   X-Forwarded-For = {forwardedFor ?? "null"}");
                    _logger.LogInformation($"[{invocationId}]   X-Original-For = {originalFor ?? "null"}");
                    
                    // Use X-Forwarded-For first, then X-Original-For, then RemoteIpAddress
                    myip = forwardedFor?.Split(',')[0].Trim() ?? 
                           originalFor?.Split(',')[0].Trim() ?? 
                           clientIp;
                    
                    // Remove port if present
                    if (!string.IsNullOrEmpty(myip) && myip.Contains(':'))
                    {
                        myip = myip.Substring(0, myip.LastIndexOf(':'));
                    }
                    
                    _logger.LogInformation($"[{invocationId}]   Detected IP = {myip ?? "failed to detect"}");
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
                    _logger.LogWarning($"[{invocationId}] Failed to determine IP address");
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
                
                // Expected format: device.ddns.title.dev
                if (!hostname.EndsWith($".{ddnsSubdomain}.{dnsZoneName}"))
                {
                    _logger.LogWarning($"[{invocationId}] Invalid hostname format: {hostname}");
                    _logger.LogWarning($"[{invocationId}] Expected: *.{ddnsSubdomain}.{dnsZoneName}");
                    return CreateDynDnsResponse("nohost", invocationId);
                }
                
                // Extract the device name (first part of hostname)
                var deviceName = hostname.Replace($".{ddnsSubdomain}.{dnsZoneName}", "");
                _logger.LogInformation($"[{invocationId}] Device name extracted: {deviceName}");
                
                // Update DNS record
                _logger.LogInformation($"[{invocationId}] Attempting DNS update:");
                _logger.LogInformation($"[{invocationId}]   Zone: {dnsZoneName}");
                _logger.LogInformation($"[{invocationId}]   Record: {deviceName}.{ddnsSubdomain}");
                _logger.LogInformation($"[{invocationId}]   Type: A");
                _logger.LogInformation($"[{invocationId}]   IP: {myip}");
                
                var updateResult = await UpdateDnsRecord(invocationId, dnsZoneName, $"{deviceName}.{ddnsSubdomain}", myip);
                
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
                
                // Use managed identity in production, DefaultAzureCredential for local dev
                var credential = new DefaultAzureCredential();
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
    }
}