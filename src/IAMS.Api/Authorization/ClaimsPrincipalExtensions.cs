using System.Security.Claims;
using IAMS.Api.Entities;

namespace IAMS.Api.Authorization;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// True if the user holds <paramref name="permission"/> via a permission claim, or is
    /// SuperAdmin. SuperAdmin bypasses permission checks everywhere else in this codebase (see
    /// <see cref="PermissionAuthorizationHandler"/>), so an in-controller check that used
    /// role-based logic before must keep that bypass to stay consistent with the policies that
    /// gate the endpoint itself.
    /// </summary>
    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.IsInRole(Roles.SuperAdmin) || user.HasClaim(Permissions.ClaimType, permission);
}
