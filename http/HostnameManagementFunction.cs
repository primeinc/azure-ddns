using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text;
using System.Text.Json;

namespace Company.Function
{
    public class HostnameManagementFunction
    {
        private readonly ILogger<HostnameManagementFunction> _logger;
        private readonly MsalAuthenticationService _authService;
        private readonly TableStorageService _tableStorage;
        private readonly ApiKeyService _apiKeyService;

        public HostnameManagementFunction(
            ILogger<HostnameManagementFunction> logger,
            MsalAuthenticationService authService,
            TableStorageService tableStorage,
            ApiKeyService apiKeyService)
        {
            _logger = logger;
            _authService = authService;
            _tableStorage = tableStorage;
            _apiKeyService = apiKeyService;
        }

        [Function("ManageHostname")]
        public async Task<HttpResponseData> ManageHostname(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/{hostname}")] HttpRequestData req,
            string hostname)
        {
            _logger.LogInformation($"Management request for hostname: {hostname}");

            // Extract bearer token from Authorization header
            string authHeader = null;
            if (req.Headers.TryGetValues("Authorization", out var authValues))
            {
                authHeader = authValues.FirstOrDefault();
            }
            var token = _authService.ExtractBearerToken(authHeader);

            if (string.IsNullOrEmpty(token))
            {
                // No token provided, redirect to Azure AD login
                var authUrl = _authService.GenerateAuthenticationUrl(hostname);
                var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                response.Headers.Add("Location", authUrl);
                return response;
            }

            // Validate the token
            var principal = await _authService.ValidateTokenAsync(token);
            if (principal == null)
            {
                var unauthorizedResponse = req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Invalid or expired token");
                return unauthorizedResponse;
            }

            var principalId = _authService.GetPrincipalIdFromToken(principal);
            var email = _authService.GetEmailFromToken(principal);

            if (string.IsNullOrEmpty(principalId))
            {
                var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Could not extract principal ID from token");
                return badRequestResponse;
            }

            // Check if hostname is already claimed
            var currentOwner = await _tableStorage.GetHostnameOwnerAsync(hostname);
            
            if (currentOwner != null && currentOwner != principalId)
            {
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync($"Hostname {hostname} is already claimed by another user");
                return forbiddenResponse;
            }

            if (currentOwner == null)
            {
                // Claim the hostname
                var claimed = await _tableStorage.ClaimHostnameAsync(hostname, principalId, email ?? "unknown");
                if (!claimed)
                {
                    var conflictResponse = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
                    await conflictResponse.WriteStringAsync("Failed to claim hostname");
                    return conflictResponse;
                }
                _logger.LogInformation($"Hostname {hostname} claimed by {principalId}");
            }

            // Get or generate API key
            var apiKeys = await _tableStorage.GetApiKeysForOwnerAsync(principalId);
            var existingKey = apiKeys.FirstOrDefault(k => k.RowKey == hostname);
            
            string apiKey;
            if (existingKey == null)
            {
                // Generate new API key
                apiKey = await _apiKeyService.GenerateApiKeyAsync(hostname, principalId);
                _logger.LogInformation($"Generated new API key for hostname {hostname}");
            }
            else
            {
                // For security, we can't retrieve the original API key
                // User must generate a new one if needed
                apiKey = "[Use existing key or generate new one]";
            }

            // Get update history
            var history = await _tableStorage.GetUpdateHistoryAsync(hostname, 10);

            // Build management dashboard HTML
            var html = GenerateManagementDashboard(hostname, email ?? principalId, apiKey, history);
            
            var okResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            okResponse.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await okResponse.WriteStringAsync(html);
            return okResponse;
        }

        [Function("GenerateApiKey")]
        public async Task<HttpResponseData> GenerateApiKey(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/key/generate")] HttpRequestData req)
        {
            // Extract and validate token
            var authHeader = req.Headers.GetValues("Authorization")?.FirstOrDefault();
            var token = _authService.ExtractBearerToken(authHeader);

            if (string.IsNullOrEmpty(token))
            {
                var unauthorizedResponse = req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Authentication required");
                return unauthorizedResponse;
            }

            var principal = await _authService.ValidateTokenAsync(token);
            if (principal == null)
            {
                var unauthorizedResponse = req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Invalid or expired token");
                return unauthorizedResponse;
            }

            var principalId = _authService.GetPrincipalIdFromToken(principal);
            if (string.IsNullOrEmpty(principalId))
            {
                var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Could not extract principal ID from token");
                return badRequestResponse;
            }

            // Parse request body
            var requestBody = await req.ReadAsStringAsync();
            if (string.IsNullOrEmpty(requestBody))
            {
                var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Request body is required");
                return badRequestResponse;
            }
            
            var request = JsonSerializer.Deserialize<GenerateKeyRequest>(requestBody);
            
            if (request == null || string.IsNullOrEmpty(request.Hostname))
            {
                var badRequestResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Hostname is required");
                return badRequestResponse;
            }

            // Verify ownership
            var owner = await _tableStorage.GetHostnameOwnerAsync(request.Hostname);
            if (owner != principalId)
            {
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("You do not own this hostname");
                return forbiddenResponse;
            }

            // Generate new API key
            var apiKey = await _apiKeyService.GenerateApiKeyAsync(request.Hostname, principalId);
            
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { apiKey, hostname = request.Hostname });
            return response;
        }

