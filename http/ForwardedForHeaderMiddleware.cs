using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Azure.Functions.Worker.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Company.Function
{
    public class ForwardedForHeaderMiddleware : IFunctionsWorkerMiddleware
    {
        private readonly ILogger<ForwardedForHeaderMiddleware> _logger;

        public ForwardedForHeaderMiddleware(ILogger<ForwardedForHeaderMiddleware> logger)
        {
            _logger = logger;
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            await CorrectForwardedHeaders(context);
            await next(context);
        }

        private async Task CorrectForwardedHeaders(FunctionContext context)
        {
            try
            {
                var httpRequestData = await context.GetHttpRequestDataAsync();
                if (httpRequestData == null)
                {
                    return;
                }

                _logger.LogInformation("=== ForwardedForHeaderMiddleware START ===");
                
                // Log existing headers first
                _logger.LogInformation($"Existing headers in HttpRequestData:");
                foreach (var header in httpRequestData.Headers)
                {
                    _logger.LogInformation($"  {header.Key}: {string.Join(", ", header.Value)}");
                }

                // Get headers from binding context (where Azure actually puts them)
                if (context.BindingContext.BindingData.TryGetValue("Headers", out var headersObj))
                {
                    string? headersJson = headersObj?.ToString();
                    if (!string.IsNullOrEmpty(headersJson))
                    {
                        _logger.LogInformation($"Raw headers JSON from BindingData: {headersJson}");
                        
                        try
                        {
                            var headers = JsonSerializer.Deserialize<JsonElement>(headersJson);
                            
                            // Fix X-Forwarded-For header
                            if (headers.TryGetProperty("X-Forwarded-For", out var xffElement))
                            {
                                var xffValue = xffElement.GetString();
                                _logger.LogInformation($"Found X-Forwarded-For in BindingData: {xffValue}");
                                
                                if (!string.IsNullOrEmpty(xffValue))
                                {
                                    // Remove existing and re-add
                                    if (httpRequestData.Headers.Contains("X-Forwarded-For"))
                                    {
                                        httpRequestData.Headers.Remove("X-Forwarded-For");
                                        _logger.LogInformation($"Removed existing X-Forwarded-For header");
                                    }
                                    httpRequestData.Headers.Add("X-Forwarded-For", xffValue);
                                    _logger.LogInformation($"Added X-Forwarded-For header: {xffValue}");
                                }
                            }
                            else
                            {
                                _logger.LogInformation("X-Forwarded-For NOT found in BindingData");
                            }

                            // Also check for X-Azure-ClientIP (Azure Front Door specific)
                            if (headers.TryGetProperty("X-Azure-ClientIP", out var azureClientIpElement))
                            {
                                var azureClientIp = azureClientIpElement.GetString();
                                _logger.LogInformation($"Found X-Azure-ClientIP in BindingData: {azureClientIp}");
                                
                                if (!string.IsNullOrEmpty(azureClientIp) && !httpRequestData.Headers.Contains("X-Azure-ClientIP"))
                                {
                                    httpRequestData.Headers.Add("X-Azure-ClientIP", azureClientIp);
                                    _logger.LogInformation($"Added X-Azure-ClientIP header: {azureClientIp}");
                                }
                            }

                            // Check for X-Real-IP
                            if (headers.TryGetProperty("X-Real-IP", out var realIpElement))
                            {
                                var realIp = realIpElement.GetString();
                                _logger.LogInformation($"Found X-Real-IP in BindingData: {realIp}");
                                
                                if (!string.IsNullOrEmpty(realIp) && !httpRequestData.Headers.Contains("X-Real-IP"))
                                {
                                    httpRequestData.Headers.Add("X-Real-IP", realIp);
                                    _logger.LogInformation($"Added X-Real-IP header: {realIp}");
                                }
                            }

                            // Check for X-Original-For
                            if (headers.TryGetProperty("X-Original-For", out var originalForElement))
                            {
                                var originalFor = originalForElement.GetString();
                                _logger.LogInformation($"Found X-Original-For in BindingData: {originalFor}");
                                
                                if (!string.IsNullOrEmpty(originalFor) && !httpRequestData.Headers.Contains("X-Original-For"))
                                {
                                    httpRequestData.Headers.Add("X-Original-For", originalFor);
                                    _logger.LogInformation($"Added X-Original-For header: {originalFor}");
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError($"Failed to parse headers JSON: {ex.Message}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Headers JSON from BindingData is null or empty");
                    }
                }
                else
                {
                    _logger.LogWarning("No Headers found in BindingContext.BindingData");
                }

                // Log final headers
                _logger.LogInformation($"Final headers after middleware:");
                foreach (var header in httpRequestData.Headers)
                {
                    _logger.LogInformation($"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                
                _logger.LogInformation("=== ForwardedForHeaderMiddleware END ===");
            }
            catch (System.Exception ex)
            {
                _logger.LogError($"Error in ForwardedForHeaderMiddleware: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                // Don't throw - let the request continue even if middleware fails
            }
        }
    }
}