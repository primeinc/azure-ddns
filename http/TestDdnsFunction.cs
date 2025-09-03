using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Function
{
    public class TestDdnsFunction
    {
        private readonly ILogger<TestDdnsFunction> _logger;

        public TestDdnsFunction(ILogger<TestDdnsFunction> logger)
        {
            _logger = logger;
            _logger.LogInformation("====== TestDdnsFunction CONSTRUCTOR called ======");
        }

        [Function("testddns")]
        public IActionResult Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "nic/test")] HttpRequest req)
        {
            var invocationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            
            _logger.LogInformation("====================================================");
            _logger.LogInformation($"[{invocationId}] TEST DDNS FUNCTION INVOKED");
            _logger.LogInformation($"[{invocationId}] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
            
            try
            {
                // Check for Basic Auth header (DynDNS2 protocol)
                var authHeader = req.Headers["Authorization"].FirstOrDefault();
                _logger.LogInformation($"[{invocationId}] Authorization header present: {!string.IsNullOrEmpty(authHeader)}");
                
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Basic "))
                {
                    try
                    {
                        var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                        var decodedBytes = Convert.FromBase64String(encodedCredentials);
                        var credentials = Encoding.UTF8.GetString(decodedBytes);
                        var parts = credentials.Split(':');
                        
                        _logger.LogInformation($"[{invocationId}] Basic Auth decoded - Username: {parts[0]}");
                        _logger.LogInformation($"[{invocationId}] Credential parts count: {parts.Length}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[{invocationId}] Error decoding Basic Auth: {ex.Message}");
                    }
                }
                
                // Log query parameters (DynDNS2 parameters)
                var hostname = req.Query["hostname"].FirstOrDefault();
                var myip = req.Query["myip"].FirstOrDefault();
                
                _logger.LogInformation($"[{invocationId}] Query Parameters:");
                _logger.LogInformation($"[{invocationId}]   hostname = {hostname ?? "null"}");
                _logger.LogInformation($"[{invocationId}]   myip = {myip ?? "null"}");
                
                // Log client IP for auto-detection testing
                var clientIp = req.HttpContext.Connection.RemoteIpAddress?.ToString();
                var forwardedFor = req.Headers["X-Forwarded-For"].FirstOrDefault();
                
                _logger.LogInformation($"[{invocationId}] Client IP Detection:");
                _logger.LogInformation($"[{invocationId}]   RemoteIpAddress = {clientIp ?? "null"}");
                _logger.LogInformation($"[{invocationId}]   X-Forwarded-For = {forwardedFor ?? "null"}");
                
                // Simulate DynDNS2 response
                string response;
                if (string.IsNullOrEmpty(authHeader))
                {
                    _logger.LogWarning($"[{invocationId}] No authorization provided - returning badauth");
                    response = "badauth";
                }
                else if (string.IsNullOrEmpty(hostname))
                {
                    _logger.LogWarning($"[{invocationId}] No hostname provided - returning notfqdn");
                    response = "notfqdn";
                }
                else
                {
                    _logger.LogInformation($"[{invocationId}] Would update {hostname} to IP {myip ?? clientIp ?? "unknown"}");
                    response = "good";
                }
                
                _logger.LogInformation($"[{invocationId}] Returning DynDNS2 response: {response}");
                _logger.LogInformation($"[{invocationId}] TEST DDNS FUNCTION COMPLETED");
                _logger.LogInformation("====================================================");
                
                // Console output for visibility
                Console.WriteLine($"[CONSOLE] TestDdns {invocationId} returned: {response}");
                
                // Return plain text response as per DynDNS2 protocol
                return new ContentResult
                {
                    Content = response,
                    ContentType = "text/plain",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{invocationId}] Unhandled exception: {ex}");
                _logger.LogError($"[{invocationId}] Stack trace: {ex.StackTrace}");
                Console.WriteLine($"[CONSOLE] TestDdns {invocationId} ERROR: {ex.Message}");
                
                return new ContentResult
                {
                    Content = "911", // DynDNS2 error code
                    ContentType = "text/plain",
                    StatusCode = 500
                };
            }
        }
    }
}