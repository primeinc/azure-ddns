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
    }
}