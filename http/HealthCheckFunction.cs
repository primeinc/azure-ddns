using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Text.Json;

namespace Company.Function
{
    public class HealthCheckFunction
    {
        private readonly ILogger<HealthCheckFunction> _logger;
        private static int _invocationCount = 0;

        public HealthCheckFunction(ILogger<HealthCheckFunction> logger)
        {
            _logger = logger;
            _logger.LogInformation("====== HealthCheckFunction CONSTRUCTOR called ======");
        }

        [Function("health")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
        {
            var invocationId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var invocationNumber = System.Threading.Interlocked.Increment(ref _invocationCount);
            
            _logger.LogInformation("====================================================");
            _logger.LogInformation($"[{invocationId}] HEALTH CHECK FUNCTION INVOKED");
            _logger.LogInformation($"[{invocationId}] Invocation Number: {invocationNumber}");
            _logger.LogInformation($"[{invocationId}] Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
            _logger.LogInformation($"[{invocationId}] Method: {req.Method}");
            _logger.LogInformation($"[{invocationId}] Path: {req.Path}");
            _logger.LogInformation($"[{invocationId}] QueryString: {req.QueryString}");
            
            // Log headers (excluding sensitive ones)
            _logger.LogInformation($"[{invocationId}] === Request Headers ===");
            foreach (var header in req.Headers)
            {
                if (!header.Key.Contains("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Contains("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"[{invocationId}] Header: {header.Key} = {string.Join(",", header.Value.ToArray())}");
                }
            }

            // Simulate some processing
            _logger.LogInformation($"[{invocationId}] Processing health check...");
            await Task.Delay(100); // Simulate work
            
            var response = new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                invocationId = invocationId,
                invocationCount = invocationNumber,
                environment = new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.ToString(),
                    processorCount = Environment.ProcessorCount,
                    is64Bit = Environment.Is64BitOperatingSystem,
                    dotnetVersion = Environment.Version.ToString()
                },
                azure = new
                {
                    functionAppName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "local",
                    region = Environment.GetEnvironmentVariable("REGION_NAME") ?? "local",
                    subscriptionId = Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME")?.Split('+')[0] ?? "local",
                    resourceGroup = Environment.GetEnvironmentVariable("WEBSITE_RESOURCE_GROUP") ?? "local"
                }
            };

            var jsonResponse = JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            _logger.LogInformation($"[{invocationId}] Health check completed successfully");
            _logger.LogInformation($"[{invocationId}] Response: {jsonResponse}");
            _logger.LogInformation($"[{invocationId}] HEALTH CHECK FUNCTION COMPLETED");
            _logger.LogInformation("====================================================");
            
            // Also write to console for visibility
            Console.WriteLine($"[CONSOLE] Health check {invocationId} completed at {DateTime.UtcNow:HH:mm:ss.fff}");
            
            return new OkObjectResult(response);
        }
    }
}