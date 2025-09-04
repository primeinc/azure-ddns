using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text.Json;

namespace Company.Function
{
    public class ApiKeyManagementFunction
    {
        private readonly ILogger<ApiKeyManagementFunction> _logger;
        private readonly TableStorageService _tableStorage;
        private readonly ApiKeyService _apiKeyService;

        public ApiKeyManagementFunction(
            ILogger<ApiKeyManagementFunction> logger,
            TableStorageService tableStorage,
            ApiKeyService apiKeyService)
        {
            _logger = logger;
            _tableStorage = tableStorage;
            _apiKeyService = apiKeyService;
        }

        [Function("GenerateNewApiKey")]
        public async Task<HttpResponseData> GenerateNewKey(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "manage/{hostname}/newkey")] HttpRequestData req,
            string hostname)
        {
            _logger.LogInformation($"[AUDIT-NEWKEY] New API key requested for hostname: {hostname}");

            // Get user from EasyAuth header
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning($"[AUDIT-NEWKEY] Unauthenticated request for new key on {hostname}");
                var authResponse = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                var encodedRedirect = Uri.EscapeDataString($"/api/manage/{hostname}");
                authResponse.Headers.Add("Location", $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}");
                return authResponse;
            }

            // Check ownership
            var owner = await _tableStorage.GetHostnameOwnerAsync(hostname);
            if (owner == null || owner != userId)
            {
                _logger.LogWarning($"[AUDIT-NEWKEY-DENIED] User {userId} attempted to generate key for {hostname} owned by {owner}");
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("You don't own this hostname");
                return forbiddenResponse;
            }

            // Generate new API key
            _logger.LogInformation($"[AUDIT-NEWKEY] Generating new API key for {hostname} by owner {userId}");
            var newApiKey = await _apiKeyService.GenerateApiKeyAsync(hostname, userId, userEmail);

            if (string.IsNullOrEmpty(newApiKey))
            {
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Failed to generate API key");
                return errorResponse;
            }

            // Return page showing the new key
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");

            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>New API Key - {hostname}</title>
    <style>
        body {{ 
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; 
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); 
            min-height: 100vh;
            padding: 20px;
            margin: 0;
        }}
        .container {{ 
            background: white; 
            padding: 40px; 
            border-radius: 12px; 
            box-shadow: 0 20px 40px rgba(0,0,0,0.1); 
            max-width: 700px; 
            margin: 50px auto; 
        }}
        h1 {{ color: #1a202c; margin-bottom: 20px; }}
        h2 {{ color: #2d3748; margin-top: 30px; margin-bottom: 20px; }}
        .success-icon {{ font-size: 48px; text-align: center; margin-bottom: 20px; }}
        .api-key-box {{
            background: #f7fafc;
            border: 2px solid #48bb78;
            border-radius: 8px;
            padding: 20px;
            margin: 20px 0;
        }}
        .api-key {{
            font-family: 'Courier New', monospace;
            font-size: 18px;
            color: #2d3748;
            word-break: break-all;
            padding: 15px;
            background: white;
            border-radius: 6px;
            border: 1px solid #e2e8f0;
            margin: 10px 0;
        }}
        .warning {{
            background: #fed7d7;
            color: #742a2a;
            padding: 15px;
            border-radius: 6px;
            margin: 20px 0;
            font-weight: 600;
        }}
        .form-section {{
            background: #f8fafc;
            border: 1px solid #e2e8f0;
            border-radius: 8px;
            padding: 25px;
            margin: 25px 0;
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
            display: inline-block;
            padding: 12px 24px;
            background: #4299e1;
            color: white;
            text-decoration: none;
            border-radius: 6px;
            font-weight: 600;
            margin-top: 20px;
        }}
        .btn:hover {{
            background: #3182ce;
        }}
        .copy-btn {{
            padding: 8px 16px;
            background: #48bb78;
            color: white;
            border: none;
            border-radius: 6px;
            cursor: pointer;
            font-weight: 600;
            white-space: nowrap;
        }}
        .copy-btn:hover {{
            background: #38a169;
        }}
        .copy-btn.copied {{
            background: #9f7aea;
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
            margin: 10px 0;
        }}
    </style>
    <script>
        function copyField(fieldId, btnId) {{
            const field = document.getElementById(fieldId);
            const btn = document.getElementById(btnId);
            navigator.clipboard.writeText(field.value).then(() => {{
                btn.textContent = '✓ Copied!';
                btn.classList.add('copied');
                setTimeout(() => {{
                    btn.textContent = 'Copy';
                    btn.classList.remove('copied');
                }}, 2000);
            }});
        }}
        
        function copyText(text, btnId) {{
            const btn = document.getElementById(btnId);
            navigator.clipboard.writeText(text).then(() => {{
                btn.textContent = '✓ Copied!';
                btn.classList.add('copied');
                setTimeout(() => {{
                    btn.textContent = 'Copy';
                    btn.classList.remove('copied');
                }}, 2000);
            }});
        }}
    </script>
</head>
<body>
    <div class='container'>
        <div class='success-icon'>✅</div>
        <h1>New API Key Generated!</h1>
        
        <div class='api-key-box'>
            <p><strong>Your new API key:</strong></p>
            <div class='api-key' id='apikey'>{newApiKey}</div>
            <button class='copy-btn' onclick='copyText(""{newApiKey}"", ""keycopy"")' id='keycopy'>Copy API Key</button>
        </div>
        
        <div class='warning'>
            ⚠️ Important: Save this API key now! For security reasons, we don't store the full key and it cannot be retrieved again.
        </div>
        
        <h2>Router Configuration (DynDNS2)</h2>
        <div class='form-section'>
            <p style='color: #718096; margin-bottom: 20px;'>Copy these settings to your router's Dynamic DNS configuration:</p>
            
            <div class='form-group'>
                <label class='form-label'>Service</label>
                <div class='input-group'>
                    <input type='text' id='service-field' class='form-input' value='Custom' readonly>
                    <button class='copy-btn' onclick='copyField(""service-field"", ""service-btn"")' id='service-btn'>Copy</button>
                </div>
            </div>
            
            <div class='form-group'>
                <label class='form-label'>Hostname</label>
                <div class='input-group'>
                    <input type='text' id='hostname-field' class='form-input' value='{hostname}' readonly>
                    <button class='copy-btn' onclick='copyField(""hostname-field"", ""hostname-btn"")' id='hostname-btn'>Copy</button>
                </div>
            </div>
            
            <div class='form-group'>
                <label class='form-label'>Username</label>
                <div class='input-group'>
                    <input type='text' id='username-field' class='form-input' value='{hostname.Split('.')[0]}' readonly>
                    <button class='copy-btn' onclick='copyField(""username-field"", ""username-btn"")' id='username-btn'>Copy</button>
                </div>
            </div>
            
            <div class='form-group'>
                <label class='form-label'>Password</label>
                <div class='input-group'>
                    <input type='text' id='password-field' class='form-input' value='{newApiKey}' readonly>
                    <button class='copy-btn' onclick='copyField(""password-field"", ""password-btn"")' id='password-btn'>Copy</button>
                </div>
            </div>
            
            <div class='form-group'>
                <label class='form-label'>Server</label>
                <div class='input-group'>
                    <input type='text' id='server-field' class='form-input' value='{req.Url.Host}' readonly>
                    <button class='copy-btn' onclick='copyField(""server-field"", ""server-btn"")' id='server-btn'>Copy</button>
                </div>
            </div>
        </div>
        
        <h2>Command Line Examples</h2>
        <div class='form-section'>
            <div class='form-group'>
                <label class='form-label'>Using curl:</label>
                <div class='command-box'>curl -u ""{hostname.Split('.')[0]}:{newApiKey}"" ""https://{req.Url.Host}/api/nic/update?hostname={hostname}&myip=auto""</div>
            </div>
            
            <div class='form-group'>
                <label class='form-label'>Using wget:</label>
                <div class='command-box'>wget --auth-no-challenge --user=""{hostname.Split('.')[0]}"" --password=""{newApiKey}"" ""https://{req.Url.Host}/api/nic/update?hostname={hostname}&myip=auto""</div>
            </div>
        </div>
        
        <a href='/api/manage/{hostname}' class='btn'>← Back to Management Page</a>
    </div>
</body>
</html>";

            await response.WriteStringAsync(html);
            _logger.LogInformation($"[AUDIT-NEWKEY-SUCCESS] New API key generated and displayed for {hostname}");
            return response;
        }

        [Function("RevokeApiKey")]
        public async Task<HttpResponseData> RevokeKey(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/{hostname}/revoke")] HttpRequestData req,
            string hostname)
        {
            var keyHashToRevoke = req.Query["keyhash"];  // We pass the hash, not the raw key
            _logger.LogInformation($"[AUDIT-REVOKE] Revoke API key requested for hostname: {hostname}, key hash: {keyHashToRevoke?.Substring(0, Math.Min(8, keyHashToRevoke?.Length ?? 0))}...");

            // Get user from EasyAuth header
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);

            if (string.IsNullOrEmpty(userId))
            {
                var authResponse = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                var encodedRedirect = Uri.EscapeDataString($"/api/manage/{hostname}");
                authResponse.Headers.Add("Location", $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}");
                return authResponse;
            }

            // Check ownership
            var owner = await _tableStorage.GetHostnameOwnerAsync(hostname);
            if (owner == null || owner != userId)
            {
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("You don't own this hostname");
                return forbiddenResponse;
            }

            // Revoke the key using its hash
            if (!string.IsNullOrEmpty(keyHashToRevoke))
            {
                await _tableStorage.RevokeApiKeyAsync(keyHashToRevoke);
                _logger.LogInformation($"[AUDIT-REVOKE-SUCCESS] API key with hash {keyHashToRevoke.Substring(0, Math.Min(8, keyHashToRevoke.Length))}... revoked for {hostname}");
            }

            // Redirect back to management page
            var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
            response.Headers.Add("Location", $"/api/manage/{hostname}");
            return response;
        }

        [Function("RevokeAllApiKeys")]
        public async Task<HttpResponseData> RevokeAllKeys(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/{hostname}/revokeall")] HttpRequestData req,
            string hostname)
        {
            _logger.LogInformation($"[AUDIT-REVOKEALL] Revoke ALL API keys requested for hostname: {hostname}");

            // Get user from EasyAuth header
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);

            if (string.IsNullOrEmpty(userId))
            {
                var authResponse = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                var encodedRedirect = Uri.EscapeDataString($"/api/manage/{hostname}");
                authResponse.Headers.Add("Location", $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}");
                return authResponse;
            }

            // Check ownership
            var owner = await _tableStorage.GetHostnameOwnerAsync(hostname);
            if (owner == null || owner != userId)
            {
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("You don't own this hostname");
                return forbiddenResponse;
            }

            // Revoke all keys for this hostname
            var keys = await _tableStorage.GetApiKeysForHostnameAsync(hostname);
            foreach (var key in keys)
            {
                var keyHash = key.PartitionKey;  // PartitionKey is already the hash
                if (!string.IsNullOrEmpty(keyHash))
                {
                    await _tableStorage.RevokeApiKeyAsync(keyHash);
                    _logger.LogInformation($"[AUDIT-REVOKEALL] Revoked key with hash {keyHash.Substring(0, Math.Min(8, keyHash.Length))}...");
                }
            }

            _logger.LogInformation($"[AUDIT-REVOKEALL-SUCCESS] All {keys.Count} API keys revoked for {hostname}");

            // Redirect back to management page
            var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
            response.Headers.Add("Location", $"/api/manage/{hostname}");
            return response;
        }

    }
}