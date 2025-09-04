using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace http.Utilities
{
    /// <summary>
    /// Centralized utility for extracting and validating IP addresses from HTTP requests.
    /// Follows DRY principle - single source of truth for IP extraction logic.
    /// </summary>
    public static class IpAddressUtility
    {
        /// <summary>
        /// Extracts the source IP address from an HTTP request, checking multiple headers
        /// in priority order to handle various proxy and load balancer configurations.
        /// </summary>
        /// <param name="request">The HTTP request</param>
        /// <param name="logger">Optional logger for diagnostic output</param>
        /// <returns>The extracted IP address or null if none found</returns>
        public static string? ExtractSourceIp(HttpRequest request, ILogger? logger = null)
        {
            if (request == null)
                return null;

            // Priority order for IP extraction (most reliable first)
            
            // 1. Azure-specific header (when behind Azure Front Door or App Gateway)
            var azureClientIp = request.Headers["X-Azure-ClientIP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(azureClientIp))
            {
                logger?.LogTrace("IP extracted from X-Azure-ClientIP: {IP}", azureClientIp);
                return CleanIpAddress(azureClientIp);
            }

            // 2. X-Forwarded-For (standard proxy header, may contain multiple IPs)
            var forwardedFor = request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                // Take the first IP in the chain (original client)
                var firstIp = forwardedFor.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(firstIp))
                {
                    logger?.LogTrace("IP extracted from X-Forwarded-For: {IP}", firstIp);
                    return CleanIpAddress(firstIp);
                }
            }

            // 3. X-Real-IP (common with nginx)
            var realIp = request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(realIp))
            {
                logger?.LogTrace("IP extracted from X-Real-IP: {IP}", realIp);
                return CleanIpAddress(realIp);
            }

            // 4. CF-Connecting-IP (Cloudflare)
            var cfConnectingIp = request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(cfConnectingIp))
            {
                logger?.LogTrace("IP extracted from CF-Connecting-IP: {IP}", cfConnectingIp);
                return CleanIpAddress(cfConnectingIp);
            }

            // 5. Direct connection (no proxy)
            var connectionIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(connectionIp))
            {
                logger?.LogTrace("IP extracted from RemoteIpAddress: {IP}", connectionIp);
                return CleanIpAddress(connectionIp);
            }

            logger?.LogWarning("Unable to extract source IP from request");
            return null;
        }

        /// <summary>
        /// Cleans and normalizes an IP address string.
        /// Removes port numbers, handles IPv6 brackets, and validates format.
        /// </summary>
        /// <param name="ipAddress">The raw IP address string</param>
        /// <returns>Cleaned IP address or original if already clean</returns>
        public static string CleanIpAddress(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return ipAddress;

            // Remove port if present (e.g., "192.168.1.1:8080" -> "192.168.1.1")
            var colonIndex = ipAddress.LastIndexOf(':');
            if (colonIndex > 0)
            {
                // Check if it's IPv6 (contains multiple colons or brackets)
                if (ipAddress.Contains('[') || ipAddress.Count(c => c == ':') > 1)
                {
                    // IPv6 - remove brackets if present
                    ipAddress = ipAddress.Trim('[', ']');
                    
                    // Check for port in IPv6 (e.g., "[::1]:8080")
                    var bracketPortIndex = ipAddress.LastIndexOf("]:");
                    if (bracketPortIndex > 0)
                    {
                        ipAddress = ipAddress.Substring(0, bracketPortIndex);
                    }
                }
                else
                {
                    // IPv4 with port - remove port
                    ipAddress = ipAddress.Substring(0, colonIndex);
                }
            }

            return ipAddress.Trim();
        }

        /// <summary>
        /// Validates if a string is a valid IP address (IPv4 or IPv6).
        /// </summary>
        /// <param name="ipAddress">The IP address to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool IsValidIpAddress(string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return false;

            return IPAddress.TryParse(ipAddress, out _);
        }

        /// <summary>
        /// Determines if an IP address is from a private network (RFC 1918 for IPv4).
        /// </summary>
        /// <param name="ipAddress">The IP address to check</param>
        /// <returns>True if private, false if public or invalid</returns>
        public static bool IsPrivateIpAddress(string? ipAddress)
        {
            if (!IsValidIpAddress(ipAddress))
                return false;

            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;

            // IPv6 loopback or link-local
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal;
            }

            // IPv4 private ranges
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

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

            return false;
        }

        /// <summary>
        /// Gets diagnostic information about IP extraction from a request.
        /// Useful for debugging proxy configurations.
        /// </summary>
        /// <param name="request">The HTTP request</param>
        /// <returns>Diagnostic string with all IP-related headers</returns>
        public static string GetIpDiagnostics(HttpRequest request)
        {
            if (request == null)
                return "Request is null";

            var diagnostics = new System.Text.StringBuilder();
            diagnostics.AppendLine("IP Address Diagnostics:");
            diagnostics.AppendLine($"  RemoteIpAddress: {request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "null"}");
            diagnostics.AppendLine($"  X-Azure-ClientIP: {request.Headers["X-Azure-ClientIP"].FirstOrDefault() ?? "null"}");
            diagnostics.AppendLine($"  X-Forwarded-For: {request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "null"}");
            diagnostics.AppendLine($"  X-Real-IP: {request.Headers["X-Real-IP"].FirstOrDefault() ?? "null"}");
            diagnostics.AppendLine($"  CF-Connecting-IP: {request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? "null"}");
            diagnostics.AppendLine($"  Extracted IP: {ExtractSourceIp(request) ?? "null"}");

            return diagnostics.ToString();
        }
    }
}