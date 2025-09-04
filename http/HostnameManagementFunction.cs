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
        <div class='user-info'>Managed by: {userEmail ?? "Unknown"}</div>
        
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
                var rows = string.Join("\n", apiKeys.Select((key, index) =>
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
                    var keyDisplay = $@"
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
                    var success = h.GetBoolean("Success") ?? false;
                    var message = h.GetString("Message") ?? "";
                    var status = success ? "Success" : "Failed";
                    
                    return $"<tr><td>{timestamp:yyyy-MM-dd HH:mm:ss}</td><td>{ipAddress}</td><td>{status}</td><td class='text-muted small'>{message}</td></tr>";
                }));
                historyHtml = $@"
                <div class='section'>
                    <h2>Recent Updates</h2>
                    <table class='table'>
                        <thead>
                            <tr>
                                <th>Timestamp</th>
                                <th>IP Address</th>
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
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ 
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); 
            min-height: 100vh;
            padding: 20px;
        }}
        .container {{ 
            background: white; 
            padding: 40px; 
            border-radius: 12px; 
            box-shadow: 0 20px 40px rgba(0,0,0,0.1); 
            max-width: 1400px; 
            margin: 0 auto; 
        }}
        h1 {{ 
            color: #1a202c; 
            margin-bottom: 10px; 
            display: flex;
            align-items: center;
            gap: 10px;
        }}
        h2 {{
            color: #2d3748;
            margin-bottom: 20px;
        }}
        .status {{ 
            display: inline-block; 
            padding: 4px 12px; 
            border-radius: 20px; 
            background: #48bb78; 
            color: white; 
            font-size: 14px; 
            font-weight: 500;
        }}
        .user-info {{ 
            color: #718096; 
            margin-bottom: 30px; 
            font-size: 14px;
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
            background: #4299e1;
            color: white;
        }}
        .btn-primary:hover {{
            background: #3182ce;
        }}
        .btn-danger {{
            background: #f56565;
            color: white;
        }}
        .btn-danger:hover {{
            background: #e53e3e;
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
        }}
        .table thead th {{
            background: #f7fafc;
            padding: 12px;
            text-align: left;
            font-weight: 600;
            color: #4a5568;
            border-bottom: 2px solid #e2e8f0;
            font-size: 13px;
            text-transform: uppercase;
        }}
        .table tbody td {{
            padding: 12px;
            border-bottom: 1px solid #e2e8f0;
            color: #2d3748;
            font-size: 14px;
        }}
        .table tbody tr:hover {{
            background: #f7fafc;
        }}
        .badge {{
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 12px;
            font-weight: 600;
        }}
        .badge-success {{
            background: #c6f6d5;
            color: #22543d;
        }}
        .badge-danger {{
            background: #fed7d7;
            color: #742a2a;
        }}
        .key-display {{
            display: flex;
            gap: 8px;
            align-items: center;
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
        .text-muted {{
            color: #a0aec0;
        }}
        footer {{
            margin-top: 40px;
            padding-top: 20px;
            border-top: 1px solid #e2e8f0;
            color: #718096;
            font-size: 14px;
            text-align: center;
        }}
        footer a {{
            color: #4299e1;
            text-decoration: none;
        }}
        footer a:hover {{
            text-decoration: underline;
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
    </script>
</head>
<body>
    <div class='container'>
        <h1>{hostname} <span class='status'>Active</span></h1>
        <div class='user-info'>Managed by: {userEmail ?? "Unknown"}</div>
        
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