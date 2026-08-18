using AssetDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Authorization;

/// <summary>
/// Guards the tenant-wide invariant that at least one ACTIVE, role-holding user retains
/// iams:roles:manage - without it, nobody could ever manage roles in the tenant again, and only a
/// platform SuperAdmin could fix it back up.
///
/// Two different operations can break this, so this is called from two different controllers:
/// RolesController.UpdateRole can strip iams:roles:manage from a role's grants, and
/// UsersController.UpdateUser/DeleteUser can move the last holder of a role-managing role to a
/// different role, or deactivate them. Both need the exact same "would this orphan role
/// management" answer, so it lives here once instead of as two independently-maintained checks
/// that could quietly drift apart.
///
/// Inactive users never count as coverage: AuthController blocks inactive users from logging in
/// or refreshing a token, so a role held only by inactive users cannot actually be exercised by
/// anyone.
/// </summary>
public static class RoleManagementLockoutGuard
{
    private const string LockoutMessage =
        "At least one active user in this tenant must retain \"Manage roles\", or nobody would " +
        "be able to manage roles in this tenant again.";

    /// <summary>
    /// True (with an explanatory message) if applying `accepted` to `roleId` would remove
    /// iams:roles:manage from a role that currently holds it, and no OTHER role with at least one
    /// ACTIVE holder in the tenant would carry it afterwards. A role nobody currently holds, or
    /// one held only by inactive users, does not count as coverage.
    /// </summary>
    public static async Task<string?> ForRoleGrantChangeAsync(
        AppDbContext db, Guid tenantId, string roleId, List<string> accepted)
    {
        if (accepted.Contains(Permissions.RolesManage)) return null; // not being removed

        var currentlyHasIt = await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == roleId && rp.TenantId == tenantId && rp.Permission == Permissions.RolesManage);
        if (!currentlyHasIt) return null; // nothing to lose

        var otherManagingRoleIds = (await RoleIdsGrantingRolesManageAsync(db, tenantId))
            .Where(id => id != roleId)
            .ToList();
        if (otherManagingRoleIds.Count == 0) return LockoutMessage;

        var stillCovered = await AnyActiveHolderAsync(db, tenantId, otherManagingRoleIds, excludingUserId: null);
        return stillCovered ? null : LockoutMessage;
    }

    /// <summary>
    /// True (with an explanatory message) if moving `userId` from their current role(s) onto
    /// `newRoleId` would leave the tenant with no active user holding a role that grants
    /// iams:roles:manage. A no-op unless the user is currently covering it and the role they are
    /// moving to does not.
    /// </summary>
    public static async Task<string?> ForUserRoleChangeAsync(
        AppDbContext db, Guid tenantId, string userId, string newRoleId)
    {
        var managingRoleIds = await RoleIdsGrantingRolesManageAsync(db, tenantId);
        if (managingRoleIds.Count == 0) return null; // nothing in this tenant grants it
        if (managingRoleIds.Contains(newRoleId)) return null; // still covered, just via the new role

        var currentRoleIds = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        if (!currentRoleIds.Any(managingRoleIds.Contains)) return null; // wasn't covering it anyway

        var stillCovered = await AnyActiveHolderAsync(db, tenantId, managingRoleIds, excludingUserId: userId);
        return stillCovered ? null : LockoutMessage;
    }

    /// <summary>
    /// True (with an explanatory message) if deactivating `userId` would leave the tenant with no
    /// active user holding a role that grants iams:roles:manage.
    /// </summary>
    public static async Task<string?> ForUserDeactivationAsync(AppDbContext db, Guid tenantId, string userId)
    {
        var managingRoleIds = await RoleIdsGrantingRolesManageAsync(db, tenantId);
        if (managingRoleIds.Count == 0) return null;

        var currentRoleIds = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        if (!currentRoleIds.Any(managingRoleIds.Contains)) return null;

        var stillCovered = await AnyActiveHolderAsync(db, tenantId, managingRoleIds, excludingUserId: userId);
        return stillCovered ? null : LockoutMessage;
    }

    /// Role ids - built-in and this tenant's own custom roles alike - that currently grant
    /// iams:roles:manage in this tenant. RolePermission rows are already tenant-scoped (see the
    /// doc comment on RolePermission itself), so filtering by TenantId alone is enough here.
    private static Task<List<string>> RoleIdsGrantingRolesManageAsync(AppDbContext db, Guid tenantId) =>
        db.RolePermissions
            .Where(rp => rp.TenantId == tenantId && rp.Permission == Permissions.RolesManage)
            .Select(rp => rp.RoleId)
            .Distinct()
            .ToListAsync();

    private static async Task<bool> AnyActiveHolderAsync(
        AppDbContext db, Guid tenantId, List<string> roleIds, string? excludingUserId)
    {
        var tenantActiveUserIds = db.Users
            .Where(u => u.TenantId == tenantId && u.IsActive && u.Id != excludingUserId)
            .Select(u => u.Id);

        return await db.UserRoles
            .Where(ur => roleIds.Contains(ur.RoleId) && tenantActiveUserIds.Contains(ur.UserId))
            .AnyAsync();
    }
}
