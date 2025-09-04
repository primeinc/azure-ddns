using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text.Json;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Identity;
using Azure.Core;
using System.Net;

namespace Company.Function
{
    public class MonitoringTestFunction
    {
        private readonly ILogger<MonitoringTestFunction> _logger;
        private readonly TelemetryHelper _telemetryHelper;
        private readonly LogsQueryClient _logsQueryClient;

        public MonitoringTestFunction(
            ILogger<MonitoringTestFunction> logger,
            TelemetryHelper telemetryHelper,
            LogsQueryClient logsQueryClient)
        {
            _logger = logger;
            _telemetryHelper = telemetryHelper;
            _logsQueryClient = logsQueryClient;
        }

        public class MonitoringTestRequest
        {
            public string TestType { get; set; } = "quick"; // "quick" or "full"
            public int WaitTimeoutSeconds { get; set; } = 300; // 5 minutes default
            public string[] Scenarios { get; set; } = ["auth_failure", "auth_success", "geo_anomaly"];
        }

        public class MonitoringTestResult
        {
            public string Status { get; set; } = "";
            public List<TestScenarioResult> Results { get; set; } = new();
            public TestSummary Summary { get; set; } = new();
        }

        public class TestScenarioResult
        {
            public string Scenario { get; set; } = "";
            public bool TelemetryLogged { get; set; }
            public bool ApplicationInsightsValidated { get; set; }
            public bool SentinelRuleWouldTrigger { get; set; }
            public string Details { get; set; } = "";
            public string? ErrorMessage { get; set; }
        }

        public class TestSummary
        {
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Total { get; set; }
        }

        [Function("TestMonitoring")]
        public async Task<HttpResponseData> TestMonitoring(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            var startTime = DateTimeOffset.UtcNow;
            var testId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                _logger.LogInformation($"[MONITORING-TEST-{testId}] Starting monitoring validation test at {startTime}");

                // Get user from EasyAuth header for authentication
                var (userId, userEmail) = GetUser(req, _logger);

                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning($"[MONITORING-TEST-{testId}] Unauthenticated request - Azure AD authentication required");
                    var authResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await authResponse.WriteStringAsync("Azure AD authentication required for monitoring tests");
                    return authResponse;
                }

                _logger.LogInformation($"[MONITORING-TEST-{testId}] Authenticated user: {userEmail} ({userId})");

                // Parse request
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var testRequest = JsonSerializer.Deserialize<MonitoringTestRequest>(requestBody) ?? new MonitoringTestRequest();

                _logger.LogInformation($"[MONITORING-TEST-{testId}] Test configuration - Type: {testRequest.TestType}, Timeout: {testRequest.WaitTimeoutSeconds}s, Scenarios: [{string.Join(", ", testRequest.Scenarios)}]");

                var results = new List<TestScenarioResult>();

                // Execute test scenarios
                foreach (var scenario in testRequest.Scenarios)
                {
                    var result = await ExecuteTestScenario(scenario, testId, startTime, req);
                    results.Add(result);
                }

