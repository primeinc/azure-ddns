using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Company.Function.Services
{
    public static class AuthenticationHelper
    {
        public static (string? oid, string? upn) GetUserFromHeaders(HttpRequestData req, ILogger log)
        {
            log.LogInformation("[AUDIT-AUTH] Attempting to extract user from X-MS-CLIENT-PRINCIPAL header");
            
            // Log all headers for debugging
            foreach (var header in req.Headers)
            {
                var headerValue = header.Value?.FirstOrDefault();
                if (header.Key.StartsWith("X-MS-"))
                {
                    log.LogInformation($"[AUDIT-HEADER] {header.Key}: {(string.IsNullOrEmpty(headerValue) ? "EMPTY" : headerValue.Substring(0, Math.Min(50, headerValue.Length)))}...");
                }
            }
            
            if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals))
            {
                log.LogWarning("[AUDIT-AUTH] X-MS-CLIENT-PRINCIPAL header not found");
                
                // Try direct headers as fallback
                req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-ID", out var idVals);
                req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-NAME", out var nameVals);
                return (idVals?.FirstOrDefault(), nameVals?.FirstOrDefault());
            }

            var raw = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(raw))
            {
                log.LogWarning("[AUDIT-AUTH] X-MS-CLIENT-PRINCIPAL header is empty");
                return (null, null);
            }

            log.LogInformation($"[AUDIT-AUTH] X-MS-CLIENT-PRINCIPAL header found with length: {raw.Length}");

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                log.LogInformation($"[AUDIT-AUTH] Decoded principal JSON length: {json.Length}");
                
                using var doc = JsonDocument.Parse(json);
                string? oid = null, upn = null;

                if (doc.RootElement.TryGetProperty("claims", out var claims))
                {
                    log.LogInformation($"[AUDIT-AUTH] Found {claims.GetArrayLength()} claims in principal");
                    
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (claim.TryGetProperty("typ", out var typ) && claim.TryGetProperty("val", out var val))
                        {
                            var typeStr = typ.GetString();
                            var valStr = val.GetString();
                            log.LogDebug($"[AUDIT-CLAIM] Type: {typeStr}, Value: {valStr?.Substring(0, Math.Min(20, valStr?.Length ?? 0))}...");
                            
                            if (typeStr == "http://schemas.microsoft.com/identity/claims/objectidentifier") 
                            {
                                oid = valStr;
                                log.LogInformation($"[AUDIT-AUTH] Found OID: {oid}");
                            }
                            if (typeStr == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn") 
                            {
                                upn = valStr;
                                log.LogInformation($"[AUDIT-AUTH] Found UPN: {upn}");
                            }
                        }
                    }
                }
                else
                {
                    log.LogWarning("[AUDIT-AUTH] No claims found in X-MS-CLIENT-PRINCIPAL");
                }
                
                // If we still don't have UPN, try the direct header as last resort
                if (string.IsNullOrEmpty(upn))
                {
                    req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL-NAME", out var nameVals);
                    upn = nameVals?.FirstOrDefault();
                    if (!string.IsNullOrEmpty(upn))
                    {
                        log.LogInformation($"[AUDIT-AUTH] Got UPN from X-MS-CLIENT-PRINCIPAL-NAME header: {upn}");
                    }
                }
                
                log.LogInformation($"[AUDIT-AUTH-SUCCESS] EasyAuth principal extracted - oid={oid ?? "NULL"} upn={upn ?? "NULL"}");
                return (oid, upn);
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"[AUDIT-AUTH-ERROR] Failed to parse X-MS-CLIENT-PRINCIPAL. Raw value: {raw.Substring(0, Math.Min(100, raw.Length))}...");
                return (null, null);
            }
        }

        public static HashSet<string> GetUserRoles(HttpRequestData req, ILogger log)
        {
            log.LogInformation("[AUDIT-ROLES] Extracting user roles from X-MS-CLIENT-PRINCIPAL header");
            
            if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals))
            {
                log.LogWarning("[AUDIT-ROLES] X-MS-CLIENT-PRINCIPAL header not found");
                return new HashSet<string>();
            }

            var raw = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(raw))
            {
                log.LogWarning("[AUDIT-ROLES] X-MS-CLIENT-PRINCIPAL header is empty");
                return new HashSet<string>();
            }

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                using var doc = JsonDocument.Parse(json);
                var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (doc.RootElement.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (claim.TryGetProperty("typ", out var typ) && claim.TryGetProperty("val", out var val))
                        {
                            var typeStr = typ.GetString();
                            var valStr = val.GetString();
                            
                            // Check for roles claim (app-specific roles)
                            if (typeStr == "roles" || typeStr == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
                            {
                                if (!string.IsNullOrEmpty(valStr))
                                {
                                    roles.Add(valStr);
                                    log.LogInformation($"[AUDIT-ROLES] Found app role: {valStr}");
                                }
                            }
                        }
                    }
                }

                log.LogInformation($"[AUDIT-ROLES-SUCCESS] Extracted {roles.Count} app roles: {string.Join(", ", roles)}");
                return roles;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[AUDIT-ROLES-ERROR] Failed to parse roles from X-MS-CLIENT-PRINCIPAL");
                return new HashSet<string>();
            }
        }

        public static bool IsBootstrapAdmin(HttpRequestData req, ILogger log)
        {
            log.LogInformation("[AUDIT-BOOTSTRAP] Checking for bootstrap admin privileges");
            
            var (userId, userEmail) = GetUserFromHeaders(req, log);
            if (string.IsNullOrEmpty(userId))
            {
                log.LogWarning("[AUDIT-BOOTSTRAP] No user ID found, denying admin access");
                return false;
            }

            // Method 1: Check for tenant admin roles via wids claim (zero latency)
            var tenantAdminRoles = GetTenantAdminRoles(req, log);
            if (tenantAdminRoles.Any())
            {
                log.LogWarning($"[AUDIT-BOOTSTRAP-SUCCESS] User {userEmail} has tenant admin roles: {string.Join(", ", tenantAdminRoles)}");
                return true;
            }

            // Method 2: Domain-based fallback for specific domains
            if (!string.IsNullOrEmpty(userEmail))
            {
                var adminDomains = new[] { "@4pp.dev", "@yourdomain.com" };
                if (adminDomains.Any(domain => userEmail.EndsWith(domain, StringComparison.OrdinalIgnoreCase)))
                {
                    log.LogInformation($"[AUDIT-BOOTSTRAP-DOMAIN] User {userEmail} granted admin via domain membership");
                    return true;
                }
            }

            log.LogInformation($"[AUDIT-BOOTSTRAP-DENIED] User {userEmail} has no bootstrap admin privileges");
            return false;
        }

        public static HashSet<string> GetTenantAdminRoles(HttpRequestData req, ILogger log)
        {
            log.LogInformation("[AUDIT-TENANT-ROLES] Extracting tenant admin roles from wids claim");
            
            // Tenant admin role template IDs (immutable)
            var adminRoleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator",
                ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator", 
                ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
                ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
                ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator"
            };

            if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals))
            {
                log.LogWarning("[AUDIT-TENANT-ROLES] X-MS-CLIENT-PRINCIPAL header not found");
                return new HashSet<string>();
            }

            var raw = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(raw))
            {
                log.LogWarning("[AUDIT-TENANT-ROLES] X-MS-CLIENT-PRINCIPAL header is empty");
                return new HashSet<string>();
            }

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                using var doc = JsonDocument.Parse(json);
                var foundAdminRoles = new HashSet<string>();

                if (doc.RootElement.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (claim.TryGetProperty("typ", out var typ) && claim.TryGetProperty("val", out var val))
                        {
                            var typeStr = typ.GetString();
                            var valStr = val.GetString();
                            
                            // Check for wids claim (well-known IDs for tenant roles)
                            if (typeStr == "wids" && !string.IsNullOrEmpty(valStr))
                            {
                                if (adminRoleIds.TryGetValue(valStr, out var roleName))
                                {
                                    foundAdminRoles.Add(roleName);
                                    log.LogWarning($"[AUDIT-TENANT-ROLES] Found admin role: {roleName} ({valStr})");
                                }
                            }
                        }
                    }
                }

                log.LogInformation($"[AUDIT-TENANT-ROLES-SUCCESS] Found {foundAdminRoles.Count} tenant admin roles: {string.Join(", ", foundAdminRoles)}");
                return foundAdminRoles;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[AUDIT-TENANT-ROLES-ERROR] Failed to parse wids from X-MS-CLIENT-PRINCIPAL");
                return new HashSet<string>();
            }
        }
    }
}