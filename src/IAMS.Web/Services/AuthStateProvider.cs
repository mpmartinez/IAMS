using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Blazored.LocalStorage;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IAMS.Web.Services;

public class AuthStateProvider(ILocalStorageService localStorage, IServiceProvider serviceProvider) : AuthenticationStateProvider
{
    // Bounds the claim-less-token refresh below to a single attempt for the LIFETIME of this
    // provider instance, not just within one call frame. NotifyAuthenticationStateChanged()
    // (see bottom of this file) re-enters GetAuthenticationStateAsync by evaluating its own
    // argument, and TryRefreshTokenAsync calls that notifier on its success path - so a local
    // "have I tried yet" variable gets reset on every re-entry and never actually bounds
    // anything. A field survives the re-entry: if the refreshed token is itself still
    // claim-less (e.g. a genuinely zero-permission user, or a refresh served by a pre-deploy
    // instance mid-rollout), every subsequent call - including the one Notify's own re-entry
    // triggers - skips straight past this branch instead of refreshing again.
    private bool _claimlessRefreshAttempted;

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

            // A token minted before this feature shipped is still unexpired and readable, but
            // carries no "permission" claim at all - every PermissionView and nav gate would read
            // that as "holds nothing", locking an already-logged-in user out for up to the token's
            // remaining lifetime (Jwt:ExpireMinutes, currently 30). Force one refresh attempt to
            // pick up a claim-bearing token instead. Bounded to a single attempt for the life of
            // this provider instance (see _claimlessRefreshAttempted) - a user with a genuinely
            // empty permission set is indistinguishable from a stale token here, and would
            // otherwise refresh forever: if the refreshed token is itself still claim-less, fall
            // through and use what we have rather than looping.
            if (jwtToken.ValidTo >= DateTime.UtcNow && !jwtToken.Claims.Any(c => c.Type == "permission")
                && !_claimlessRefreshAttempted)
            {
                _claimlessRefreshAttempted = true;
                var authService = serviceProvider.GetRequiredService<AuthService>();
                if (await authService.TryRefreshTokenAsync())
                {
                    var refreshedToken = await localStorage.GetItemAsync<string>("authToken");
                    if (!string.IsNullOrEmpty(refreshedToken) && handler.CanReadToken(refreshedToken))
                    {
                        token = refreshedToken;
                        jwtToken = handler.ReadJwtToken(token);
                        user = await localStorage.GetItemAsync<UserDto>("currentUser") ?? user;
                    }
                }
                else
                {
                    // The refresh failed (e.g. a 401 because the refresh token was invalid).
                    // TryRefreshTokenAsync already cleared storage and pushed its own
                    // unauthenticated notification in that case - but this frame is still
                    // holding the stale `token`/`user` locals read before the attempt. Falling
                    // through with those would build and return an AUTHENTICATED state here,
                    // racing the unauthenticated one that notification just queued. Re-check
                    // storage instead: if it's gone, this call must agree and report
                    // unauthenticated too.
                    var stillHasToken = await localStorage.GetItemAsync<string>("authToken");
                    if (string.IsNullOrEmpty(stillHasToken))
                        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
                }
            }

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
                new("department", user.Department ?? ""),
                new("tenant_id", user.TenantId.ToString()),
                new("tenant_name", user.TenantName ?? ""),
                new("is_tenant_admin", user.IsTenantAdmin.ToString().ToLower()),
                new("is_super_admin", user.IsSuperAdmin.ToString().ToLower())
            };

            // Roles come from the token, which is what the API authorises against - not from
            // UserDto.Role, which holds only one. A user in several roles (the seeded super
            // admin is in both SuperAdmin and Admin) lost all but that one here, so the API
            // would accept a request the UI had already hidden the button for.
            var roleClaims = jwtToken.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                .Select(c => c.Value)
                .Distinct()
                .ToList();

            // Fall back to the stored single role only if the token carried none.
            if (roleClaims.Count == 0 && !string.IsNullOrWhiteSpace(user.Role))
                roleClaims.Add(user.Role);

            if (user.IsSuperAdmin && !roleClaims.Contains("SuperAdmin"))
                roleClaims.Add("SuperAdmin");

            claims.AddRange(roleClaims.Select(r => new Claim(ClaimTypes.Role, r)));

            // Permission claims come from the token for the same reason roles do: the token is
            // what the API authorises against. UserDto carries no permissions at all.
            claims.AddRange(jwtToken.Claims
                .Where(c => c.Type == "permission")
                .Select(c => c.Value)
                .Distinct()
                .Select(p => new Claim("permission", p)));

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