                // Wait for telemetry ingestion
                _logger.LogInformation($"[MONITORING-TEST-{testId}] Waiting {testRequest.WaitTimeoutSeconds}s for telemetry ingestion...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(testRequest.WaitTimeoutSeconds, 30))); // Start with short delay

                // Get Application Insights workspace ID from environment
                var workspaceId = Environment.GetEnvironmentVariable("LOG_ANALYTICS_WORKSPACE_ID");
                if (string.IsNullOrEmpty(workspaceId))
                {
                    throw new InvalidOperationException("LOG_ANALYTICS_WORKSPACE_ID environment variable not configured");
                }

                // Validate telemetry in Application Insights
                var validationResults = await ValidateTelemetryInApplicationInsights(workspaceId, testId, startTime, testRequest.WaitTimeoutSeconds);

                // Update results with validation outcomes
                for (int i = 0; i < results.Count && i < validationResults.Count; i++)
                {
                    results[i].ApplicationInsightsValidated = validationResults[i].ApplicationInsightsValidated;
                    results[i].SentinelRuleWouldTrigger = validationResults[i].SentinelRuleWouldTrigger;
                    if (!string.IsNullOrEmpty(validationResults[i].Details))
                    {
                        results[i].Details += $" | {validationResults[i].Details}";
                    }
                    if (!string.IsNullOrEmpty(validationResults[i].ErrorMessage))
                    {
                        results[i].ErrorMessage = validationResults[i].ErrorMessage;
                    }
                }

                // Calculate summary
                var summary = new TestSummary
                {
                    Total = results.Count,
                    Passed = results.Count(r => r.TelemetryLogged && r.ApplicationInsightsValidated),
                    Failed = results.Count(r => !r.TelemetryLogged || !r.ApplicationInsightsValidated)
                };

                var finalResult = new MonitoringTestResult
                {
                    Status = summary.Failed == 0 ? "completed_success" : "completed_with_failures",
                    Results = results,
                    Summary = summary
                };

                _logger.LogInformation($"[MONITORING-TEST-{testId}] Test completed - {summary.Passed}/{summary.Total} scenarios passed");

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true }));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[MONITORING-TEST-{testId}] Test failed with exception");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Test failed: {ex.Message}");
                return errorResponse;
            }
        }

        private async Task<TestScenarioResult> ExecuteTestScenario(string scenario, string testId, DateTimeOffset startTime, HttpRequestData req)
        {
            var result = new TestScenarioResult { Scenario = scenario, TelemetryLogged = true }; // Assume success unless exception

            try
            {
                switch (scenario.ToLowerInvariant())
                {
                    case "auth_failure":
                        {
                            // Create synthetic telemetry directly using structured logging
                            using var scope1 = _logger.BeginScope(new Dictionary<string, object>
                            {
                                ["ddns.event_type"] = "ddns_update",
                                ["ddns.hostname"] = $"test-{testId}.ddns.test.dev",
                                ["ddns.source_ip"] = "203.0.113.42",
                                ["ddns.user_agent"] = "test-monitoring-client/1.0",
                                ["ddns.auth_method"] = "ApiKey",
                                ["ddns.result_code"] = "badauth",
                                ["ddns.is_success"] = false,
                                ["ddns.correlation_id"] = testId,
                                ["ddns.timestamp"] = startTime,
                                ["ddns.target_ip"] = "198.51.100.10",
                                ["ddns.record_type"] = "A",
                                ["ddns.ttl"] = "none",
                                ["ddns.error_message"] = $"Authentication failed - test scenario {testId}",
                                ["testId"] = testId // For KQL filtering
                            });
                            _logger.LogWarning("DDNS update attempt for hostname test-{TestId}.ddns.test.dev from 203.0.113.42 resulted in badauth - Authentication failed - test scenario {TestId}", testId, testId);
                            result.Details = "Simulated authentication failure";
                            break;
                        }

                    case "auth_success":
                        {
                            using var scope2 = _logger.BeginScope(new Dictionary<string, object>
                            {
                                ["ddns.event_type"] = "ddns_update",
                                ["ddns.hostname"] = $"test-{testId}.ddns.test.dev",
                                ["ddns.source_ip"] = "203.0.113.42",
                                ["ddns.user_agent"] = "test-monitoring-client/1.0",
                                ["ddns.auth_method"] = "ApiKey",
                                ["ddns.result_code"] = "good",
                                ["ddns.is_success"] = true,
                                ["ddns.correlation_id"] = testId,
                                ["ddns.timestamp"] = startTime,
                                ["ddns.target_ip"] = "198.51.100.10",
                                ["ddns.record_type"] = "A",
                                ["ddns.ttl"] = "none",
                                ["ddns.error_message"] = "",
                                ["testId"] = testId // For KQL filtering
                            });
                            _logger.LogInformation("DDNS update attempt for hostname test-{TestId}.ddns.test.dev from 203.0.113.42 resulted in good - Update successful - test scenario {TestId}", testId, testId);
                            result.Details = "Simulated successful update";
                            break;
                        }

                    case "geo_anomaly":
                        {
                            // Simulate rapid geographic changes - First update from US
                            using (var scope3a = _logger.BeginScope(new Dictionary<string, object>
                            {
                                ["ddns.event_type"] = "ddns_update",
                                ["ddns.hostname"] = $"test-{testId}.ddns.test.dev",
                                ["ddns.source_ip"] = "198.51.100.42", // US IP
                                ["ddns.user_agent"] = "test-monitoring-client/1.0",
                                ["ddns.auth_method"] = "ApiKey",
                                ["ddns.result_code"] = "good",
                                ["ddns.is_success"] = true,
                                ["ddns.correlation_id"] = testId,
                                ["ddns.timestamp"] = startTime,
                                ["ddns.target_ip"] = "203.0.113.10",
                                ["ddns.record_type"] = "A",
                                ["ddns.ttl"] = "none",
                                ["ddns.error_message"] = "",
                                ["testId"] = testId // For KQL filtering
                            }))
                            {
                                _logger.LogInformation("DDNS update attempt for hostname test-{TestId}.ddns.test.dev from 198.51.100.42 resulted in good - Update from US - test scenario {TestId}", testId, testId);
                            }
                            
                            await Task.Delay(1000); // Small delay
                            
                            // Second update from Europe (suspicious rapid geographic change)
                            using (var scope3b = _logger.BeginScope(new Dictionary<string, object>
                            {
                                ["ddns.event_type"] = "ddns_update",
                                ["ddns.hostname"] = $"test-{testId}.ddns.test.dev",
                                ["ddns.source_ip"] = "185.199.108.42", // Europe IP
                                ["ddns.user_agent"] = "test-monitoring-client/1.0",
                                ["ddns.auth_method"] = "ApiKey",
                                ["ddns.result_code"] = "good",
                                ["ddns.is_success"] = true,
                                ["ddns.correlation_id"] = testId,
                                ["ddns.timestamp"] = startTime.AddMilliseconds(1000),
                                ["ddns.target_ip"] = "203.0.113.10",
                                ["ddns.record_type"] = "A",
                                ["ddns.ttl"] = "none",
                                ["ddns.error_message"] = "",
                                ["testId"] = testId // For KQL filtering
                            }))
                            {
                                _logger.LogInformation("DDNS update attempt for hostname test-{TestId}.ddns.test.dev from 185.199.108.42 resulted in good - Update from Europe - test scenario {TestId}", testId, testId);
                            }
                            result.Details = "Simulated geographic anomaly (US -> Europe within 1s)";
                            break;
                        }

                    default:
                        throw new ArgumentException($"Unknown test scenario: {scenario}");
                }

                _logger.LogInformation($"[MONITORING-TEST-{testId}] Executed scenario '{scenario}' successfully");
            }
            catch (Exception ex)
            {
                result.TelemetryLogged = false;
                result.ErrorMessage = ex.Message;
                result.Details = $"Failed to execute scenario: {ex.Message}";
                _logger.LogError(ex, $"[MONITORING-TEST-{testId}] Failed to execute scenario '{scenario}'");
            }

            return result;
        }

        private async Task<List<TestScenarioResult>> ValidateTelemetryInApplicationInsights(
            string workspaceId, 
            string testId, 
            DateTimeOffset startTime, 
            int timeoutSeconds)
        {
            var results = new List<TestScenarioResult>();
            var maxRetries = Math.Min(timeoutSeconds / 30, 10); // Poll every 30s, max 10 attempts
            var retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    _logger.LogInformation($"[MONITORING-TEST-{testId}] Querying Application Insights (attempt {retryCount + 1}/{maxRetries})...");

                    // Query for test telemetry using testId for filtering
                    var query = $@"
AppTraces
| where TimeGenerated >= datetime({startTime:yyyy-MM-ddTHH:mm:ss.fffZ})
| where tostring(customDimensions['testId']) == '{testId}'
| extend TestScenario = case(
    customDimensions[""ddns.result_code""] == ""badauth"", ""auth_failure"",
    customDimensions[""ddns.result_code""] == ""good"" and customDimensions[""ddns.source_ip""] == ""203.0.113.42"", ""auth_success"",
    customDimensions[""ddns.result_code""] == ""good"" and customDimensions[""ddns.source_ip""] in (""198.51.100.42"", ""185.199.108.42""), ""geo_anomaly"",
    ""unknown""
)
| where TestScenario != ""unknown""
| summarize 
    Count = count(),
    FailedAuths = countif(customDimensions[""ddns.result_code""] == ""badauth""),
    SuccessfulUpdates = countif(customDimensions[""ddns.result_code""] == ""good""),
    UniqueIPs = dcount(tostring(customDimensions[""ddns.source_ip""]))
  by TestScenario";

                    var response = await _logsQueryClient.QueryWorkspaceAsync(
                        workspaceId,
                        query,
                        new QueryTimeRange(startTime, DateTimeOffset.UtcNow)
                    );

                    if (response.Value.Table.Rows.Count > 0)
                    {
                        _logger.LogInformation($"[MONITORING-TEST-{testId}] Found {response.Value.Table.Rows.Count} telemetry records");
                        
                        foreach (var row in response.Value.Table.Rows)
                        {
                            var scenario = row["TestScenario"].ToString();
                            var count = Convert.ToInt32(row["Count"]);
                            var failedAuths = Convert.ToInt32(row["FailedAuths"]);
                            var successfulUpdates = Convert.ToInt32(row["SuccessfulUpdates"]);
                            var uniqueIPs = Convert.ToInt32(row["UniqueIPs"]);

                            var testResult = new TestScenarioResult
                            {
                                Scenario = scenario!,
                                ApplicationInsightsValidated = true,
                                SentinelRuleWouldTrigger = DetermineSentinelRuleTrigger(scenario!, failedAuths, uniqueIPs),
                                Details = $"Found {count} events, {failedAuths} failed auths, {successfulUpdates} successful, {uniqueIPs} unique IPs"
                            };

                            results.Add(testResult);
                        }

                        _logger.LogInformation($"[MONITORING-TEST-{testId}] Successfully validated telemetry for {results.Count} scenarios");
                        break; // Success - exit retry loop
                    }
                    else
                    {
                        _logger.LogInformation($"[MONITORING-TEST-{testId}] No telemetry found yet, waiting 30s before retry...");
                        if (retryCount < maxRetries - 1) // Don't wait on the last attempt
                        {
                            await Task.Delay(30000); // Wait 30 seconds
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[MONITORING-TEST-{testId}] Failed to query Application Insights (attempt {retryCount + 1})");
                    
                    if (retryCount == maxRetries - 1) // Last attempt, add error results
                    {
                        results.Add(new TestScenarioResult
                        {
                            Scenario = "query_failure",
                            ApplicationInsightsValidated = false,
                            ErrorMessage = ex.Message,
                            Details = "Failed to query Application Insights"
                        });
                    }
                    else
                    {
                        await Task.Delay(30000); // Wait before retry
                    }
                }

                retryCount++;
            }

            // If no data found after all retries, create failure results
            if (results.Count == 0)
            {
                results.Add(new TestScenarioResult
                {
                    Scenario = "telemetry_ingestion",
                    ApplicationInsightsValidated = false,
                    Details = $"No telemetry found in Application Insights after {timeoutSeconds}s timeout",
                    ErrorMessage = "Telemetry ingestion timeout - data may not have reached Application Insights"
                });
            }

            return results;
        }

        private static bool DetermineSentinelRuleTrigger(string scenario, int failedAuths, int uniqueIPs)
        {
            return scenario.ToLowerInvariant() switch
            {
                "auth_failure" => failedAuths >= 1, // Our rule triggers on failed auth
                "geo_anomaly" => uniqueIPs >= 2, // Geographic anomaly with multiple IPs
                "auth_success" => false, // Successful auth shouldn't trigger alerts
                _ => false
            };
        }

        private static (string? oid, string? upn) GetUser(HttpRequestData req, ILogger log)
        {
            if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals))
            {
                return (null, null);
            }

            var raw = vals.FirstOrDefault();
            if (string.IsNullOrEmpty(raw))
            {
                return (null, null);
            }

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                using var doc = JsonDocument.Parse(json);
                string? oid = null, upn = null;

                if (doc.RootElement.TryGetProperty("claims", out var claims))
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (claim.TryGetProperty("typ", out var typ) && claim.TryGetProperty("val", out var val))
                        {
                            var typeStr = typ.GetString();
                            var valStr = val.GetString();
                            
                            if (typeStr == "http://schemas.microsoft.com/identity/claims/objectidentifier") 
                            {
                                oid = valStr;
                            }
                            if (typeStr == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn") 
                            {
                                upn = valStr;
                            }
                        }
                    }
                }
                
                return (oid, upn);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to parse X-MS-CLIENT-PRINCIPAL");
                return (null, null);
            }
        }
    }
}