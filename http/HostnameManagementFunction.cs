using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;

namespace Company.Function
{
    public class HostnameManagementFunction
    {
        private readonly ILogger<HostnameManagementFunction> _logger;
        private readonly TableStorageService _tableStorage;
        private readonly ApiKeyService _apiKeyService;
        private readonly TemplateService _templateService;

        public HostnameManagementFunction(
            ILogger<HostnameManagementFunction> logger,
            TableStorageService tableStorage,
            ApiKeyService apiKeyService,
            TemplateService templateService)
        {
            _logger = logger;
            _tableStorage = tableStorage;
            _apiKeyService = apiKeyService;
            _templateService = templateService;
        }


        [Function("ManageHostname")]
        public async Task<HttpResponseData> ManageHostname(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/{hostname}")] HttpRequestData req,
            string hostname)
        {
            try
            {
                _logger.LogInformation($"[AUDIT-MANAGE] Management request for hostname: {hostname}");
                _logger.LogInformation($"[AUDIT-REQUEST] Method: {req.Method}, URL: {req.Url}, Headers Count: {req.Headers.Count()}");

                // Get user from EasyAuth header
                var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);

                if (string.IsNullOrEmpty(userId))
                {
                    // No authentication, redirect to Azure AD login via EasyAuth
                    _logger.LogInformation($"[AUDIT-MANAGE] No authentication for {hostname}, redirecting to EasyAuth login");
                    var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                    var encodedRedirect = Uri.EscapeDataString($"/api/manage/{hostname}");
                    var redirectUrl = $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}";
                    _logger.LogInformation($"[AUDIT-REDIRECT] Redirecting to: {redirectUrl}");
                    response.Headers.Add("Location", redirectUrl);
                    return response;
                }

                _logger.LogInformation($"[AUDIT-MANAGE] Authenticated user {userEmail} (ID: {userId}) accessing hostname {hostname}");

                // Auto-redirect to full FQDN if not already provided
                var ddnsSubdomain = Environment.GetEnvironmentVariable("DDNS_SUBDOMAIN");
                var dnsZoneName = Environment.GetEnvironmentVariable("DNS_ZONE_NAME");
                
                // Only redirect if we have the configuration and hostname is not already a FQDN
                if (!string.IsNullOrEmpty(ddnsSubdomain) && !string.IsNullOrEmpty(dnsZoneName) && !hostname.Contains("."))
                {
                    // Build the full FQDN: {hostname}.{ddnsSubdomain}.{dnsZoneName}
                    var fullHostname = $"{hostname}.{ddnsSubdomain}.{dnsZoneName}";
                    _logger.LogInformation($"[AUDIT-MANAGE] Redirecting short hostname '{hostname}' to full FQDN '{fullHostname}'");
                    
                    var redirectResponse = req.CreateResponse(System.Net.HttpStatusCode.PermanentRedirect);
                    redirectResponse.Headers.Add("Location", $"/api/manage/{fullHostname}");
                    return redirectResponse;
                }

            // Check if hostname is already claimed
            var existingOwnerPrincipalId = await _tableStorage.GetHostnameOwnerAsync(hostname);
            
