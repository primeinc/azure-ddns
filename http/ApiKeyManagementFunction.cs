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
        private readonly TemplateService _templateService;

        public ApiKeyManagementFunction(
            ILogger<ApiKeyManagementFunction> logger,
            TableStorageService tableStorage,
            ApiKeyService apiKeyService,
            TemplateService templateService)
        {
            _logger = logger;
            _tableStorage = tableStorage;
            _apiKeyService = apiKeyService;
            _templateService = templateService;
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

            var model = new
            {
                Hostname = hostname,
                SimpleHostname = hostname.Split('.')[0],
                OwnerEmail = userEmail,
                ApiKey = newApiKey,
                BaseUrl = $"https://{req.Url.Host}",
                Host = req.Url.Host,
                SuccessMessage = "✅ New API key generated successfully! Save it now - it cannot be retrieved again.",
                ShowRegenerateForm = false
            };

            var html = await _templateService.RenderAdvancedTemplateAsync("api-key-management.html", model);
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