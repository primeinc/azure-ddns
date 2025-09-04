using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Company.Function.Services
{
    /// <summary>
    /// Comprehensive telemetry service for structured logging and Application Insights tracking
    /// in the Azure DDNS project. Provides standardized event logging with consistent properties
    /// and proper correlation for monitoring and analytics.
    /// 
    /// USAGE EXAMPLES:
    /// 
    /// 1. DDNS Update Logging:
    /// <code>
    /// _telemetryHelper.LogDdnsUpdateAttempt(
    ///     req, 
    ///     hostname: "mydevice.ddns.example.com",
    ///     ipAddress: "203.0.113.42",
    ///     authMethod: "ApiKey",
    ///     resultCode: "good",
    ///     isSuccess: true,
    ///     correlationId: invocationId,
    ///     recordType: "A",
    ///     ttl: 60
    /// );
    /// </code>
    /// 
    /// 2. Authentication Event Logging:
    /// <code>
    /// _telemetryHelper.LogAuthenticationAttempt(
    ///     req,
    ///     authMethod: "AzureAD",
    ///     isSuccess: true,
    ///     username: "user@domain.com",
    ///     hostname: "mydevice.ddns.example.com",
    ///     correlationId: correlationId
    /// );
    /// </code>
    /// 
    /// 3. Hostname Management Logging:
    /// <code>
    /// _telemetryHelper.LogHostnameManagementEvent(
    ///     req,
    ///     eventType: "hostname_claimed",
    ///     hostname: "mydevice.ddns.example.com",
    ///     principalId: "12345678-1234-1234-1234-123456789012",
    ///     email: "user@domain.com",
    ///     isSuccess: true,
    ///     correlationId: correlationId
    /// );
    /// </code>
    /// 
    /// 4. Management Page Access:
    /// <code>
    /// _telemetryHelper.LogManagementPageAccess(
    ///     req,
    ///     hostname: "mydevice.ddns.example.com",
    ///     principalId: userPrincipalId,
    ///     email: userEmail,
    ///     accessType: "dashboard",
    ///     isAuthorized: true,
    ///     correlationId: correlationId,
    ///     responseTime: stopwatch.ElapsedMilliseconds
    /// );
    /// </code>
    /// 
    /// STRUCTURED LOGGING PROPERTIES:
    /// - All events use consistent 'ddns.*' property naming
    /// - Core properties: event_type, source_ip, user_agent, hostname, correlation_id, timestamp
    /// - Event-specific properties for detailed context
    /// - Boolean success/failure fields for filtering and metrics
    /// 
    /// APPLICATION INSIGHTS INTEGRATION:
    /// - Custom events with standardized naming (DdnsUpdateAttempt, AuthenticationAttempt, etc.)
    /// - Properties and metrics for dashboards and alerts
    /// - Correlation tracking for distributed tracing
    /// - Support for custom dimensions in Azure Monitor workbooks
    /// </summary>
    public class TelemetryHelper
    {
        private readonly ILogger<TelemetryHelper> _logger;
        private readonly TelemetryClient _telemetryClient;

        public TelemetryHelper(ILogger<TelemetryHelper> logger, TelemetryClient telemetryClient)
        {
            _logger = logger;
            _telemetryClient = telemetryClient;
        }

        /// <summary>
        /// Logs a DDNS update attempt with comprehensive context and metrics.
        /// Tracks both successful updates and failures with detailed properties for analytics.
        /// </summary>
        /// <param name="req">HTTP request for extracting client information</param>
        /// <param name="hostname">Target hostname being updated</param>
        /// <param name="ipAddress">IP address being set (null if detection failed)</param>
        /// <param name="authMethod">Authentication method used (ApiKey, BasicAuth, None)</param>
        /// <param name="resultCode">DynDNS2 protocol result code (good, nochg, badauth, nohost, etc.)</param>
        /// <param name="isSuccess">Whether the update was successful</param>
        /// <param name="correlationId">Correlation ID for request tracking</param>
        /// <param name="errorMessage">Error message if update failed</param>
        /// <param name="recordType">DNS record type (A, AAAA, etc.)</param>
        /// <param name="ttl">TTL value set on the DNS record</param>
        public void LogDdnsUpdateAttempt(
            HttpRequest req,
            string? hostname = null,
            string? ipAddress = null,
            string authMethod = "None",
            string resultCode = "unknown",
            bool isSuccess = false,
            string? correlationId = null,
            string? errorMessage = null,
            string recordType = "A",
            int? ttl = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var sourceIp = ExtractSourceIp(req);
            var userAgent = ExtractUserAgent(req);
            correlationId ??= GenerateCorrelationId();

            // Structured logging with scoped properties
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["ddns.event_type"] = "ddns_update",
                ["ddns.hostname"] = hostname ?? "unknown",
                ["ddns.source_ip"] = sourceIp,
                ["ddns.user_agent"] = userAgent,
                ["ddns.auth_method"] = authMethod,
                ["ddns.result_code"] = resultCode,
                ["ddns.is_success"] = isSuccess,
                ["ddns.correlation_id"] = correlationId,
                ["ddns.timestamp"] = timestamp,
                ["ddns.target_ip"] = ipAddress ?? "unknown",
                ["ddns.record_type"] = recordType ?? "A",
                ["ddns.ttl"] = ttl ?? (object)"none",
                ["ddns.error_message"] = errorMessage ?? string.Empty
            });

            var logLevel = isSuccess ? LogLevel.Information : LogLevel.Warning;
            var message = $"DDNS update attempt for hostname '{hostname}' from {sourceIp} resulted in '{resultCode}'";
            
            if (!isSuccess && !string.IsNullOrEmpty(errorMessage))
            {
                message += $" - Error: {errorMessage}";
            }

            _logger.Log(logLevel, message);

            // Application Insights custom event
            var eventProperties = new Dictionary<string, string>
            {
                ["hostname"] = hostname ?? "unknown",
                ["sourceIp"] = sourceIp,
                ["userAgent"] = userAgent,
                ["authMethod"] = authMethod,
                ["resultCode"] = resultCode,
                ["correlationId"] = correlationId,
                ["targetIp"] = ipAddress ?? "unknown",
                ["recordType"] = recordType ?? "A",
                ["errorMessage"] = errorMessage ?? string.Empty
            };

            var eventMetrics = new Dictionary<string, double>
            {
                ["success"] = isSuccess ? 1 : 0,
                ["responseTime"] = 0 // Can be set by caller if timing is tracked
            };

            if (ttl.HasValue)
            {
                eventMetrics["ttl"] = ttl.Value;
            }

            _telemetryClient.TrackEvent("DdnsUpdateAttempt", eventProperties, eventMetrics);
        }

        /// <summary>
        /// Logs authentication attempts for both API key and Azure AD authentication.
        /// Tracks security-related events for monitoring and audit purposes.
        /// </summary>
        /// <param name="req">HTTP request for extracting client information</param>
        /// <param name="authMethod">Authentication method (ApiKey, AzureAD, BasicAuth)</param>
        /// <param name="isSuccess">Whether authentication was successful</param>
        /// <param name="username">Username or principal ID</param>
        /// <param name="hostname">Hostname being accessed (if applicable)</param>
        /// <param name="correlationId">Correlation ID for request tracking</param>
        /// <param name="errorMessage">Error message if authentication failed</param>
        /// <param name="apiKeyHash">First 8 characters of API key hash (for audit trail)</param>
        public void LogAuthenticationAttempt(
            HttpRequest req,
            string authMethod,
            bool isSuccess,
            string? username = null,
            string? hostname = null,
            string? correlationId = null,
            string? errorMessage = null,
            string? apiKeyHash = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var sourceIp = ExtractSourceIp(req);
            var userAgent = ExtractUserAgent(req);
            correlationId ??= GenerateCorrelationId();

            // Structured logging with scoped properties
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["ddns.event_type"] = "authentication",
                ["ddns.auth_method"] = authMethod,
                ["ddns.is_success"] = isSuccess,
                ["ddns.username"] = username ?? "unknown",
                ["ddns.hostname"] = hostname ?? "none",
                ["ddns.source_ip"] = sourceIp,
                ["ddns.user_agent"] = userAgent,
                ["ddns.correlation_id"] = correlationId,
                ["ddns.timestamp"] = timestamp,
                ["ddns.api_key_hash_prefix"] = apiKeyHash?.Substring(0, Math.Min(8, apiKeyHash.Length)) ?? "none",
                ["ddns.error_message"] = errorMessage ?? string.Empty
            });

            var logLevel = isSuccess ? LogLevel.Information : LogLevel.Warning;
            var message = $"{authMethod} authentication attempt by '{username}' from {sourceIp} - {(isSuccess ? "SUCCESS" : "FAILED")}";
            
            if (!isSuccess && !string.IsNullOrEmpty(errorMessage))
            {
                message += $" - {errorMessage}";
            }

            _logger.Log(logLevel, message);

            // Application Insights custom event
            var eventProperties = new Dictionary<string, string>
            {
                ["authMethod"] = authMethod,
                ["username"] = username ?? "unknown",
                ["hostname"] = hostname ?? "none",
                ["sourceIp"] = sourceIp,
                ["userAgent"] = userAgent,
                ["correlationId"] = correlationId,
                ["errorMessage"] = errorMessage ?? string.Empty,
                ["apiKeyHashPrefix"] = apiKeyHash?.Substring(0, Math.Min(8, apiKeyHash.Length)) ?? "none"
            };

            var eventMetrics = new Dictionary<string, double>
            {
                ["success"] = isSuccess ? 1 : 0
            };

            _telemetryClient.TrackEvent("AuthenticationAttempt", eventProperties, eventMetrics);
        }

        /// <summary>
        /// Logs hostname management events such as claims, API key generation, and revocation.
        /// Tracks the lifecycle of hostname ownership and API key management.
        /// </summary>
        /// <param name="req">HTTP request for extracting client information</param>
        /// <param name="eventType">Type of management event (claim, api_key_generated, api_key_revoked, etc.)</param>
        /// <param name="hostname">Hostname being managed</param>
        /// <param name="principalId">Azure AD principal ID of the user</param>
        /// <param name="email">User's email address</param>
        /// <param name="isSuccess">Whether the operation was successful</param>
        /// <param name="correlationId">Correlation ID for request tracking</param>
        /// <param name="errorMessage">Error message if operation failed</param>
        /// <param name="apiKeyHash">API key hash prefix for key-related events</param>
        /// <param name="additionalProperties">Additional custom properties</param>
        public void LogHostnameManagementEvent(
            HttpRequest req,
            string eventType,
            string hostname,
            string? principalId = null,
            string? email = null,
            bool isSuccess = true,
            string? correlationId = null,
            string? errorMessage = null,
            string? apiKeyHash = null,
            Dictionary<string, string>? additionalProperties = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var sourceIp = ExtractSourceIp(req);
            var userAgent = ExtractUserAgent(req);
            correlationId ??= GenerateCorrelationId();

            // Structured logging with scoped properties
            var scopeProperties = new Dictionary<string, object>
            {
                ["ddns.event_type"] = "hostname_management",
                ["ddns.management_event"] = eventType,
                ["ddns.hostname"] = hostname,
                ["ddns.principal_id"] = principalId ?? "unknown",
                ["ddns.email"] = email ?? "unknown",
                ["ddns.is_success"] = isSuccess,
                ["ddns.source_ip"] = sourceIp,
                ["ddns.user_agent"] = userAgent,
                ["ddns.correlation_id"] = correlationId,
                ["ddns.timestamp"] = timestamp,
                ["ddns.api_key_hash_prefix"] = apiKeyHash?.Substring(0, Math.Min(8, apiKeyHash.Length)) ?? "none",
                ["ddns.error_message"] = errorMessage ?? string.Empty
            };

            // Add additional properties if provided
            if (additionalProperties != null)
            {
                foreach (var prop in additionalProperties)
                {
                    scopeProperties[$"ddns.{prop.Key}"] = prop.Value;
                }
            }

            using var scope = _logger.BeginScope(scopeProperties);

            var logLevel = isSuccess ? LogLevel.Information : LogLevel.Error;
            var message = $"Hostname management event '{eventType}' for '{hostname}' by '{principalId}' ({email}) from {sourceIp} - {(isSuccess ? "SUCCESS" : "FAILED")}";
            
            if (!isSuccess && !string.IsNullOrEmpty(errorMessage))
            {
                message += $" - {errorMessage}";
            }

            _logger.Log(logLevel, message);

            // Application Insights custom event
            var eventProperties = new Dictionary<string, string>
            {
                ["managementEvent"] = eventType,
                ["hostname"] = hostname,
                ["principalId"] = principalId ?? "unknown",
                ["email"] = email ?? "unknown",
                ["sourceIp"] = sourceIp,
                ["userAgent"] = userAgent,
                ["correlationId"] = correlationId,
                ["errorMessage"] = errorMessage ?? string.Empty,
                ["apiKeyHashPrefix"] = apiKeyHash?.Substring(0, Math.Min(8, apiKeyHash.Length)) ?? "none"
            };

            // Add additional properties to App Insights event
            if (additionalProperties != null)
            {
                foreach (var prop in additionalProperties)
                {
                    eventProperties[prop.Key] = prop.Value;
                }
            }

            var eventMetrics = new Dictionary<string, double>
            {
                ["success"] = isSuccess ? 1 : 0
            };

            _telemetryClient.TrackEvent("HostnameManagementEvent", eventProperties, eventMetrics);
        }

        /// <summary>
        /// Logs management page access events for tracking user interaction with the DDNS dashboard.
        /// Helps understand usage patterns and identify potential security issues.
        /// </summary>
        /// <param name="req">HTTP request for extracting client information</param>
        /// <param name="hostname">Hostname being accessed in the management interface</param>
        /// <param name="principalId">Azure AD principal ID of the user</param>
        /// <param name="email">User's email address</param>
        /// <param name="accessType">Type of access (dashboard, api_key_view, history, etc.)</param>
        /// <param name="isAuthorized">Whether the user is authorized to access this hostname</param>
        /// <param name="correlationId">Correlation ID for request tracking</param>
        /// <param name="responseTime">Page response time in milliseconds</param>
        /// <param name="additionalProperties">Additional custom properties</param>
        public void LogManagementPageAccess(
            HttpRequest req,
            string hostname,
            string? principalId = null,
            string? email = null,
            string accessType = "dashboard",
            bool isAuthorized = true,
            string? correlationId = null,
            double? responseTime = null,
            Dictionary<string, string>? additionalProperties = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var sourceIp = ExtractSourceIp(req);
            var userAgent = ExtractUserAgent(req);
            correlationId ??= GenerateCorrelationId();

            // Structured logging with scoped properties
            var scopeProperties = new Dictionary<string, object>
            {
                ["ddns.event_type"] = "management_page_access",
                ["ddns.access_type"] = accessType,
                ["ddns.hostname"] = hostname,
                ["ddns.principal_id"] = principalId ?? "anonymous",
                ["ddns.email"] = email ?? "unknown",
                ["ddns.is_authorized"] = isAuthorized,
                ["ddns.source_ip"] = sourceIp,
                ["ddns.user_agent"] = userAgent,
                ["ddns.correlation_id"] = correlationId,
                ["ddns.timestamp"] = timestamp,
                ["ddns.response_time"] = responseTime ?? 0
            };

            // Add additional properties if provided
            if (additionalProperties != null)
            {
                foreach (var prop in additionalProperties)
                {
                    scopeProperties[$"ddns.{prop.Key}"] = prop.Value;
                }
            }

            using var scope = _logger.BeginScope(scopeProperties);

            var logLevel = isAuthorized ? LogLevel.Information : LogLevel.Warning;
            var message = $"Management page access '{accessType}' for hostname '{hostname}' by '{principalId}' ({email}) from {sourceIp} - {(isAuthorized ? "AUTHORIZED" : "UNAUTHORIZED")}";

            _logger.Log(logLevel, message);

            // Application Insights custom event
            var eventProperties = new Dictionary<string, string>
            {
                ["accessType"] = accessType,
                ["hostname"] = hostname,
                ["principalId"] = principalId ?? "anonymous",
                ["email"] = email ?? "unknown",
                ["sourceIp"] = sourceIp,
                ["userAgent"] = userAgent,
                ["correlationId"] = correlationId
            };

            // Add additional properties to App Insights event
            if (additionalProperties != null)
            {
                foreach (var prop in additionalProperties)
                {
                    eventProperties[prop.Key] = prop.Value;
                }
            }

            var eventMetrics = new Dictionary<string, double>
            {
                ["authorized"] = isAuthorized ? 1 : 0
            };

            if (responseTime.HasValue)
            {
                eventMetrics["responseTimeMs"] = responseTime.Value;
            }

            _telemetryClient.TrackEvent("ManagementPageAccess", eventProperties, eventMetrics);
        }

        /// <summary>
        /// Logs general system events that don't fit into other specific categories.
        /// Useful for tracking application lifecycle, configuration changes, etc.
        /// </summary>
        /// <param name="eventType">Type of system event</param>
        /// <param name="message">Human-readable message</param>
        /// <param name="isSuccess">Whether the operation was successful</param>
        /// <param name="properties">Custom properties for the event</param>
        /// <param name="metrics">Custom metrics for the event</param>
        /// <param name="correlationId">Correlation ID for request tracking</param>
        public void LogSystemEvent(
            string eventType,
            string message,
            bool isSuccess = true,
            Dictionary<string, string>? properties = null,
            Dictionary<string, double>? metrics = null,
            string? correlationId = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            correlationId ??= GenerateCorrelationId();

            // Structured logging with scoped properties
            var scopeProperties = new Dictionary<string, object>
            {
                ["ddns.event_type"] = "system_event",
                ["ddns.system_event"] = eventType,
                ["ddns.is_success"] = isSuccess,
                ["ddns.correlation_id"] = correlationId,
                ["ddns.timestamp"] = timestamp
            };

            using var scope = _logger.BeginScope(scopeProperties);

            var logLevel = isSuccess ? LogLevel.Information : LogLevel.Error;
            _logger.Log(logLevel, $"System event '{eventType}': {message}");

            // Application Insights custom event
            var eventProperties = new Dictionary<string, string>
            {
                ["systemEvent"] = eventType,
                ["correlationId"] = correlationId,
                ["message"] = message
            };

            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    eventProperties[prop.Key] = prop.Value;
                }
            }

            var eventMetrics = new Dictionary<string, double>
            {
                ["success"] = isSuccess ? 1 : 0
            };

            if (metrics != null)
            {
                foreach (var metric in metrics)
                {
                    eventMetrics[metric.Key] = metric.Value;
                }
            }

            _telemetryClient.TrackEvent("SystemEvent", eventProperties, eventMetrics);
        }

        /// <summary>
        /// Extracts the real client IP address from HTTP request headers.
        /// Handles X-Forwarded-For, X-Real-IP, X-Azure-ClientIP and other proxy headers.
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <returns>Client IP address or "unknown" if not determinable</returns>
        private string ExtractSourceIp(HttpRequest req)
        {
            if (req == null) return "unknown";

            try
            {
                // Try Azure-specific headers first (most reliable in Azure environment)
                var azureClientIp = req.Headers["X-Azure-ClientIP"].FirstOrDefault();
                if (!string.IsNullOrEmpty(azureClientIp))
                {
                    return CleanIpAddress(azureClientIp);
                }

                // Try X-Forwarded-For (most common proxy header)
                var forwardedFor = req.Headers["X-Forwarded-For"].FirstOrDefault();
                if (!string.IsNullOrEmpty(forwardedFor))
                {
                    // Take the first IP in the chain (original client)
                    var firstIp = forwardedFor.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(firstIp))
                    {
                        return CleanIpAddress(firstIp);
                    }
                }

                // Try X-Real-IP
                var realIp = req.Headers["X-Real-IP"].FirstOrDefault();
                if (!string.IsNullOrEmpty(realIp))
                {
                    return CleanIpAddress(realIp);
                }

                // Try X-Original-For
                var originalFor = req.Headers["X-Original-For"].FirstOrDefault();
                if (!string.IsNullOrEmpty(originalFor))
                {
                    var firstIp = originalFor.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(firstIp))
                    {
                        return CleanIpAddress(firstIp);
                    }
                }

                // Fall back to connection remote IP
                var connectionIp = req.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                if (!string.IsNullOrEmpty(connectionIp))
                {
                    return CleanIpAddress(connectionIp);
                }

                return "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Cleans up IP address strings by removing brackets, port numbers, etc.
        /// </summary>
        /// <param name="ipAddress">Raw IP address string</param>
        /// <returns>Clean IP address</returns>
        private string CleanIpAddress(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) return "unknown";

            try
            {
                // Remove IPv6 brackets and port: [::1]:41380 -> ::1
                if (ipAddress.StartsWith("[") && ipAddress.Contains("]:"))
                {
                    var endBracket = ipAddress.IndexOf("]:");
                    return ipAddress.Substring(1, endBracket - 1);
                }
                
                // Remove brackets from IPv6: [::1] -> ::1
                if (ipAddress.StartsWith("[") && ipAddress.EndsWith("]"))
                {
                    return ipAddress.Substring(1, ipAddress.Length - 2);
                }
                
                // Remove port from IPv4: 1.2.3.4:80 -> 1.2.3.4
                if (ipAddress.Contains(':') && ipAddress.Split('.').Length == 4)
                {
                    return ipAddress.Substring(0, ipAddress.LastIndexOf(':'));
                }

                // Validate IP format
                if (IPAddress.TryParse(ipAddress, out _))
                {
                    return ipAddress;
                }

                return "invalid";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Extracts User-Agent header from HTTP request.
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <returns>User-Agent string or "unknown" if not present</returns>
        private string ExtractUserAgent(HttpRequest req)
        {
            if (req == null) return "unknown";

            try
            {
                var userAgent = req.Headers["User-Agent"].FirstOrDefault();
                return string.IsNullOrEmpty(userAgent) ? "unknown" : userAgent;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Generates a correlation ID for request tracking.
        /// </summary>
        /// <returns>8-character correlation ID</returns>
        private string GenerateCorrelationId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Determines if an IP address is private, loopback, or link-local.
        /// Useful for identifying when IP auto-detection might not work properly.
        /// </summary>
        /// <param name="ipAddress">IP address to check</param>
        /// <returns>True if the IP is private/internal</returns>
        public bool IsPrivateOrInternalIP(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;

            // Check for loopback
            if (IPAddress.IsLoopback(ip))
                return true;

            // Check for IPv4 private ranges
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10)
                    return true;

                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;

                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;

                // 127.0.0.0/8 (loopback)
                if (bytes[0] == 127)
                    return true;

                // 169.254.0.0/16 (link-local)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;
            }

            return false;
        }
    }
}