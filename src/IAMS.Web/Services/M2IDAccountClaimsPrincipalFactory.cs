using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;

namespace IAMS.Web.Services;

/// <summary>
/// Custom claims principal factory that maps m2ID roles to IAMS roles.
/// </summary>
public class M2IDAccountClaimsPrincipalFactory : AccountClaimsPrincipalFactory<RemoteUserAccount>
{
    public M2IDAccountClaimsPrincipalFactory(IAccessTokenProviderAccessor accessor)
        : base(accessor)
    {
    }

    public override async ValueTask<ClaimsPrincipal> CreateUserAsync(
        RemoteUserAccount account,
        RemoteAuthenticationUserOptions options)
    {
        var user = await base.CreateUserAsync(account, options);

        if (user.Identity?.IsAuthenticated == true && account != null)
        {
            var identity = (ClaimsIdentity)user.Identity;

            // DEBUG: Log all additional properties
            Console.WriteLine("=== M2ID Claims Factory Debug ===");
            Console.WriteLine($"AdditionalProperties count: {account.AdditionalProperties.Count}");
            foreach (var prop in account.AdditionalProperties)
            {
                Console.WriteLine($"  {prop.Key}: {prop.Value}");
            }
            Console.WriteLine("=================================");

            // Check for role claim in additional claims
            if (account.AdditionalProperties.TryGetValue("role", out var roleValue))
            {
                AddRoleClaims(identity, roleValue);
            }

            // Also check the configured role claim name
            if (!string.IsNullOrEmpty(options.RoleClaim) &&
                account.AdditionalProperties.TryGetValue(options.RoleClaim, out var configuredRoleValue))
            {
                AddRoleClaims(identity, configuredRoleValue);
            }

            // Get the identity's role claim type and map Administrator to Admin if present
            var roleClaimType = identity.RoleClaimType ?? ClaimTypes.Role;
            var allRoles = identity.FindAll(c => c.Type == roleClaimType || c.Type == ClaimTypes.Role || c.Type == "role").ToList();
            foreach (var role in allRoles)
            {
                if (role.Value == "Administrator" && !identity.HasClaim(roleClaimType, "Admin"))
                {
                    identity.AddClaim(new Claim(roleClaimType, "Admin"));
                }
            }

            // Extract permission claims from the token
            if (account.AdditionalProperties.TryGetValue("permission", out var permissionValue))
            {
                AddPermissionClaims(identity, permissionValue);
            }
        }

        return user;
    }

    private void AddPermissionClaims(ClaimsIdentity identity, object permissionValue)
    {
        if (permissionValue is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var perm in element.EnumerateArray())
                {
                    var permStr = perm.GetString();
                    if (!string.IsNullOrEmpty(permStr) && !identity.HasClaim("permission", permStr))
                    {
                        identity.AddClaim(new Claim("permission", permStr));
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var permStr = element.GetString();
                if (!string.IsNullOrEmpty(permStr) && !identity.HasClaim("permission", permStr))
                {
                    identity.AddClaim(new Claim("permission", permStr));
                }
            }
        }
        else if (permissionValue is string permStr)
        {
            if (!identity.HasClaim("permission", permStr))
            {
                identity.AddClaim(new Claim("permission", permStr));
            }
        }
    }

    private void AddRoleClaims(ClaimsIdentity identity, object roleValue)
    {
        // Get the identity's role claim type (what IsInRole uses)
        var roleClaimType = identity.RoleClaimType ?? ClaimTypes.Role;

        if (roleValue is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in element.EnumerateArray())
                {
                    var roleStr = role.GetString();
                    if (!string.IsNullOrEmpty(roleStr))
                    {
                        AddRoleWithMapping(identity, roleStr, roleClaimType);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var roleStr = element.GetString();
                if (!string.IsNullOrEmpty(roleStr))
                {
                    AddRoleWithMapping(identity, roleStr, roleClaimType);
                }
            }
        }
        else if (roleValue is string roleStr)
        {
            AddRoleWithMapping(identity, roleStr, roleClaimType);
        }
    }

    private void AddRoleWithMapping(ClaimsIdentity identity, string role, string roleClaimType)
    {
        // Add with identity's role claim type for IsInRole to work
        if (!identity.HasClaim(roleClaimType, role))
        {
            identity.AddClaim(new Claim(roleClaimType, role));
        }

        // Map Administrator to Admin
        if (role == "Administrator" && !identity.HasClaim(roleClaimType, "Admin"))
        {
            identity.AddClaim(new Claim(roleClaimType, "Admin"));
        }
    }
}
