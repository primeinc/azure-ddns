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

        public HostnameManagementFunction(
            ILogger<HostnameManagementFunction> logger,
            TableStorageService tableStorage,
            ApiKeyService apiKeyService)
        {
            _logger = logger;
            _tableStorage = tableStorage;
            _apiKeyService = apiKeyService;
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

            var apiKeyDisplay = apiKey != null 
                ? $@"<div class='api-key-section'>
                    <h2>Your API Key</h2>
                    <div class='api-key'>{apiKey}</div>
                    <p class='warning'>⚠️ Save this key securely. It won't be shown again.</p>
                   </div>"
                : @"<div class='info-box'>
                    <p>Your API key is already configured. If you need a new one, contact support.</p>
                   </div>";

            var historyHtml = "";
            if (updateHistory != null && updateHistory.Any())
            {
                var rows = string.Join("\n", updateHistory.Select(h => 
                {
                    var timestamp = h.GetDateTimeOffset("Timestamp") ?? DateTimeOffset.MinValue;
                    var ipAddress = h.GetString("IpAddress") ?? "N/A";
                    var success = h.GetBoolean("Success") ?? false;
                    var status = success ? "Success" : "Failed";
                    return $"<tr><td>{timestamp:yyyy-MM-dd HH:mm:ss}</td><td>{ipAddress}</td><td>{status}</td></tr>";
                }));
                historyHtml = $@"
                <h2>Recent Updates</h2>
                <table class='history'>
                    <tr><th>Timestamp</th><th>IP Address</th><th>Status</th></tr>
                    {rows}
                </table>";
            }

            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>DDNS Management - {hostname}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px; background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); min-height: 100vh; }}
        .container {{ background: white; padding: 40px; border-radius: 12px; box-shadow: 0 20px 40px rgba(0,0,0,0.1); max-width: 900px; margin: 0 auto; }}
        h1 {{ color: #333; margin-bottom: 10px; }}
        .status {{ display: inline-block; padding: 4px 12px; border-radius: 20px; background: #10b981; color: white; font-size: 14px; margin-left: 10px; }}
        .info-box {{ background: #f0f9ff; border-left: 4px solid #3b82f6; padding: 20px; margin: 20px 0; border-radius: 4px; }}
        .api-key {{ font-family: 'Courier New', monospace; background: #1e293b; color: #10b981; padding: 20px; border-radius: 8px; word-break: break-all; font-size: 16px; margin: 10px 0; }}
        .warning {{ color: #ef4444; font-weight: bold; }}
        .command {{ background: #1e293b; color: #f1f5f9; padding: 20px; border-radius: 8px; overflow-x: auto; margin: 10px 0; font-family: 'Courier New', monospace; }}
        h2 {{ color: #475569; margin-top: 30px; }}
        .history {{ width: 100%; border-collapse: collapse; margin-top: 10px; }}
        .history th {{ background: #f1f5f9; padding: 10px; text-align: left; }}
        .history td {{ padding: 10px; border-bottom: 1px solid #e5e7eb; }}
        .user-info {{ color: #64748b; margin-bottom: 20px; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>{hostname} <span class='status'>Active</span></h1>
        <div class='user-info'>Managed by: <span class='user-email' onclick='triggerEasterEgg()'>{userEmail ?? "Unknown"}</span></div>
        
        {apiKeyDisplay}
        
        <h2>Update Your Dynamic IP</h2>
        
        <h3>Using curl:</h3>
        <div class='command'>curl -u ""{hostname.Split('.')[0]}:{apiKey}"" ""https://{req.Url.Host}/api/nic/update?hostname={hostname}&myip=auto""</div>
        
        <h3>Using wget:</h3>
        <div class='command'>wget --auth-no-challenge --user=""{hostname.Split('.')[0]}"" --password=""{apiKey}"" ""https://{req.Url.Host}/api/nic/update?hostname={hostname}&myip=auto""</div>
        
        <h3>Router Configuration (DynDNS2 Protocol):</h3>
        <div class='info-box'>
            <strong>Service Type:</strong> DynDNS or Custom<br>
            <strong>Server/URL:</strong> {req.Url.Host}<br>
            <strong>Username:</strong> {hostname.Split('.')[0]}<br>
            <strong>Password:</strong> {apiKey}<br>
            <strong>Hostname:</strong> {hostname}
        </div>
        
        {historyHtml}
        
        <p style='margin-top: 40px; padding-top: 20px; border-top: 1px solid #e5e7eb; color: #64748b; font-size: 14px;'>
            <a href='/.auth/logout'>Sign out</a> | 
            Generated by Azure DDNS Service
        </p>
    </div>
</body>
</html>";
            
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

            // Note: We only store hashes, not actual keys - for security
            // Users must save their API key when it's generated as we can't show it again

            // Build API keys table with management buttons
            var apiKeysHtml = "";
            if (apiKeys != null && apiKeys.Count > 0)
            {
                // Sort API keys by LastUsed (most recent first), then by CreatedAt for never-used keys
                var sortedKeys = apiKeys.OrderByDescending(k => 
                {
                    var lastUsed = k.GetDateTimeOffset("LastUsed");
                    if (lastUsed.HasValue) return lastUsed.Value;
                    return k.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue;
                }).ToList();
                
                var rows = string.Join("\n", sortedKeys.Select((key, index) =>
                {
                    var keyId = key.PartitionKey ?? "unknown";
                    var createdAt = key.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue;
                    var createdBy = key.GetString("CreatedByEmail") ?? "Unknown";
                    var lastUsed = key.GetDateTimeOffset("LastUsed");
                    var lastIp = key.GetString("LastUsedFromIp") ?? "Never";
                    var useCount = key.GetInt32("UseCount") ?? 0;
                    var isActive = key.GetBoolean("IsActive") ?? false;
                    
                    var statusBadge = isActive 
                        ? "<span class='badge badge-success'>Active</span>" 
                        : "<span class='badge badge-danger'>Revoked</span>";
                    var lastUsedStr = lastUsed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never";
                    
                    // Display key hash (we don't store actual keys for security)
                    // First key in sorted list (most recently used) gets hot pink styling
                    var isHottest = index == 0 && lastUsed.HasValue;
                    var keyDisplay = isHottest 
                        ? $@"
                        <div class='key-display'>
                            <code class='key-hash hottest-key' title='SHA256 hash of API key - MOST RECENTLY USED 🔥'>{keyId.Substring(0, Math.Min(12, keyId.Length))}...</code>
                            <span class='text-muted small'>🔥 (latest)</span>
                        </div>"
                        : $@"
                        <div class='key-display'>
                            <code class='key-hash' title='SHA256 hash of API key'>{keyId.Substring(0, Math.Min(12, keyId.Length))}...</code>
                            <span class='text-muted small'>(hash only)</span>
                        </div>";
                    
                    var actions = isActive 
                        ? $"<button onclick='revokeKey(\"{keyId}\")' class='btn-danger btn-sm'>Revoke</button>"
                        : "<span class='text-muted'>Revoked</span>";
                    
                    return $@"<tr>
                        <td>{keyDisplay}</td>
                        <td>{createdBy}</td>
                        <td>{createdAt:yyyy-MM-dd HH:mm:ss}</td>
                        <td>{lastUsedStr}</td>
                        <td>{lastIp}</td>
                        <td>{useCount}</td>
                        <td>{statusBadge}</td>
                        <td>{actions}</td>
                    </tr>";
                }));
                
                apiKeysHtml = $@"
                <div class='section'>
                    <div class='section-header'>
                        <h2>API Keys Management</h2>
                        <div class='btn-group'>
                            <button onclick='generateNewKey()' class='btn btn-primary'>🔑 Generate New Key</button>
                            <button onclick='revokeAllKeys()' class='btn btn-danger'>⛔ Revoke All Keys</button>
                        </div>
                    </div>
                    <table class='table'>
                        <thead>
                            <tr>
                                <th style='width: 300px'>API Key</th>
                                <th>Created By</th>
                                <th>Created At</th>
                                <th>Last Used</th>
                                <th>Last IP</th>
                                <th>Uses</th>
                                <th>Status</th>
                                <th>Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            {rows}
                        </tbody>
                    </table>
                </div>";
            }
            else
            {
                apiKeysHtml = @"
                <div class='section'>
                    <h2>API Keys Management</h2>
                    <div class='alert alert-info'>
                        <p>No API keys found. Generate one to start using DDNS updates.</p>
                        <button onclick='generateNewKey()' class='btn btn-primary'>🔑 Generate Your First API Key</button>
                    </div>
                </div>";
            }

            // Build update history
            var historyHtml = "";
            if (updateHistory != null && updateHistory.Any())
            {
                var rows = string.Join("\n", updateHistory.Select(h =>
                {
                    var timestamp = h.GetDateTimeOffset("Timestamp") ?? DateTimeOffset.MinValue;
                    var ipAddress = h.GetString("IpAddress") ?? "N/A";
                    var oldIpAddress = h.GetString("OldIpAddress") ?? "unknown";
                    var success = h.GetBoolean("Success") ?? false;
                    var message = h.GetString("Message") ?? "";
                    var authMethod = h.GetString("AuthMethod") ?? "unknown";
                    var apiKeyHash = h.GetString("ApiKeyHash") ?? "none";
                    var responseTimeMs = h.GetInt64("ResponseTimeMs") ?? 0;
                    var status = success ? "✓" : "✗";
                    var statusClass = success ? "text-success" : "text-danger";
                    
                    // Format IP change
                    var ipChange = string.IsNullOrEmpty(oldIpAddress) || oldIpAddress == "unknown" 
                        ? ipAddress 
                        : (oldIpAddress == ipAddress ? ipAddress : $"{oldIpAddress} → {ipAddress}");
                    
                    // Format auth method with icon
                    var authDisplay = authMethod switch
                    {
                        "ApiKey" => "🔑 API Key",
                        "Legacy" => "🔒 Legacy",
                        _ => authMethod
                    };
                    
                    // Format response time
                    var responseTimeDisplay = responseTimeMs > 0 ? $"{responseTimeMs}ms" : "-";
                    
                    return $@"<tr>
                        <td>{timestamp:yyyy-MM-dd HH:mm:ss}</td>
                        <td style='font-family: monospace;'>{ipChange}</td>
                        <td>{authDisplay}</td>
                        <td style='font-family: monospace;' class='text-muted small'>{apiKeyHash}</td>
                        <td>{responseTimeDisplay}</td>
                        <td class='{statusClass}'>{status}</td>
                        <td class='text-muted small'>{message}</td>
                    </tr>";
                }));
                historyHtml = $@"
                <div class='section'>
                    <h2>Recent Updates</h2>
                    <table class='table'>
                        <thead>
                            <tr>
                                <th>Timestamp</th>
                                <th>IP Change</th>
                                <th>Auth Method</th>
                                <th>API Key</th>
                                <th>Response Time</th>
                                <th>Status</th>
                                <th>Message</th>
                            </tr>
                        </thead>
                        <tbody>
                            {rows}
                        </tbody>
                    </table>
                </div>";
            }
            else
            {
                historyHtml = @"
                <div class='section'>
                    <h2>Update History</h2>
                    <div class='alert alert-info'>
                        <p>No updates recorded yet.</p>
                    </div>
                </div>";
            }

            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>DDNS Management - {hostname}</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&display=swap');
        @import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;700&display=swap');
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ 
            font-family: 'Orbitron', -apple-system, BlinkMacSystemFont, 'Segoe UI', monospace; 
            background: #0a0a0a;
            background: linear-gradient(135deg, #1a1a2e 0%, #0f0f1e 50%, #1a0033 100%);
            min-height: 100vh;
            padding: 20px;
            position: relative;
            overflow-x: hidden;
        }}
        .container {{ 
            background: #0a0a0a;
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            padding: 40px; 
            border-radius: 8px; 
            border: 1px solid #6a00ff;
            box-shadow: 
                0 0 20px rgba(106, 0, 255, 0.3),
                inset 0 0 20px rgba(0, 255, 255, 0.05); 
            max-width: 1400px; 
            margin: 0 auto;
            position: relative;
        }}
        h1 {{ 
            color: #00ffff;
            text-shadow: 0 0 10px rgba(0, 255, 255, 0.5);
            margin-bottom: 10px; 
            display: flex;
            align-items: center;
            gap: 10px;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 2px;
        }}
        h2 {{
            color: #ff00ff;
            text-shadow: 0 0 8px rgba(255, 0, 255, 0.5);
            margin-bottom: 20px;
            text-transform: uppercase;
            letter-spacing: 1px;
            font-size: 18px;
        }}
        .status {{ 
            display: inline-block; 
            padding: 4px 12px; 
            border-radius: 4px; 
            background: #00ff88;
            color: #000; 
            font-size: 12px; 
            font-weight: 700;
            text-transform: uppercase;
            box-shadow: 0 0 10px rgba(0, 255, 136, 0.5);
        }}
        .user-info {{ 
            color: #00ffff; 
            margin-bottom: 30px; 
            font-size: 14px;
            text-shadow: 0 0 5px #00ffff;
        }}
        .section {{
            margin-bottom: 40px;
        }}
        .section-header {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 20px;
        }}
        .config-form {{
            background: #f7fafc;
            border: 1px solid #e2e8f0;
            border-radius: 8px;
            padding: 20px;
            margin: 20px 0;
        }}
        .form-group {{
            margin-bottom: 20px;
        }}
        .form-label {{
            display: block;
            color: #4a5568;
            font-weight: 600;
            margin-bottom: 8px;
            font-size: 14px;
        }}
        .input-group {{
            display: flex;
            gap: 8px;
            align-items: center;
        }}
        .form-input {{
            flex: 1;
            padding: 10px 12px;
            border: 1px solid #cbd5e0;
            border-radius: 6px;
            font-family: 'Courier New', monospace;
            font-size: 14px;
            background: white;
            color: #2d3748;
        }}
        .form-input:read-only {{
            background: #edf2f7;
        }}
        .btn {{
            padding: 10px 16px;
            border-radius: 6px;
            border: none;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s;
            font-size: 14px;
        }}
        .btn-primary {{
            background: linear-gradient(90deg, #00ffaa 0%, #00ffff 100%);
            color: #000;
            font-weight: 600;
            text-transform: uppercase;
            box-shadow: 0 2px 8px rgba(0, 255, 170, 0.3);
            border: 1px solid #00ffaa;
        }}
        .btn-primary:hover {{
            background: linear-gradient(90deg, #00ffff 0%, #00ffaa 100%);
            box-shadow: 0 2px 12px rgba(0, 255, 170, 0.5);
            transform: translateY(-1px);
        }}
        .btn-danger {{
            background: #ff0066;
            color: #fff;
            font-weight: 600;
            text-transform: uppercase;
            box-shadow: 0 2px 8px rgba(255, 0, 102, 0.3);
            border: 1px solid #ff0066;
        }}
        .btn-danger:hover {{
            background: #ff3366;
            box-shadow: 0 2px 12px rgba(255, 0, 102, 0.5);
        }}
        .btn-sm {{
            padding: 6px 12px;
            font-size: 12px;
        }}
        .btn-icon {{
            width: 36px;
            height: 36px;
            padding: 0;
            border: 1px solid #cbd5e0;
            border-radius: 6px;
            background: white;
            cursor: pointer;
            transition: all 0.2s;
            display: inline-flex;
            align-items: center;
            justify-content: center;
        }}
        .btn-icon:hover {{
            background: #edf2f7;
            border-color: #a0aec0;
        }}
        .btn-group {{
            display: flex;
            gap: 10px;
        }}
        .table {{
            width: 100%;
            border-collapse: collapse;
            border: 1px solid #ff00ff;
        }}
        .table thead th {{
            background: #1a1a2e;
            padding: 12px;
            text-align: left;
            font-weight: 700;
            color: #00ffff;
            border-bottom: 1px solid #6a00ff;
            border-right: 1px solid rgba(106, 0, 255, 0.2);
            font-size: 13px;
            text-transform: uppercase;
            text-shadow: 0 0 5px rgba(0, 255, 255, 0.5);
            position: relative;
        }}
        .table thead th:last-child {{
            border-right: none;
        }}
        .table thead th::after {{
            content: '';
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: linear-gradient(90deg, 
                transparent 0%, 
                rgba(106, 0, 255, 0.1) 20%, 
                rgba(106, 0, 255, 0.2) 50%, 
                rgba(106, 0, 255, 0.1) 80%, 
                transparent 100%);
            pointer-events: none;
        }}
        .table tbody td {{
            padding: 12px;
            border-bottom: 1px solid rgba(106, 0, 255, 0.2);
            border-right: 1px solid rgba(106, 0, 255, 0.1);
            color: #00ff99;
            font-size: 14px;
            text-shadow: 0 0 3px rgba(0, 255, 153, 0.5);
        }}
        .table tbody td:last-child {{
            border-right: none;
        }}
        .table tbody tr:hover {{
            background: rgba(255, 0, 255, 0.1);
            box-shadow: inset 0 0 30px rgba(255, 0, 255, 0.2);
        }}
        .table tbody tr:last-child td {{
            border-bottom: 1px solid #6a00ff;
        }}
        .badge {{
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 12px;
            font-weight: 600;
        }}
        .badge-success {{
            background: #00ff00;
            color: #000;
            box-shadow: 0 0 10px #00ff00;
            font-weight: 700;
        }}
        .badge-danger {{
            background: #ff0066;
            color: #fff;
            box-shadow: 0 0 10px #ff0066;
            font-weight: 700;
        }}
        .key-display {{
            display: flex;
            gap: 8px;
            align-items: center;
        }}
        .key-hash {{
            font-family: 'CaskaydiaCove NFM', 'JetBrains Mono', 'Cascadia Code', 'Fira Code', 'Orbitron', monospace;
        }}
        .hottest-key {{
            color: #ff00ff !important;
            font-weight: 700;
            text-shadow: 0 0 8px rgba(255, 0, 255, 0.8);
        }}
        .key-input {{
            flex: 1;
            padding: 6px 10px;
            border: 1px solid #cbd5e0;
            border-radius: 4px;
            font-family: 'Courier New', monospace;
            font-size: 13px;
            background: white;
            min-width: 200px;
        }}
        .alert {{
            padding: 16px 20px;
            border-radius: 8px;
            margin: 20px 0;
        }}
        .alert-info {{
            background: #bee3f8;
            color: #2c5282;
        }}
        .command-box {{
            background: #1a202c;
            color: #e2e8f0;
            padding: 16px 20px;
            border-radius: 8px;
            font-family: 'Courier New', monospace;
            font-size: 14px;
            overflow-x: auto;
            white-space: nowrap;
        }}
        .user-email {{
            color: #00ff99;
            cursor: pointer;
            text-decoration: underline;
            transition: all 0.3s;
            display: inline-block;
        }}
        .user-email:hover {{
            animation: rainbow 2s linear infinite;
            text-shadow: 0 0 10px rgba(255, 0, 255, 0.8);
            transform: scale(1.05);
        }}
        @keyframes rainbow {{
            0% {{ color: #ff0000; }}
            16% {{ color: #ff8800; }}
            33% {{ color: #ffff00; }}
            50% {{ color: #00ff00; }}
            66% {{ color: #0088ff; }}
            83% {{ color: #8800ff; }}
            100% {{ color: #ff0000; }}
        }}
        .matrix-container {{
            position: fixed;
            top: 0;
            left: 0;
            width: 100vw;
            height: 100vh;
            pointer-events: none;
            overflow: hidden;
            z-index: 9999;
        }}
        .matrix-char {{
            position: absolute;
            font-family: 'CaskaydiaCove NFM', 'JetBrains Mono', 'Cascadia Code', 'Fira Code', 'Courier New', monospace;
            font-size: 20px;
            color: #00ff00;
            animation: matrixFall linear;
            text-shadow: 0 0 5px #00ff00;
            font-weight: 400;
        }}
        @keyframes matrixFall {{
            0% {{
                transform: translateY(-100px);
                opacity: 1;
            }}
            70% {{
                opacity: 1;
            }}
            100% {{
                transform: translateY(calc(100vh + 100px));
                opacity: 0;
            }}
        }}
        .text-muted {{
            color: #9966ff;
            text-shadow: 0 0 2px #9966ff;
        }}
        .text-success {{
            color: #00ff00;
            text-shadow: 0 0 5px #00ff00;
            font-weight: 700;
        }}
        .text-danger {{
            color: #ff0066;
            text-shadow: 0 0 5px #ff0066;
            font-weight: 700;
        }}
        footer {{
            margin-top: 40px;
            padding-top: 20px;
            border-top: 2px solid #ff00ff;
            color: #00ffff;
            font-size: 14px;
            text-align: center;
            text-shadow: 0 0 5px #00ffff;
        }}
        footer a {{
            color: #ff00ff;
            text-decoration: none;
            text-shadow: 0 0 5px #ff00ff;
            font-weight: 700;
        }}
        footer a:hover {{
            text-decoration: none;
            text-shadow: 0 0 10px #ff00ff, 0 0 20px #ff00ff;
        }}
    </style>
    <script>
        function copyToClipboard(text) {{
            navigator.clipboard.writeText(text).then(() => {{
                showToast('Copied to clipboard!');
            }});
        }}
        
        function copyField(fieldId) {{
            const field = document.getElementById(fieldId);
            copyToClipboard(field.value);
        }}
        
        function toggleKey(index) {{
            const input = document.getElementById('key-' + index);
            input.type = input.type === 'password' ? 'text' : 'password';
        }}
        
        function copyKey(index) {{
            const input = document.getElementById('key-' + index);
            copyToClipboard(input.value);
        }}
        
        function generateNewKey() {{
            if(confirm('Generate a new API key for {hostname}?')) {{
                window.location.href = '/api/manage/{hostname}/newkey';
            }}
        }}
        
        function revokeKey(keyHash) {{
            if(confirm('Revoke this API key? It will no longer work for updates.')) {{
                window.location.href = '/api/manage/{hostname}/revoke?keyhash=' + encodeURIComponent(keyHash);
            }}
        }}
        
        function revokeAllKeys() {{
            if(confirm('Revoke ALL API keys for {hostname}? This cannot be undone!')) {{
                window.location.href = '/api/manage/{hostname}/revokeall';
            }}
        }}
        
        function showToast(message) {{
            const toast = document.createElement('div');
            toast.textContent = message;
            toast.style.cssText = 'position:fixed;bottom:20px;right:20px;background:#48bb78;color:white;padding:12px 20px;border-radius:8px;font-weight:600;z-index:9999;';
            document.body.appendChild(toast);
            setTimeout(() => toast.remove(), 3000);
        }}
        
        function triggerEasterEgg() {{
            // Create Matrix rain container
            const container = document.createElement('div');
            container.className = 'matrix-container';
            document.body.appendChild(container);
            
            // Matrix characters
            const chars = '01アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲン';
            const columns = Math.floor(window.innerWidth / 20);
            
            // Create falling characters
            for (let i = 0; i < columns; i++) {{
                setTimeout(() => {{
                    const charElem = document.createElement('div');
                    charElem.className = 'matrix-char';
                    charElem.style.left = (i * 20) + 'px';
                    charElem.style.animationDuration = (3 + Math.random() * 2) + 's';
                    charElem.style.animationDelay = Math.random() + 's';
                    
                    // Random character stream
                    const charCount = 10 + Math.floor(Math.random() * 10);
                    let charStream = '';
                    for (let j = 0; j < charCount; j++) {{
                        charStream += chars[Math.floor(Math.random() * chars.length)] + '<br>';
                    }}
                    charElem.innerHTML = charStream;
                    
                    container.appendChild(charElem);
                    
                    // Remove character after animation
                    setTimeout(() => {{
                        charElem.remove();
                    }}, 5000);
                }}, i * 50);
            }}
            
            // Remove container after all animations complete
            setTimeout(() => {{
                container.remove();
            }}, 8000);
            
            // Show special message
            setTimeout(() => {{
                showToast('🎉 You found the Matrix! Welcome to the real world...');
            }}, 1000);
        }}
    </script>
</head>
<body>
    <div class='container'>
        <h1>{hostname} <span class='status'>Active</span></h1>
        <div class='user-info'>Managed by: <span class='user-email' onclick='triggerEasterEgg()'>{userEmail ?? "Unknown"}</span></div>
        
        {apiKeysHtml}
        
        {historyHtml}
        
        <footer>
            <a href='/.auth/logout'>Sign out</a> | 
            Azure DDNS Service | 
            <a href='https://github.com/yourusername/azure-ddns'>Documentation</a>
        </footer>
    </div>
</body>
</html>";
            
            await response.WriteStringAsync(html);
            return response;
        }
    }
}