using System.Security.Claims;
using Blazored.LocalStorage;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;

namespace IAMS.Web.Services;

public class AuthStateProvider(ILocalStorageService localStorage) : AuthenticationStateProvider
{
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await localStorage.GetItemAsync<string>("authToken");
        var user = await localStorage.GetItemAsync<UserDto>("currentUser");

        if (string.IsNullOrEmpty(token) || user is null)
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

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

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
