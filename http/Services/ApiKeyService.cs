using Azure.Security.KeyVault.Secrets;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Company.Function.Services
{
    public class ApiKeyService
    {
        private readonly SecretClient _secretClient;
        private readonly TableStorageService _tableStorage;
        private readonly ILogger<ApiKeyService> _logger;

        public ApiKeyService(TableStorageService tableStorage, ILogger<ApiKeyService> logger)
        {
            _tableStorage = tableStorage;
            _logger = logger;
            
            var keyVaultUri = Environment.GetEnvironmentVariable("KEY_VAULT_URI");
            var environment = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT");
            
            if (environment == "Development")
            {
                _logger.LogWarning("[INIT] Running in Development mode - Key Vault operations will be mocked");
                // In development, we'll store keys in Table Storage only
                _secretClient = null!; // Will check for null before using
            }
            else
            {
                if (string.IsNullOrEmpty(keyVaultUri))
                {
                    throw new InvalidOperationException("KEY_VAULT_URI environment variable is not set");
                }
                
                _secretClient = new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential());
            }
        }

        public async Task<string> GenerateApiKeyAsync(string hostname, string ownerPrincipalId)
        {
            _logger.LogInformation($"[AUDIT-KEYGEN] Starting API key generation for hostname '{hostname}' requested by '{ownerPrincipalId}'");
            
            try
            {
                // Generate a cryptographically secure API key
                var apiKey = GenerateSecureApiKey();
                var apiKeyHash = HashApiKey(apiKey);
                
                _logger.LogInformation($"[AUDIT-KEYGEN] Generated secure key with hash '{apiKeyHash.Substring(0, 8)}...' for '{hostname}'");
                
                // Store the full API key in Key Vault (skip in development)
                if (_secretClient != null)
                {
                    var secretName = $"apikey-{SanitizeForKeyVault(hostname)}-{Guid.NewGuid():N}";
                    _logger.LogInformation($"[AUDIT-KEYGEN-VAULT] Storing key in Key Vault as secret '{secretName}'");
                    
                    await _secretClient.SetSecretAsync(secretName, apiKey);
                    _logger.LogInformation($"[AUDIT-KEYGEN-VAULT-SUCCESS] Key stored in Key Vault successfully");
                }
                else
                {
                    _logger.LogWarning($"[AUDIT-KEYGEN-DEV] Development mode - skipping Key Vault storage for key");
                }
                
                // Store the mapping in Table Storage (with hash, not the actual key)
                var stored = await _tableStorage.StoreApiKeyMappingAsync(apiKeyHash, hostname, ownerPrincipalId);
                
                if (stored)
                {
                    _logger.LogInformation($"[AUDIT-KEYGEN-SUCCESS] API key generated and stored for '{hostname}' (owner: '{ownerPrincipalId}', hash: '{apiKeyHash.Substring(0, 8)}...')");
                }
                else
                {
                    _logger.LogWarning($"[AUDIT-KEYGEN-WARNING] API key generated but table storage mapping failed for '{hostname}'");
                }
                
                return apiKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-KEYGEN-ERROR] Failed to generate API key for hostname '{hostname}' (owner: '{ownerPrincipalId}')");
                throw;
            }
        }

        public async Task<(bool isValid, string? hostname)> ValidateApiKeyAsync(string apiKey)
        {
            var keyPreview = apiKey.Length > 8 ? apiKey.Substring(0, 8) : apiKey;
            _logger.LogDebug($"[AUDIT-VALIDATE] Starting API key validation for key '{keyPreview}...'");
            
            try
            {
                // Hash the provided API key
                var apiKeyHash = HashApiKey(apiKey);
                _logger.LogDebug($"[AUDIT-VALIDATE] Computed hash '{apiKeyHash.Substring(0, 8)}...' for validation");
                
                // Check if the hash exists in Table Storage
                var (hostname, isValid) = await _tableStorage.ValidateApiKeyAsync(apiKeyHash);
                
                if (isValid && !string.IsNullOrEmpty(hostname))
                {
                    _logger.LogInformation($"[AUDIT-VALIDATE-SUCCESS] API key validated successfully for hostname '{hostname}' (hash: '{apiKeyHash.Substring(0, 8)}...')");
                    return (true, hostname);
                }
                
                _logger.LogWarning($"[AUDIT-VALIDATE-FAIL] API key validation failed (hash: '{apiKeyHash.Substring(0, 8)}...')");
                return (false, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-VALIDATE-ERROR] Error validating API key '{keyPreview}...'");
                return (false, null);
            }
        }

        public async Task<bool> RevokeApiKeyAsync(string apiKeyHash, string ownerPrincipalId)
        {
            _logger.LogInformation($"[AUDIT-REVOKE] Attempting to revoke API key '{apiKeyHash.Substring(0, 8)}...' for owner '{ownerPrincipalId}'");
            
            try
            {
                // Get the API key details from Table Storage
                var keys = await _tableStorage.GetApiKeysForOwnerAsync(ownerPrincipalId);
                _logger.LogInformation($"[AUDIT-REVOKE] Found {keys.Count} keys for owner '{ownerPrincipalId}'");
                
                foreach (var key in keys)
                {
                    if (key.PartitionKey == apiKeyHash)
                    {
                        var hostname = key.RowKey;
                        var revokedAt = DateTimeOffset.UtcNow;
                        
                        // Mark as inactive in Table Storage
                        key["IsActive"] = false;
                        key["RevokedAt"] = revokedAt;
                        key["RevokedBy"] = ownerPrincipalId;
                        key["RevokedReason"] = "User requested revocation";
                        
                        // Note: We don't delete from Key Vault to maintain audit trail
                        // The key validation will fail due to IsActive = false
                        
                        _logger.LogInformation($"[AUDIT-REVOKE-SUCCESS] Revoked API key for hostname '{hostname}' (hash: '{apiKeyHash.Substring(0, 8)}...', owner: '{ownerPrincipalId}', revoked at: {revokedAt:yyyy-MM-dd HH:mm:ss})");
                        return true;
                    }
                }
                
                _logger.LogWarning($"[AUDIT-REVOKE-NOT-FOUND] API key '{apiKeyHash.Substring(0, 8)}...' not found for owner '{ownerPrincipalId}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-REVOKE-ERROR] Failed to revoke API key '{apiKeyHash.Substring(0, 8)}...' for owner '{ownerPrincipalId}'");
                return false;
            }
        }

        public Task<string?> GetApiKeyFromBasicAuth(string? authorizationHeader)
        {
            if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Basic "))
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                var base64Credentials = authorizationHeader.Substring("Basic ".Length).Trim();
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(base64Credentials));
                var parts = credentials.Split(':', 2);
                
                if (parts.Length == 2)
                {
                    // In the format username:apikey, we use the password field as the API key
                    return Task.FromResult<string?>(parts[1]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract API key from Basic auth header");
            }
            
            return Task.FromResult<string?>(null);
        }

        private string GenerateSecureApiKey()
        {
            // Generate 32 bytes of random data
            var randomBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }
            
            // Convert to base64 and make URL-safe
            var apiKey = Convert.ToBase64String(randomBytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
            
            return apiKey;
        }

        private string HashApiKey(string apiKey)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
                return Convert.ToBase64String(hashBytes)
                    .Replace("+", "-")
                    .Replace("/", "_")
                    .TrimEnd('=');
            }
        }

        private string SanitizeForKeyVault(string input)
        {
            // Key Vault secret names must match pattern: ^[0-9a-zA-Z-]+$
            return input.Replace(".", "-")
                       .Replace("_", "-")
                       .Replace(" ", "-")
                       .ToLowerInvariant();
        }
    }
}