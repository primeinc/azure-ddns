using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Company.Function.Services
{
    public class TableStorageService
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly ILogger<TableStorageService> _logger;
        private readonly TableClient _hostnameOwnershipTable;
        private readonly TableClient _apiKeysTable;
        private readonly TableClient _updateHistoryTable;

        public TableStorageService(ILogger<TableStorageService> logger)
        {
            _logger = logger;
            
            // Check if we're running locally with Azurite
            var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
            var environment = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT");
            
            if (!string.IsNullOrEmpty(connectionString) && environment == "Development")
            {
                // Use connection string for local development with Azurite
                _logger.LogInformation("[INIT] Using Azurite connection string for local development");
                _tableServiceClient = new TableServiceClient(connectionString);
            }
            else
            {
                // Use managed identity for production
                var storageAccountName = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT_NAME");
                var storageUri = new Uri($"https://{storageAccountName}.table.core.windows.net");
                _logger.LogInformation($"[INIT] Using managed identity for storage account '{storageAccountName}'");
                _tableServiceClient = new TableServiceClient(storageUri, new DefaultAzureCredential());
            }
            
            // Initialize table clients
            _hostnameOwnershipTable = _tableServiceClient.GetTableClient("HostnameOwnership");
            _apiKeysTable = _tableServiceClient.GetTableClient("ApiKeys");
            _updateHistoryTable = _tableServiceClient.GetTableClient("UpdateHistory");
            
            // Ensure tables exist
            Task.Run(async () => await InitializeTablesAsync()).Wait();
        }

        private async Task InitializeTablesAsync()
        {
            try
            {
                await _hostnameOwnershipTable.CreateIfNotExistsAsync();
                await _apiKeysTable.CreateIfNotExistsAsync();
                await _updateHistoryTable.CreateIfNotExistsAsync();
                _logger.LogInformation("Table Storage initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Table Storage");
                throw;
            }
        }

        // Hostname Ownership Operations
        public async Task<bool> ClaimHostnameAsync(string hostname, string ownerPrincipalId, string ownerEmail)
        {
            _logger.LogInformation($"[AUDIT] Attempting to claim hostname '{hostname}' for principal '{ownerPrincipalId}' with email '{ownerEmail}'");
            
            try
            {
                var entity = new TableEntity(hostname, ownerPrincipalId)
                {
                    ["Email"] = ownerEmail,
                    ["ClaimedAt"] = DateTimeOffset.UtcNow,
                    ["LastModified"] = DateTimeOffset.UtcNow,
                    ["ClaimedFromIP"] = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "local"
                };
                
                var response = await _hostnameOwnershipTable.AddEntityAsync(entity);
                
                if (!response.IsError)
                {
                    _logger.LogInformation($"[AUDIT-SUCCESS] Hostname '{hostname}' successfully claimed by '{ownerPrincipalId}' ({ownerEmail})");
                    
                    
                    return true;
                }
                
                _logger.LogWarning($"[AUDIT-FAIL] Failed to claim hostname '{hostname}' - response error: {response.ReasonPhrase}");
                return false;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogWarning($"[AUDIT-CONFLICT] Hostname '{hostname}' is already claimed - attempted by '{ownerPrincipalId}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-ERROR] Failed to claim hostname '{hostname}' for '{ownerPrincipalId}'");
                return false;
            }
        }

        public async Task<string?> GetHostnameOwnerAsync(string hostname)
        {
            _logger.LogDebug($"[AUDIT-QUERY] Looking up owner for hostname '{hostname}'");
            
            try
            {
                var response = _hostnameOwnershipTable.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{hostname}'",
                    maxPerPage: 1);
                
                await foreach (var entity in response)
                {
                    var owner = entity.RowKey;
                    _logger.LogInformation($"[AUDIT-FOUND] Hostname '{hostname}' is owned by '{owner}'");
                    return owner;
                }
                
                _logger.LogInformation($"[AUDIT-NOT-FOUND] Hostname '{hostname}' has no owner");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-ERROR] Failed to get owner for hostname '{hostname}'");
                return null;
            }
        }

        // API Key Operations
        public async Task<bool> StoreApiKeyMappingAsync(string apiKeyHash, string hostname, string ownerPrincipalId, string? ownerEmail = null)
        {
            var createdAt = DateTimeOffset.UtcNow;
            var expiresAt = createdAt.AddYears(1);
            
            _logger.LogInformation($"[AUDIT-API-KEY] Creating API key mapping for hostname '{hostname}' owned by '{ownerPrincipalId}' ({ownerEmail}), expires {expiresAt:yyyy-MM-dd}");
            
            try
            {
                var entity = new TableEntity(apiKeyHash, hostname)
                {
                    ["OwnerPrincipalId"] = ownerPrincipalId,
                    ["CreatedByEmail"] = ownerEmail ?? "Unknown",
                    ["CreatedAt"] = createdAt,
                    ["ExpiresAt"] = expiresAt,
                    ["IsActive"] = true,
                    ["UseCount"] = 0,
                    ["CreatedByIP"] = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "local"
                };
                
                await _apiKeysTable.UpsertEntityAsync(entity);
                _logger.LogInformation($"[AUDIT-API-KEY-SUCCESS] API key created for '{hostname}' with hash '{apiKeyHash.Substring(0, 8)}...'");
                
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-API-KEY-ERROR] Failed to store API key mapping for hostname '{hostname}'");
                return false;
            }
        }

        public async Task UpdateApiKeyUsageAsync(string apiKeyHash, string clientIp)
        {
            try
            {
                // Get the existing entity
                var response = _apiKeysTable.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{apiKeyHash}'",
                    maxPerPage: 1);
                
                await foreach (var entity in response)
                {
                    // Update usage stats
                    var currentCount = entity.GetInt32("UseCount") ?? 0;
                    entity["UseCount"] = currentCount + 1;
                    entity["LastUsed"] = DateTimeOffset.UtcNow;
                    entity["LastUsedFromIp"] = clientIp;
                    
                    await _apiKeysTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    _logger.LogInformation($"[AUDIT-USAGE] Updated usage stats for API key hash '{apiKeyHash.Substring(0, 8)}...': Count={currentCount + 1}, IP={clientIp}");
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-USAGE-ERROR] Failed to update usage stats for API key hash '{apiKeyHash.Substring(0, 8)}...'");
            }
        }

        public async Task<(string? hostname, bool isValid)> ValidateApiKeyAsync(string apiKeyHash)
        {
            _logger.LogDebug($"[AUDIT-AUTH] Validating API key with hash '{apiKeyHash.Substring(0, 8)}...'");
            
            try
            {
                // Query for the API key by PartitionKey (which is the hash)
                var response = _apiKeysTable.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{apiKeyHash}'",
                    maxPerPage: 1);
                
                TableEntity? entity = null;
                await foreach (var item in response)
                {
                    entity = item;
                    break;
                }
                
                if (entity == null)
                {
                    _logger.LogWarning($"[AUDIT-AUTH-FAIL] API key not found: '{apiKeyHash.Substring(0, 8)}...'");
                    return (null, false);
                }
                
                var hostname = entity.RowKey;
                var isActive = entity.GetBoolean("IsActive") ?? false;
                var expiresAt = entity.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MinValue;
                var owner = entity.GetString("OwnerPrincipalId") ?? "unknown";
                
                if (!isActive)
                {
                    _logger.LogWarning($"[AUDIT-AUTH-INACTIVE] API key for '{hostname}' is inactive (owner: '{owner}')");
                    return (null, false);
                }
                
                if (expiresAt < DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning($"[AUDIT-AUTH-EXPIRED] API key for '{hostname}' expired on {expiresAt:yyyy-MM-dd} (owner: '{owner}')");
                    return (null, false);
                }
                
                _logger.LogInformation($"[AUDIT-AUTH-SUCCESS] API key validated for '{hostname}' (owner: '{owner}', expires: {expiresAt:yyyy-MM-dd})");
                return (hostname, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-AUTH-ERROR] Failed to validate API key '{apiKeyHash.Substring(0, 8)}...'");
                return (null, false);
            }
        }

        public async Task<List<TableEntity>> GetApiKeysForOwnerAsync(string ownerPrincipalId)
        {
            var keys = new List<TableEntity>();
            
            try
            {
                var response = _apiKeysTable.QueryAsync<TableEntity>(
                    filter: $"OwnerPrincipalId eq '{ownerPrincipalId}'");
                
                await foreach (var entity in response)
                {
                    keys.Add(entity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get API keys for owner {ownerPrincipalId}");
            }
            
            return keys;
        }

        public async Task<List<TableEntity>> GetApiKeysForHostnameAsync(string hostname)
        {
            var keys = new List<TableEntity>();
            
            try
            {
                var response = _apiKeysTable.QueryAsync<TableEntity>(
                    filter: $"RowKey eq '{hostname}'");
                
                await foreach (var entity in response)
                {
                    keys.Add(entity);
                }
                
                _logger.LogInformation($"[AUDIT-API-KEYS] Found {keys.Count} API keys for hostname '{hostname}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-API-KEYS-ERROR] Failed to get API keys for hostname {hostname}");
            }
            
            return keys;
        }

        public async Task<bool> RevokeApiKeyAsync(string apiKeyHash)
        {
            _logger.LogInformation($"[AUDIT-REVOKE] Revoking API key with hash '{apiKeyHash.Substring(0, Math.Min(8, apiKeyHash.Length))}...'");
            
            try
            {
                // Get the entity first to find the hostname (RowKey)
                var response = _apiKeysTable.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{apiKeyHash}'",
                    maxPerPage: 1);
                
                TableEntity? entityToRevoke = null;
                await foreach (var entity in response)
                {
                    entityToRevoke = entity;
                    break;
                }
                
                if (entityToRevoke == null)
                {
                    _logger.LogWarning($"[AUDIT-REVOKE-NOT-FOUND] API key not found: '{apiKeyHash.Substring(0, Math.Min(8, apiKeyHash.Length))}...'");
                    return false;
                }
                
                var hostname = entityToRevoke.RowKey;
                var owner = entityToRevoke.GetString("OwnerPrincipalId") ?? "unknown";
                
                // Mark as inactive instead of deleting (preserve audit trail)
                entityToRevoke["IsActive"] = false;
                entityToRevoke["RevokedAt"] = DateTimeOffset.UtcNow;
                entityToRevoke["LastModified"] = DateTimeOffset.UtcNow;
                
                await _apiKeysTable.UpdateEntityAsync(entityToRevoke, entityToRevoke.ETag);
                
                _logger.LogInformation($"[AUDIT-REVOKE-SUCCESS] API key revoked for hostname '{hostname}' (owner: '{owner}')");
                
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-REVOKE-ERROR] Failed to revoke API key '{apiKeyHash.Substring(0, Math.Min(8, apiKeyHash.Length))}...'");
                return false;
            }
        }

        // Update History Operations
        public async Task LogUpdateAsync(
            string hostname, 
            string ipAddress, 
            bool success, 
            string? message = null,
            string? authMethod = null,
            string? apiKeyHash = null,
            string? oldIp = null,
            long? responseTimeMs = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            
            _logger.LogInformation($"[AUDIT-UPDATE] Recording DDNS update for '{hostname}' -> '{ipAddress}' (success: {success}, message: {message})");
            
            try
            {
                var entity = new TableEntity(hostname, timestamp.Ticks.ToString())
                {
                    ["IpAddress"] = ipAddress,
                    ["OldIpAddress"] = oldIp ?? "unknown",
                    ["Success"] = success,
                    ["Message"] = message ?? string.Empty,
                    ["Timestamp"] = timestamp,
                    ["AuthMethod"] = authMethod ?? "unknown",
                    ["ApiKeyHash"] = apiKeyHash != null ? apiKeyHash.Substring(0, Math.Min(12, apiKeyHash.Length)) + "..." : "none",
                    ["ResponseTimeMs"] = responseTimeMs ?? 0,
                    ["InstanceId"] = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "local",
                    ["UserAgent"] = Environment.GetEnvironmentVariable("HTTP_USER_AGENT") ?? "unknown"
                };
                
                await _updateHistoryTable.AddEntityAsync(entity);
                _logger.LogInformation($"[AUDIT-UPDATE-LOGGED] Update history recorded for '{hostname}' at {timestamp:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AUDIT-UPDATE-ERROR] Failed to log update for hostname '{hostname}'");
            }
        }

        public async Task<List<TableEntity>> GetUpdateHistoryAsync(string hostname, int maxRecords = 100)
        {
            var history = new List<TableEntity>();
            
            try
            {
                var response = _updateHistoryTable.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{hostname}'",
                    maxPerPage: maxRecords);
                
                await foreach (var entity in response)
                {
                    history.Add(entity);
                    if (history.Count >= maxRecords) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get update history for hostname {hostname}");
            }
            
            return history;
        }

    }
}