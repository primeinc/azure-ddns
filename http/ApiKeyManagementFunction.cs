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
            var (userId, userEmail) = GetUser(req, _logger);

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
            max-width: 600px; 
            margin: 50px auto; 
        }}
        h1 {{ color: #1a202c; margin-bottom: 20px; }}
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
            background: #48bb78;
            color: white;
            border: none;
            padding: 8px 16px;
            border-radius: 4px;
            cursor: pointer;
            font-weight: 600;
        }}
        .copy-btn:hover {{
            background: #38a169;
        }}
    </style>
    <script>
        function copyKey() {{
            const keyElement = document.getElementById('apikey');
            navigator.clipboard.writeText(keyElement.textContent).then(() => {{
                const btn = document.getElementById('copybtn');
                btn.textContent = 'Copied!';
                setTimeout(() => btn.textContent = 'Copy', 2000);
            }});
        }}
    </script>
</head>
<body>
    <div class='container'>
        <div class='success-icon'>✅</div>
        <h1>New API Key Generated!</h1>
        
        <div class='api-key-box'>
            <p><strong>Hostname:</strong> {hostname}</p>
            <p><strong>Your new API key:</strong></p>
            <div class='api-key' id='apikey'>{newApiKey}</div>
            <button class='copy-btn' id='copybtn' onclick='copyKey()'>Copy</button>
        </div>
        
        <div class='warning'>
            ⚠️ Important: Save this API key now! For security reasons, we don't store the full key and it cannot be retrieved again.
        </div>
        
        <p>You can now use this API key to update your DDNS record:</p>
        <pre style='background: #1a202c; color: #e2e8f0; padding: 15px; border-radius: 6px; overflow-x: auto;'>curl -u ""{hostname.Split('.')[0]}:{newApiKey}"" ""https://{req.Url.Host}/api/nic/update?hostname={hostname}&myip=auto""</pre>
        
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
            var keyToRevoke = req.Query["key"];
            _logger.LogInformation($"[AUDIT-REVOKE] Revoke API key requested for hostname: {hostname}, key: {keyToRevoke?.Substring(0, 8)}...");

            // Get user from EasyAuth header
            var (userId, userEmail) = GetUser(req, _logger);

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

            // Revoke the key
            if (!string.IsNullOrEmpty(keyToRevoke))
            {
                await _tableStorage.RevokeApiKeyAsync(keyToRevoke);
                _logger.LogInformation($"[AUDIT-REVOKE-SUCCESS] API key {keyToRevoke.Substring(0, 8)}... revoked for {hostname}");
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
            var (userId, userEmail) = GetUser(req, _logger);

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
                var keyId = key.PartitionKey;
                if (!string.IsNullOrEmpty(keyId))
                {
                    await _tableStorage.RevokeApiKeyAsync(keyId);
                    _logger.LogInformation($"[AUDIT-REVOKEALL] Revoked key {keyId.Substring(0, 8)}...");
                }
            }

            _logger.LogInformation($"[AUDIT-REVOKEALL-SUCCESS] All {keys.Count} API keys revoked for {hostname}");

            // Redirect back to management page
            var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
            response.Headers.Add("Location", $"/api/manage/{hostname}");
            return response;
        }

        private static (string? oid, string? upn) GetUser(HttpRequestData req, ILogger log)
        {
            // Get user from EasyAuth header
            if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals))
            {
                // Try direct headers
                req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-ID", out var idVals);
                req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-NAME", out var nameVals);
                return (idVals?.FirstOrDefault(), nameVals?.FirstOrDefault());
            }

            var raw = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(raw)) return (null, null);

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                using var doc = JsonDocument.Parse(json);
                string? oid = null, upn = null;

                if (doc.RootElement.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (claim.TryGetProperty("typ", out var typ) && claim.TryGetProperty("val", out var val))
                        {
                            var typeStr = typ.GetString();
                            var valStr = val.GetString();
                            if (typeStr == "http://schemas.microsoft.com/identity/claims/objectidentifier") oid = valStr;
                            if (typeStr == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn") upn = valStr;
                        }
                    }
                }
                
                return (oid, upn);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[AUDIT] Failed to parse X-MS-CLIENT-PRINCIPAL");
                return (null, null);
            }
        }
    }
}