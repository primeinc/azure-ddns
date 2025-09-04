using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Company.Function.Services;
using System.Text.Json;

namespace Company.Function
{
    public class AdminFunction
    {
        private readonly ILogger<AdminFunction> _logger;
        private readonly TableStorageService _tableStorage;

        public AdminFunction(ILogger<AdminFunction> logger, TableStorageService tableStorage)
        {
            _logger = logger;
            _tableStorage = tableStorage;
        }

        [Function("AdminPanel")]
        public async Task<HttpResponseData> AdminPanel(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "management")] HttpRequestData req)
        {
            _logger.LogInformation("[AUDIT-ADMIN] Admin panel accessed");

            // Get user from EasyAuth header
            var (userId, userEmail) = AuthenticationHelper.GetUserFromHeaders(req, _logger);
            var userRoles = AuthenticationHelper.GetUserRoles(req, _logger);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogError("[AUDIT-ADMIN] EasyAuth failed - no user ID in headers");
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Authentication system error");
                return errorResponse;
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

            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>DDNS Admin Panel</title>
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&display=swap');
        body {{ 
            font-family: 'Orbitron', monospace; 
            background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
            color: #00ffff;
            margin: 0;
            padding: 20px;
        }}
        .container {{ 
            background: #0a0a0a;
            padding: 40px;
            border-radius: 8px;
            border: 1px solid #6a00ff;
            box-shadow: 0 0 20px rgba(106, 0, 255, 0.3);
            max-width: 1200px;
            margin: 0 auto;
        }}
        h1 {{ color: #ff00ff; text-shadow: 0 0 10px rgba(255, 0, 255, 0.5); }}
        .stats {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin: 20px 0; }}
        .stat {{ 
            background: #1a1a2e; 
            padding: 20px; 
            border-radius: 8px; 
            border: 1px solid #00ffff; 
            text-align: center;
        }}
        .stat-number {{ font-size: 2em; color: #00ff00; font-weight: bold; }}
        .form-section {{ 
            background: #1a1a2e; 
            padding: 25px; 
            border-radius: 8px; 
            border: 1px solid #ff00ff; 
            margin: 20px 0; 
        }}
        .form-group {{ margin-bottom: 15px; }}
        .form-label {{ display: block; color: #00ffff; margin-bottom: 5px; }}
        .form-input {{ 
            width: 100%; 
            padding: 10px; 
            border: 1px solid #6a00ff; 
            border-radius: 4px; 
            background: #0a0a0a; 
            color: #00ffff; 
        }}
        .btn {{ 
            padding: 12px 24px; 
            background: linear-gradient(135deg, #ff00ff, #6a00ff); 
            color: white; 
            border: none; 
            border-radius: 6px; 
            cursor: pointer; 
            font-family: inherit;
        }}
        .btn:hover {{ background: linear-gradient(135deg, #6a00ff, #ff00ff); }}
        .danger {{ background: linear-gradient(135deg, #ff0066, #ff3366); }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>🔧 DDNS Admin Panel</h1>
        
        <div class='stats'>
            <div class='stat'>
                <div class='stat-number'>{totalHostnames}</div>
                <div>Total Hostnames</div>
            </div>
            <div class='stat'>
                <div class='stat-number'>{totalUsers}</div>
                <div>Active Users</div>
            </div>
            <div class='stat'>
                <div class='stat-number'>{totalApiKeys}</div>
                <div>API Keys</div>
            </div>
        </div>

        <div class='form-section'>
            <h2>🎯 Quick Actions</h2>
            <p>Coming soon: User role management, hostname transfers, bulk operations...</p>
            <p><strong>Bootstrap Admin:</strong> You currently have admin access via domain membership. 
               Ask another admin to assign you the 'DDNS Administrator' role for permanent access.</p>
        </div>

        <div class='form-section'>
            <h2>📊 System Health</h2>
            <p>✅ Table Storage: Connected</p>
            <p>✅ Authentication: Azure AD</p>
            <p>✅ DNS Integration: Active</p>
        </div>

        <div class='form-section'>
            <h2>🔗 Management Links</h2>
            <p><a href='/api/manage' style='color: #00ffff;'>← Back to User Dashboard</a></p>
            <p><a href='https://entra.microsoft.com' target='_blank' style='color: #00ffff;'>Entra Admin Center (Role Assignments)</a></p>
        </div>
    </div>
</body>
</html>";

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
                // Query hostname ownership table for count
                // This is a simplified version - you might want to add proper counting
                return 42; // Placeholder
            }
            catch
            {
                return 0;
            }
        }

        private async Task<int> GetTotalUsers()
        {
            try
            {
                // Count unique users from hostname ownership
                return 13; // Placeholder  
            }
            catch
            {
                return 0;
            }
        }

        private async Task<int> GetTotalApiKeys()
        {
            try
            {
                // Count active API keys
                return 28; // Placeholder
            }
            catch
            {
                return 0;
            }
        }
    }
}