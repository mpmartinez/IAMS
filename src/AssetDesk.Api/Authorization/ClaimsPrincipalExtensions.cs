using System.Security.Claims;
using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Authorization;

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
        user is not null && (user.IsInRole(Roles.SuperAdmin) || user.HasClaim(Permissions.ClaimType, permission));

    /// <summary>
    /// The permissions <paramref name="actor"/> may hand to a role. Without this cap anyone
    /// holding iams:roles:manage could mint a role with every permission and assign it to
    /// themselves, which would make every other permission decorative.
    ///
    /// Lives here, not on RolesController, so RoleAssignmentGuard (and anything else in the
    /// authorization layer) can call it without a `using AssetDesk.Api.Controllers;` reaching down
    /// from Authorization into Controllers.
    /// </summary>
    public static IReadOnlyList<string> GrantableKeys(ClaimsPrincipal actor, bool isSuperAdmin)
    {
        // Permissions.Keys is a shared, process-wide static string[]. Returning it directly would
        // let a caller cast back to string[] and mutate the catalog for every tenant and every
        // request that follows. Array.AsReadOnly wraps it without copying but genuinely blocks
        // that cast.
        if (isSuperAdmin) return Array.AsReadOnly(Permissions.Keys);

        return actor.FindAll(Permissions.ClaimType)
            .Select(c => c.Value)
            .Where(Permissions.IsValid)
            .Distinct()
            .ToList();
    }
}