            if (existingOwnerPrincipalId == null)
            {
                // Get the user's email from the header if not in claims
                if (string.IsNullOrEmpty(userEmail))
                {
                    req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-NAME", out var nameVals);
                    userEmail = nameVals?.FirstOrDefault() ?? "Unknown";
                    _logger.LogInformation($"[AUDIT-CLAIM] Got email from X-MS-CLIENT-PRINCIPAL-NAME: {userEmail}");
                }
                
                // Hostname not claimed yet, claim it for this user
                _logger.LogInformation($"[AUDIT-CLAIM] Hostname {hostname} not claimed, attempting to claim for user {userId} ({userEmail})");
                var claimed = await _tableStorage.ClaimHostnameAsync(hostname, userId, userEmail);
                
                if (!claimed)
                {
                    _logger.LogError($"[AUDIT-CLAIM-FAIL] Failed to claim hostname {hostname} for user {userId}");
                    var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                    await errorResponse.WriteStringAsync("Failed to claim hostname");
                    return errorResponse;
                }
                
                _logger.LogInformation($"[AUDIT-CLAIM-SUCCESS] Hostname {hostname} successfully claimed by user {userId}");
                
                // Generate initial API key
                _logger.LogInformation($"[AUDIT-API-KEY] Generating initial API key for hostname {hostname}");
                var apiKey = await _apiKeyService.GenerateApiKeyAsync(hostname, userId, userEmail);
                _logger.LogInformation($"[AUDIT-API-KEY-SUCCESS] Generated API key for new hostname {hostname}, key starts with: {apiKey?.Substring(0, 8)}...");
                
                // Return management page with new API key
                _logger.LogInformation($"[AUDIT-MANAGE] Returning management page with new API key for {hostname}");
                return await CreateManagementPage(req, hostname, userEmail, apiKey, true);
            }
            else if (existingOwnerPrincipalId != userId)
            {
                // Hostname claimed by another user
                _logger.LogWarning($"[AUDIT-MANAGE] User {userId} attempted to access hostname {hostname} owned by {existingOwnerPrincipalId}");
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync($"Hostname {hostname} is already claimed by another user");
                return forbiddenResponse;
            }
            else
            {
                // User owns this hostname, show management page
                _logger.LogInformation($"[AUDIT-MANAGE] Owner {userId} accessing their hostname {hostname}");
                
                // Get all API keys for this hostname
                var apiKeys = await _tableStorage.GetApiKeysForHostnameAsync(hostname);
                _logger.LogInformation($"[AUDIT-MANAGE] Found {apiKeys.Count} API keys for hostname {hostname}");
                
                // Get update history
                var updateHistory = await _tableStorage.GetUpdateHistoryAsync(hostname, 50);
                
                // Get the user's email from the header if not in claims
                if (string.IsNullOrEmpty(userEmail))
                {
                    req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-NAME", out var nameVals);
                    userEmail = nameVals?.FirstOrDefault() ?? "Unknown";
                    _logger.LogInformation($"[AUDIT-MANAGE] Got email from X-MS-CLIENT-PRINCIPAL-NAME: {userEmail}");
                }
                
                
                // Return management page
                _logger.LogInformation($"[AUDIT-MANAGE] Returning management page for owner {userId} of hostname {hostname}");
                return await CreateManagementPageWithKeys(req, hostname, userEmail, apiKeys, updateHistory);
            }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-ERROR] Failed to process management request for hostname {hostname}");
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Internal server error: {ex.Message}");
                return errorResponse;
            }
        }

        private async Task<HttpResponseData> CreateManagementPage(
            HttpRequestData req, 
            string hostname, 
            string? userEmail, 
            string? apiKey,
            bool isNew,
            IEnumerable<Azure.Data.Tables.TableEntity>? updateHistory = null)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");

            // Get current DNS record if it exists
            string? currentIp = null;
            DateTime? lastUpdated = null;
            
            if (updateHistory != null && updateHistory.Any())
            {
                var lastUpdate = updateHistory.OrderByDescending(h => h.GetDateTimeOffset("Timestamp")).FirstOrDefault();
                if (lastUpdate != null)
                {
                    currentIp = lastUpdate.GetString("IpAddress");
                    lastUpdated = lastUpdate.GetDateTimeOffset("Timestamp")?.DateTime;
                }
            }

            var model = new
            {
                IsOwner = true,
                Hostname = hostname,
                SimpleHostname = hostname.Split('.')[0],
                OwnerEmail = userEmail ?? "Unknown",
                CurrentIp = currentIp,
                LastUpdated = lastUpdated?.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TotalUpdates = updateHistory?.Count() ?? 0,
                BaseUrl = $"https://{req.Url.Host}",
                ApiKeys = apiKey != null ? new[] { new { 
                    KeyHashShort = "NEW KEY", 
                    Created = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastUsed = (string?)null
                }} : Array.Empty<object>(),
                UpdateHistory = updateHistory?.Select(h => new
                {
                    Timestamp = (h.GetDateTimeOffset("Timestamp") ?? DateTimeOffset.MinValue).ToString("yyyy-MM-dd HH:mm:ss"),
                    IpAddress = h.GetString("IpAddress") ?? "N/A",
                    Status = (h.GetBoolean("Success") ?? false) ? "✅ Success" : "❌ Failed"
                }).Take(10).ToArray() ?? Array.Empty<object>()
            };

            var html = await _templateService.RenderAdvancedTemplateAsync("hostname-management.html", model);
            
            // If we have a new API key, inject it into the page with a special display
            if (apiKey != null)
            {
                html = html.Replace("<!-- API_KEY_PLACEHOLDER -->", $@"
                    <div style='background: #c6f6d5; padding: 20px; border-radius: 8px; margin: 20px 0; border: 2px solid #48bb78;'>
                        <h3 style='color: #22543d; margin-top: 0;'>🎉 New API Key Generated!</h3>
                        <div style='background: #1e293b; color: #10b981; padding: 15px; border-radius: 6px; font-family: monospace; word-break: break-all; font-size: 16px;'>
                            {apiKey}
                        </div>
                        <p style='color: #22543d; font-weight: bold; margin-bottom: 0;'>
                            ⚠️ Save this key now! For security, we don't store the full key and it cannot be retrieved again.
                        </p>
                    </div>");
            }
            
            await response.WriteStringAsync(html);
            return response;
        }

        private async Task<HttpResponseData> CreateManagementPageWithKeys(
            HttpRequestData req,
            string hostname,
            string? userEmail,
            List<Azure.Data.Tables.TableEntity> apiKeys,
            IEnumerable<Azure.Data.Tables.TableEntity>? updateHistory = null)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");

            // Get current DNS record if it exists
            string? currentIp = null;
            DateTime? lastUpdated = null;
            
            if (updateHistory != null && updateHistory.Any())
            {
                var lastUpdate = updateHistory.OrderByDescending(h => h.GetDateTimeOffset("Timestamp")).FirstOrDefault();
                if (lastUpdate != null)
                {
                    currentIp = lastUpdate.GetString("IpAddress");
                    lastUpdated = lastUpdate.GetDateTimeOffset("Timestamp")?.DateTime;
                }
            }

            // Sort API keys by last used
            var sortedKeys = apiKeys?.OrderByDescending(k => 
            {
                var lastUsed = k.GetDateTimeOffset("LastUsed");
                if (lastUsed.HasValue) return lastUsed.Value;
                return k.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue;
            }) ?? Enumerable.Empty<TableEntity>();

            var model = new
            {
                IsOwner = true,
                Hostname = hostname,
                SimpleHostname = hostname.Split('.')[0],
                OwnerEmail = userEmail ?? "Unknown",
                CurrentIp = currentIp,
                LastUpdated = lastUpdated?.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TotalUpdates = updateHistory?.Count() ?? 0,
                BaseUrl = $"https://{req.Url.Host}",
                ApiKeys = sortedKeys.Select(key => new
                {
                    KeyHashShort = (key.PartitionKey ?? "unknown").Substring(0, Math.Min(12, (key.PartitionKey ?? "").Length)),
                    Created = (key.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue).ToString("yyyy-MM-dd HH:mm:ss"),
                    LastUsed = key.GetDateTimeOffset("LastUsed")?.ToString("yyyy-MM-dd HH:mm:ss"),
                    IsActive = key.GetBoolean("IsActive") ?? false
                }).ToArray(),
                UpdateHistory = updateHistory?.Select(h => new
                {
                    Timestamp = (h.GetDateTimeOffset("Timestamp") ?? DateTimeOffset.MinValue).ToString("yyyy-MM-dd HH:mm:ss"),
                    IpAddress = h.GetString("IpAddress") ?? "N/A",
                    Status = (h.GetBoolean("Success") ?? false) ? "✅ Success" : "❌ Failed"
                }).Take(10).ToArray() ?? Array.Empty<object>()
            };

            var html = await _templateService.RenderAdvancedTemplateAsync("hostname-management.html", model);
            await response.WriteStringAsync(html);
            return response;
        }
    }
}