        private string GenerateManagementDashboard(string hostname, string owner, string apiKey, List<Azure.Data.Tables.TableEntity> history)
        {
            var currentIp = history.FirstOrDefault()?["IpAddress"]?.ToString() ?? "Not set";
            var lastUpdate = history.FirstOrDefault()?["Timestamp"]?.ToString() ?? "Never";
            
            var historyHtml = new StringBuilder();
            foreach (var update in history.Take(5))
            {
                var timestamp = update["Timestamp"]?.ToString() ?? "";
                var ip = update["IpAddress"]?.ToString() ?? "";
                var success = update["Success"]?.ToString() ?? "";
                var statusClass = success == "True" ? "success" : "failure";
                historyHtml.AppendLine($"<tr class='{statusClass}'><td>{timestamp}</td><td>{ip}</td><td>{success}</td></tr>");
            }

            return $@"<!DOCTYPE html>
<html>
<head>
    <title>DDNS Management - {hostname}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 1200px; margin: 0 auto; padding: 20px; background: #f5f5f5; }}
        .dashboard {{ background: white; border-radius: 8px; padding: 30px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        h1 {{ color: #333; border-bottom: 2px solid #0078d4; padding-bottom: 10px; }}
        .info-grid {{ display: grid; grid-template-columns: 150px 1fr; gap: 15px; margin: 20px 0; }}
        .info-label {{ font-weight: 600; color: #666; }}
        .info-value {{ font-family: monospace; background: #f8f8f8; padding: 5px 10px; border-radius: 4px; }}
        .api-key {{ background: #fffbf0; border: 1px solid #ffa500; padding: 10px; border-radius: 4px; margin: 20px 0; }}
        .commands {{ background: #f0f8ff; border: 1px solid #0078d4; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        code {{ background: #2b2b2b; color: #f8f8f2; padding: 10px; border-radius: 4px; display: block; margin: 10px 0; overflow-x: auto; }}
        table {{ width: 100%; border-collapse: collapse; margin: 20px 0; }}
        th {{ background: #0078d4; color: white; padding: 10px; text-align: left; }}
        td {{ padding: 10px; border-bottom: 1px solid #ddd; }}
        .success {{ background: #f0fff0; }}
        .failure {{ background: #fff0f0; }}
        button {{ background: #0078d4; color: white; border: none; padding: 10px 20px; border-radius: 4px; cursor: pointer; margin: 10px 0; }}
        button:hover {{ background: #005a9e; }}
    </style>
</head>
<body>
    <div class='dashboard'>
        <h1>🔒 DDNS Management Dashboard</h1>
        
        <div class='info-grid'>
            <div class='info-label'>Hostname:</div>
            <div class='info-value'>{hostname}.ddns.title.dev</div>
            
            <div class='info-label'>Owner:</div>
            <div class='info-value'>{owner}</div>
            
            <div class='info-label'>Current IP:</div>
            <div class='info-value'>{currentIp}</div>
            
            <div class='info-label'>Last Updated:</div>
            <div class='info-value'>{lastUpdate}</div>
        </div>

        <div class='api-key'>
            <h2>🔑 API Key</h2>
            <div class='info-value' id='apiKey'>{apiKey}</div>
            <button onclick='generateNewKey()'>Generate New API Key</button>
            <p><small>⚠️ Save this key securely. It cannot be retrieved once you leave this page.</small></p>
        </div>

        <div class='commands'>
            <h2>📡 Update Commands</h2>
            <p>Use these commands to update your DDNS record from your router or device:</p>
            
            <h3>Using curl:</h3>
            <code>curl -u {hostname}:[API_KEY] ""https://ddns.title.dev/nic/update?hostname={hostname}.ddns.title.dev&myip=auto""</code>
            
            <h3>Using wget:</h3>
            <code>wget --auth-no-challenge --user={hostname} --password=[API_KEY] ""https://ddns.title.dev/nic/update?hostname={hostname}.ddns.title.dev&myip=auto""</code>
            
            <h3>UniFi Router Configuration:</h3>
            <code>
Server: ddns.title.dev<br>
Hostname: {hostname}.ddns.title.dev<br>
Username: {hostname}<br>
Password: [API_KEY]
            </code>
        </div>

        <h2>📊 Recent Updates</h2>
        <table>
            <thead>
                <tr>
                    <th>Timestamp</th>
                    <th>IP Address</th>
                    <th>Status</th>
                </tr>
            </thead>
            <tbody>
                {historyHtml}
            </tbody>
        </table>
    </div>

    <script>
        async function generateNewKey() {{
            if (!confirm('Are you sure? This will invalidate your current API key.')) return;
            
            try {{
                const response = await fetch('/api/key/generate', {{
                    method: 'POST',
                    headers: {{
                        'Content-Type': 'application/json',
                        'Authorization': 'Bearer ' + localStorage.getItem('token')
                    }},
                    body: JSON.stringify({{ hostname: '{hostname}' }})
                }});
                
                if (response.ok) {{
                    const data = await response.json();
                    document.getElementById('apiKey').textContent = data.apiKey;
                    alert('New API key generated successfully!');
                }} else {{
                    alert('Failed to generate new API key: ' + await response.text());
                }}
            }} catch (error) {{
                alert('Error: ' + error.message);
            }}
        }}
    </script>
</body>
</html>";
        }

        private class GenerateKeyRequest
        {
            public string Hostname { get; set; } = string.Empty;
        }
    }
}