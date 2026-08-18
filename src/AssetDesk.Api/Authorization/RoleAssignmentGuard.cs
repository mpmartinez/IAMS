using System.Security.Claims;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Authorization;

/// <summary>
/// Whether an actor may assign a given role name to a user.
///
/// Replaces name-whitelisting (the old Roles.CanAssign, now deleted) with a grant-subset check:
/// an actor may hand out any role - built-in or a tenant's own custom role - as long as that
/// role's grants are wholly contained in what the actor may themselves confer (see
/// <see cref="ClaimsPrincipalExtensions.GrantableKeys"/>). Without this, a holder of iams:users:manage
/// alone could assign themselves (or anyone) a role such as Admin that carries every permission
/// there is - total privilege escalation in one request. It also fixes the flip side: a custom
/// role created through POST /api/roles was never assignable to anyone, because the old
/// whitelist only knew about the five built-in role names.
/// </summary>
public static class RoleAssignmentGuard
{
    public sealed record Result(bool Success, string? Error, ApplicationRole? Role)
    {
        public static Result Ok(ApplicationRole role) => new(true, null, role);
        public static Result Fail(string error) => new(false, error, null);
    }

    /// <param name="tenantId">
    /// The tenant the role will actually apply within - the target user's tenant. For CreateUser
    /// this is the new user's own tenant (the actor's tenant, since only same-tenant creation is
    /// supported). For UpdateUser this must be the target user's tenant, not necessarily the
    /// actor's - a SuperAdmin can edit a user in a different tenant than their own, and it is the
    /// target tenant's custom roles and grants that matter there.
    /// </param>
    public static async Task<Result> CheckAsync(
        AppDbContext db,
        IPermissionResolver permissionResolver,
        ClaimsPrincipal actor,
        bool actorIsSuperAdmin,
        Guid tenantId,
        string roleName,
        CancellationToken ct = default)
    {
        // Visible to this tenant: every built-in role, plus the tenant's own custom roles.
        // Mirrors RolesController.VisibleRoles. SuperAdmin is excluded from the list (and so
        // rejected as "invalid") unless the actor is themselves a SuperAdmin - same rule
        // RolesController.GetAssignable enforces, since it bypasses tenant isolation entirely.
        var visible = await db.Roles
            .Where(r => r.TenantId == null || r.TenantId == tenantId)
            .Where(r => actorIsSuperAdmin || r.Name != Roles.SuperAdmin)
            .ToListAsync(ct);

        var role = visible.FirstOrDefault(r => r.Name == roleName);
        if (role is null)
        {
            var names = string.Join(", ", visible
                .OrderByDescending(r => r.IsBuiltIn)
                .ThenBy(r => r.Name)
                .Select(r => r.Name));
            return Result.Fail($"Invalid role. Must be one of: {names}");
        }

        // SuperAdmin bypasses every permission check, so it also bypasses this one - the same
        // way ClaimsPrincipalExtensions.GrantableKeys does for creating/editing roles.
        if (actorIsSuperAdmin) return Result.Ok(role);

        var roleGrants = await permissionResolver.GetPermissionsAsync([roleName], tenantId, ct);

        // An empty resolved grant set is not "harmless" just because an empty set is trivially a
        // subset of anything below - it is step 2 of a three-step escalation: PUT /api/roles/{id}
        // with {"permissions": []} (Validate only caps additions, so emptying passes), then
        // assign that now-zero-grant role to yourself, then let SeedData's startup
        // re-provisioning treat the tenant as "never provisioned" and silently restore every
        // default grant to it. Built-in roles ship with non-empty defaults (see
        // Permissions.DefaultsFor) and are never legitimately empty, so refuse to assign one that
        // resolves to zero grants. Custom roles are excluded from this check: a tenant admin may
        // deliberately create or edit a custom role down to zero permissions as a placeholder, and
        // that role isn't reachable by SeedData's re-provisioning at all (it only touches
        // IsBuiltIn roles), so it carries none of this chain's risk.
        if (role.IsBuiltIn && roleGrants.Count == 0)
            return Result.Fail(
                $"The \"{roleName}\" role has no permissions configured in this tenant and cannot be " +
                "assigned to anyone. Set its permissions first.");

        var grantable = ClaimsPrincipalExtensions.GrantableKeys(actor, actorIsSuperAdmin);
        var overreach = roleGrants.Where(p => !grantable.Contains(p)).ToList();
        if (overreach.Count > 0)
            return Result.Fail(
                $"You cannot assign a role that grants a permission you do not hold yourself: {string.Join(", ", overreach)}");

        return Result.Ok(role);
    }
}
