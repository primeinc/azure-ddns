using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Company.Function.Services
{
    public class MsalAuthenticationService
    {
        private readonly IPublicClientApplication _publicClientApp;
        private readonly ILogger<MsalAuthenticationService> _logger;
        private readonly string _tenantId;
        private readonly string _clientId;
        private readonly string[] _scopes = new[] { "User.Read" };

        public MsalAuthenticationService(ILogger<MsalAuthenticationService> logger)
        {
            _logger = logger;
            
            _tenantId = Environment.GetEnvironmentVariable("AZURE_AD_TENANT_ID") 
                ?? throw new InvalidOperationException("AZURE_AD_TENANT_ID not configured");
            
            _clientId = Environment.GetEnvironmentVariable("AZURE_AD_CLIENT_ID")
                ?? throw new InvalidOperationException("AZURE_AD_CLIENT_ID not configured");
            
            var redirectUri = Environment.GetEnvironmentVariable("AZURE_AD_REDIRECT_URI")
                ?? "https://localhost";

            _publicClientApp = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
                .WithRedirectUri(redirectUri)
                .Build();
        }

        public async Task<AuthenticationResult?> AcquireTokenInteractiveAsync()
        {
            try
            {
                var accounts = await _publicClientApp.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();

                AuthenticationResult result;
                
                try
                {
                    // Try to acquire token silently first
                    result = await _publicClientApp.AcquireTokenSilent(_scopes, firstAccount)
                        .ExecuteAsync();
                }
                catch (MsalUiRequiredException)
                {
                    // Fallback to interactive authentication
                    result = await _publicClientApp.AcquireTokenInteractive(_scopes)
                        .WithAccount(firstAccount)
                        .WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync();
                }
                
                _logger.LogInformation($"Token acquired for user: {result.Account.Username}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acquire token interactively");
                return null;
            }
        }

        public async Task<ClaimsPrincipal?> ValidateTokenAsync(string token)
        {
            try
            {
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://login.microsoftonline.com/{_tenantId}/v2.0",
                    ValidateAudience = true,
                    ValidAudience = _clientId,
                    ValidateLifetime = true,
                    IssuerSigningKeys = await GetSigningKeysAsync(),
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);
                
                _logger.LogInformation($"Token validated successfully for user: {principal.Identity?.Name}");
                return principal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token validation failed");
                return null;
            }
        }

        public string? GetPrincipalIdFromToken(ClaimsPrincipal principal)
        {
            // Try different claim types that might contain the principal ID
            var principalId = principal.FindFirst("oid")?.Value // Object ID claim
                           ?? principal.FindFirst("sub")?.Value // Subject claim
                           ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            return principalId;
        }

        public string? GetEmailFromToken(ClaimsPrincipal principal)
        {
            // Try different claim types that might contain the email
            var email = principal.FindFirst("preferred_username")?.Value
                     ?? principal.FindFirst("email")?.Value
                     ?? principal.FindFirst(ClaimTypes.Email)?.Value
                     ?? principal.FindFirst("upn")?.Value;
            
            return email;
        }

        public string? ExtractBearerToken(string? authorizationHeader)
        {
            if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Bearer "))
            {
                return null;
            }

            return authorizationHeader.Substring("Bearer ".Length).Trim();
        }

        private Task<IEnumerable<SecurityKey>> GetSigningKeysAsync()
        {
            // In production, you should cache these keys and refresh periodically
            // For now, we'll use a simplified approach
            var openIdConfigUrl = $"https://login.microsoftonline.com/{_tenantId}/v2.0/.well-known/openid-configuration";
            
            // This is a simplified version - in production, fetch and cache the signing keys
            // from the OpenID configuration endpoint
            return Task.FromResult<IEnumerable<SecurityKey>>(new List<SecurityKey>());
        }

        public string GenerateAuthenticationUrl(string hostname)
        {
            // Generate the URL for initiating the authentication flow
            var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(hostname));
            
            return $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/authorize" +
                   $"?client_id={_clientId}" +
                   $"&response_type=code" +
                   $"&redirect_uri={Uri.EscapeDataString(Environment.GetEnvironmentVariable("AZURE_AD_REDIRECT_URI") ?? "")}" +
                   $"&response_mode=query" +
                   $"&scope={Uri.EscapeDataString(string.Join(" ", _scopes))}" +
                   $"&state={state}";
        }
    }
}