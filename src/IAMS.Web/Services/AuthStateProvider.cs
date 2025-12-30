using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Blazored.LocalStorage;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;

namespace IAMS.Web.Services;

public class AuthStateProvider(ILocalStorageService localStorage) : AuthenticationStateProvider
{
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await localStorage.GetItemAsync<string>("authToken");
            var user = await localStorage.GetItemAsync<UserDto>("currentUser");

            if (string.IsNullOrEmpty(token) || user is null)
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

            // Validate token is not expired
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

            var jwtToken = handler.ReadJwtToken(token);
            if (jwtToken.ValidTo < DateTime.UtcNow)
            {
                // Token is expired - return unauthenticated
                // The AuthService will attempt refresh when API calls are made
                Console.WriteLine("Access token expired, will attempt refresh on next API call");

                // Check if we have a refresh token - if not, clear auth state
                var refreshToken = await localStorage.GetItemAsync<string>("refreshToken");
                if (string.IsNullOrEmpty(refreshToken))
                {
                    return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                }

                // Return authenticated state to allow app to load, AuthService will refresh on API call
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Name, user.FullName),
                new(ClaimTypes.Role, user.Role),
                new("department", user.Department ?? ""),
                new("tenant_id", user.TenantId.ToString()),
                new("tenant_name", user.TenantName ?? ""),
                new("is_tenant_admin", user.IsTenantAdmin.ToString().ToLower()),
                new("is_super_admin", user.IsSuperAdmin.ToString().ToLower())
            };

            // Add SuperAdmin role if applicable
            if (user.IsSuperAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "SuperAdmin"));
            }

            var identity = new ClaimsIdentity(claims, "jwt");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AuthStateProvider error: {ex.Message}");
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
