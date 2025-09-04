using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text.Json;
using System.Collections.Generic;

namespace Company.Function
{
    public class AdminFunction
    {
        private readonly ILogger<AdminFunction> _logger;
        private readonly TableStorageService _tableStorage;
        private readonly TemplateService _templateService;

        public AdminFunction(ILogger<AdminFunction> logger, TableStorageService tableStorage, TemplateService templateService)
        {
            _logger = logger;
            _tableStorage = tableStorage;
            _templateService = templateService;
        }

        [Function("AdminPanel")]
        public async Task<HttpResponseData> AdminPanel(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "management")] HttpRequestData req)
        {
            _logger.LogInformation("[AUDIT-ADMIN] Admin panel accessed");

            // Get user from EasyAuth header
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);
            var userRoles = AuthenticationHelper.GetUserRoles(req, _logger);

            if (string.IsNullOrEmpty(userId))
            {
                // No authentication, redirect to Azure AD login via EasyAuth
                _logger.LogInformation("[AUDIT-ADMIN] No authentication for admin panel, redirecting to EasyAuth login");
                var response = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
                var encodedRedirect = Uri.EscapeDataString("/api/management");
                var redirectUrl = $"https://{req.Url.Host}/.auth/login/aad?post_login_redirect_url={encodedRedirect}";
                _logger.LogInformation($"[AUDIT-REDIRECT] Redirecting to: {redirectUrl}");
                response.Headers.Add("Location", redirectUrl);
                return response;
            }

            // Check if user is admin or bootstrap admin (tenant admin, etc.)
            bool isAdmin = userRoles.Contains("DDNSAdmin") || AuthenticationHelper.IsBootstrapAdmin(req, _logger);
            
            if (!isAdmin)
            {
                _logger.LogWarning($"[AUDIT-ADMIN-DENIED] Non-admin user {userEmail} attempted to access admin panel");
                var forbiddenResponse = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteStringAsync("Admin access required. Contact your IT administrator to be assigned the DDNS Administrator role.");
                return forbiddenResponse;
            }

            _logger.LogInformation($"[AUDIT-ADMIN-SUCCESS] Admin {userEmail} accessed admin panel");

            if (req.Method == "GET")
            {
                return await ShowAdminPanel(req);
            }
            else
            {
                return await HandleAdminAction(req);
            }
        }


        private async Task<HttpResponseData> ShowAdminPanel(HttpRequestData req)
        {
            // Get system statistics
            var totalHostnames = await GetTotalHostnames();
            var totalUsers = await GetTotalUsers();
            var totalApiKeys = await GetTotalApiKeys();
            
            // Get system health status
            var healthStatus = await GetSystemHealthStatus(req);

            // Build the model for template rendering
            var model = new Dictionary<string, object>
            {
                ["TotalHostnames"] = totalHostnames,
                ["TotalUsers"] = totalUsers,
                ["TotalApiKeys"] = totalApiKeys,
                ["AdminStatus"] = healthStatus.adminStatus,
                ["TableStorageIcon"] = healthStatus.tableStorage ? "✅" : "❌",
                ["TableStorageStatus"] = healthStatus.tableStorage ? "Connected" : "Connection Failed",
                ["AuthenticationIcon"] = healthStatus.authentication ? "✅" : "❌",
                ["AuthenticationStatus"] = healthStatus.authentication ? "Azure AD Active" : "Authentication Failed",
                ["DnsIntegrationIcon"] = healthStatus.dnsIntegration ? "✅" : "❌",
                ["DnsIntegrationStatus"] = healthStatus.dnsIntegration ? "Configuration Found" : "Missing DNS Configuration"
            };

            // Render the template
            var html = await _templateService.RenderAdvancedTemplateAsync("AdminPanelTemplate.html", model);

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await response.WriteStringAsync(html);
            return response;
        }

        private async Task<HttpResponseData> HandleAdminAction(HttpRequestData req)
        {
            // TODO: Handle POST actions like role assignments
            var response = req.CreateResponse(System.Net.HttpStatusCode.NotImplemented);
            await response.WriteStringAsync("Admin actions coming soon!");
            return response;
        }

        private async Task<int> GetTotalHostnames()
        {
            try
            {
                return await _tableStorage.GetTotalHostnamesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN-ERROR] Failed to get total hostnames count");
                return 0;
            }
        }

        private async Task<int> GetTotalUsers()
        {
            try
            {
                return await _tableStorage.GetTotalUsersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN-ERROR] Failed to get total users count");
                return 0;
            }
        }

        private async Task<int> GetTotalApiKeys()
        {
            try
            {
                return await _tableStorage.GetTotalActiveApiKeysAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ADMIN-ERROR] Failed to get total API keys count");
                return 0;
            }
        }

        private async Task<(bool tableStorage, bool authentication, bool dnsIntegration, string adminStatus)> GetSystemHealthStatus(HttpRequestData req)
        {
            // Test Table Storage connection
            var tableStorageHealthy = await _tableStorage.TestConnectionAsync();

            // Test authentication - we're already authenticated if we got here
            var authHealthy = true;

            // Test DNS integration - check if we can access environment variables for DNS
            var dnsZoneName = Environment.GetEnvironmentVariable("DnsZoneName");
            var dnsZoneRG = Environment.GetEnvironmentVariable("DnsZoneRGName");
            var dnsHealthy = !string.IsNullOrEmpty(dnsZoneName) && !string.IsNullOrEmpty(dnsZoneRG);

            // Get real admin status
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);
            var userRoles = AuthenticationHelper.GetUserRoles(req, _logger);
            var hasAppRole = userRoles.Contains("DDNSAdmin");
            var isBootstrapAdmin = AuthenticationHelper.IsBootstrapAdmin(req, _logger);
            
            string adminStatus;
            if (hasAppRole)
            {
                adminStatus = "✅ You have the 'DDNS Administrator' application role assigned.";
            }
            else if (isBootstrapAdmin)
            {
                var tenantRoles = AuthenticationHelper.GetTenantAdminRoles(req, _logger);
                if (tenantRoles.Any())
                {
                    adminStatus = $"🔑 Bootstrap Admin via tenant roles: {string.Join(", ", tenantRoles)}. Ask another admin to assign you the 'DDNS Administrator' role for permanent access.";
                }
                else
                {
                    adminStatus = "🔑 Bootstrap Admin via domain membership. Ask another admin to assign you the 'DDNS Administrator' role for permanent access.";
                }
            }
            else
            {
                adminStatus = "❌ No admin access detected.";
            }

            return (tableStorageHealthy, authHealthy, dnsHealthy, adminStatus);
        }
    }
}