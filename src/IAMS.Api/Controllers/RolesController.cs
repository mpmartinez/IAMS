using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class RolesController(
    AppDbContext db,
    RoleManager<ApplicationRole> roleManager,
    ITenantProvider tenantProvider,
    ILogger<RolesController> logger) : ControllerBase
{
    [HttpGet("~/api/permissions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewRoles")]
    public ActionResult<ApiResponse<List<PermissionGroupDto>>> GetPermissions()
    {
        var groups = Permissions.All
            .GroupBy(p => p.Group)
            .Select(g => new PermissionGroupDto
            {
                Group = g.Key,
                Permissions = g.Select(p => new PermissionDto
                {
                    Key = p.Key, Group = p.Group, Label = p.Label, Description = p.Description
                }).ToList()
            })
            .ToList();

        return Ok(ApiResponse<List<PermissionGroupDto>>.Ok(groups));
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewRoles")]
    public async Task<ActionResult<ApiResponse<List<RoleDto>>>> GetRoles()
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var roles = await VisibleRoles(tenantId).ToListAsync();
        var roleIds = roles.Select(r => r.Id).ToList();

        var grants = await db.RolePermissions
            .Where(rp => rp.TenantId == tenantId && roleIds.Contains(rp.RoleId))
            .Select(rp => new { rp.RoleId, rp.Permission })
            .ToListAsync();

        var counts = await UserCountsByRole(tenantId, roleIds);

        var result = roles.Select(role => new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            IsBuiltIn = role.IsBuiltIn,
            UserCount = counts.GetValueOrDefault(role.Id),
            Permissions = grants.Where(g => g.RoleId == role.Id).Select(g => g.Permission).ToList()
        }).ToList();

        return Ok(ApiResponse<List<RoleDto>>.Ok(result.OrderByDescending(r => r.IsBuiltIn).ThenBy(r => r.Name).ToList()));
    }

    /// <summary>
    /// How many users in this tenant hold each of these roles, in one query.
    ///
    /// Deliberately not UserManager.GetUsersInRoleAsync: that loads every holder of a role across
    /// every tenant into memory before filtering, so a role like Employee would pull the whole
    /// platform's users on each page load.
    /// </summary>
    private async Task<Dictionary<string, int>> UserCountsByRole(Guid tenantId, List<string> roleIds)
    {
        var tenantUserIds = db.Users.Where(u => u.TenantId == tenantId).Select(u => u.Id);

        return await db.UserRoles
            .Where(ur => roleIds.Contains(ur.RoleId) && tenantUserIds.Contains(ur.UserId))
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count);
    }

    [HttpGet("assignable")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageUsers")]
    public async Task<ActionResult<ApiResponse<List<AssignableRoleDto>>>> GetAssignable()
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var isSuperAdmin = tenantProvider.IsSuperAdmin();

        var roles = await VisibleRoles(tenantId)
            // SuperAdmin short-circuits tenant isolation, so only an existing SuperAdmin may hand
            // it out.
            .Where(r => isSuperAdmin || r.Name != Roles.SuperAdmin)
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name)
            .Select(r => new AssignableRoleDto { Name = r.Name!, Description = r.Description })
            .ToListAsync();

        return Ok(ApiResponse<List<AssignableRoleDto>>.Ok(roles));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> CreateRole(CreateRoleDto dto)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();

        var name = dto.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(ApiResponse<RoleDto>.Fail("Name is required."));

        // ApplicationRole keeps Identity's global unique index on NormalizedName (no per-tenant
        // composite index exists yet - see the "Catalog versioning" / name-uniqueness note in the
        // design doc), so this check is unscoped: a name already taken by ANY tenant collides
        // here, not just this tenant's own roles. Echoing the name back in the error would
        // confirm to tenant B that a role called that exists somewhere - possibly tenant A's,
        // which tenant B has no visibility into otherwise. Keep the message generic.
        if (await roleManager.FindByNameAsync(name) is not null)
            return BadRequest(ApiResponse<RoleDto>.Fail("That name is not available."));

        var rejected = Validate(dto.Permissions, out var accepted, existing: null);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        var role = new ApplicationRole(name)
        {
            TenantId = tenantId,
            IsBuiltIn = false,
            Description = dto.Description?.Trim()
        };

        // Role creation and its initial grants must land together. Without a transaction, a
        // failure between the two steps leaves a grant-less role behind and a 500 to the caller;
        // retrying then trips the duplicate-name check above, and the admin is stuck until
        // someone deletes the orphan manually.
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var created = await roleManager.CreateAsync(role);
            if (!created.Succeeded)
            {
                await transaction.RollbackAsync();
                return BadRequest(ApiResponse<RoleDto>.Fail(
                    string.Join(", ", created.Errors.Select(e => e.Description))));
            }

            await ReplaceGrants(role.Id, tenantId, accepted);
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex)
        {
            // Covers DbUpdateConcurrencyException too (it derives from DbUpdateException) - e.g.
            // two concurrent creates of the same role name racing past the FindByNameAsync check
            // above and tripping the unique index together.
            logger.LogWarning(ex, "CreateRole for tenant {TenantId} hit a DbUpdateException.", tenantId);
            await transaction.RollbackAsync();
            return Conflict(ApiResponse<RoleDto>.Fail(
                "This role could not be saved because of a conflicting change. Reload and try again."));
        }

        return Ok(ApiResponse<RoleDto>.Ok(new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            IsBuiltIn = false,
            UserCount = 0,
            Permissions = accepted
        }));
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> UpdateRole(string id, UpdateRoleDto dto)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var isSuperAdmin = tenantProvider.IsSuperAdmin();

        var role = await VisibleRoles(tenantId).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        // SuperAdmin bypasses every check, so letting anyone edit its grants would present a
        // control that does nothing while implying it does something.
        if (role.Name == Roles.SuperAdmin)
            return BadRequest(ApiResponse<RoleDto>.Fail("The SuperAdmin role cannot be edited."));

        var existingGrants = await db.RolePermissions
            .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
            .Select(rp => rp.Permission)
            .ToListAsync();

        var rejected = Validate(dto.Permissions, out var accepted, existingGrants);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        // A roles:manage holder could otherwise strip that permission from every role in the
        // tenant - including this one - and lock the whole tenant out of role management,
        // recoverable only by a platform SuperAdmin. SuperAdmin actors are exempt: they bypass
        // every permission check anyway, so they can always fix it back up.
        if (!isSuperAdmin)
        {
            var lockoutError = await RoleManagementLockoutGuard.ForRoleGrantChangeAsync(db, tenantId, role.Id, accepted);
            if (lockoutError is not null) return BadRequest(ApiResponse<RoleDto>.Fail(lockoutError));
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            if (!role.IsBuiltIn && dto.Description is not null)
            {
                role.Description = dto.Description.Trim();
                await roleManager.UpdateAsync(role);
            }

            await ReplaceGrants(role.Id, tenantId, accepted);

            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex)
        {
            // Covers DbUpdateConcurrencyException too. Two concurrent PUTs on the same role can
            // both delete-then-reinsert its grants, or trip the (RoleId, TenantId, Permission)
            // unique index against each other; either way this used to surface as an unhandled
            // 500. Ask the caller to reload and retry rather than implementing full optimistic
            // concurrency.
            logger.LogWarning(ex, "UpdateRole {RoleId} for tenant {TenantId} hit a DbUpdateException.", id, tenantId);
            return Conflict(ApiResponse<RoleDto>.Fail(
                "This role was changed by someone else. Reload and try again."));
        }

        var counts = await UserCountsByRole(tenantId, [role.Id]);

        return Ok(ApiResponse<RoleDto>.Ok(new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            IsBuiltIn = role.IsBuiltIn,
            UserCount = counts.GetValueOrDefault(role.Id),
            Permissions = accepted
        }));
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<IActionResult> DeleteRole(string id)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();

        var role = await VisibleRoles(tenantId).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        if (role.IsBuiltIn)
            return BadRequest(ApiResponse<object>.Fail("Built-in roles cannot be deleted."));

        var holders = (await UserCountsByRole(tenantId, [role.Id])).GetValueOrDefault(role.Id);
        if (holders > 0)
            return Conflict(ApiResponse<object>.Fail(
                $"{holders} user{(holders == 1 ? "" : "s")} still hold this role. Move them to another role first."));

        // The grant delete and the role delete must succeed or fail together. Without a
        // transaction and a checked result, a failed role delete (IdentityResult discarded) left
        // the caller told "gone" while the role still existed with zero grants - a silent
        // permission wipe reported as success.
        await using var transaction = await db.Database.BeginTransactionAsync();

        var grants = await db.RolePermissions
            .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
            .ToListAsync();
        db.RolePermissions.RemoveRange(grants);
        await db.SaveChangesAsync();

        var deleted = await roleManager.DeleteAsync(role);
        if (!deleted.Succeeded)
        {
            await transaction.RollbackAsync();
            return BadRequest(ApiResponse<object>.Fail(
                string.Join(", ", deleted.Errors.Select(e => e.Description))));
        }

        await transaction.CommitAsync();
        return NoContent();
    }

    /// Built-in roles plus this tenant's own. Another tenant's custom role is simply not there,
    /// so every lookup by id 404s rather than leaking its existence.
    private IQueryable<ApplicationRole> VisibleRoles(Guid tenantId) =>
        db.Roles.Where(r => r.TenantId == null || r.TenantId == tenantId);

    /// <summary>
    /// Returns an error message, or null and the accepted key list.
    ///
    /// <paramref name="existing"/> is null for a brand-new role (CreateRole - nothing to
    /// preserve yet, so any permission outside the actor's own grantable set is a flat rejection)
    /// and the role's current grants for an edit (UpdateRole).
    ///
    /// For an edit, this is delta-based rather than whole-set validation:
    /// <c>accepted = (existing ∩ ¬grantable) ∪ (requested ∩ grantable)</c> - every key the actor
    /// cannot grant is preserved regardless of what was submitted, and the actor's changes apply
    /// only within their own grantable set. Two escalation-adjacent bugs this closes:
    ///
    /// - DEAD END: a role richer than the actor's own grants disabled-but-checked boxes in the UI
    ///   for keys the actor cannot touch. Whole-set validation rejected the entire save because
    ///   those keys came back in the submitted list even though the actor never changed them. The
    ///   delta approach ignores what came back for a key outside the actor's grantable set and
    ///   just keeps what the role already had, so the save succeeds.
    /// - DESTRUCTION: the same whole-set check let an actor submit a strict subset of a role's
    ///   permissions - including keys they themselves lack - and that subset became the new
    ///   truth, silently stripping permissions nobody asked to remove and that the actor was
    ///   never entitled to touch. The delta approach makes that impossible: a key outside the
    ///   actor's grantable set is preserved from the role's existing state no matter what the
    ///   request body contains.
    /// </summary>
    private string? Validate(List<string> requested, out List<string> accepted, List<string>? existing)
    {
        var requestedDistinct = requested.Distinct().ToList();

        var unknown = requestedDistinct.Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
        {
            accepted = [];
            return $"Unknown permission{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown)}";
        }

        var grantable = ClaimsPrincipalExtensions.GrantableKeys(User, tenantProvider.IsSuperAdmin());

        if (existing is null)
        {
            // Nothing to preserve yet - a brand-new role can only be minted with permissions the
            // actor holds themselves. (Via the UI this never fires: the create form starts blank
            // and unheld keys render disabled, so nothing outside the actor's grantable set is
            // ever checked in the first place.)
            var overreach = requestedDistinct.Where(p => !grantable.Contains(p)).ToList();
            if (overreach.Count > 0)
            {
                accepted = [];
                return $"You cannot grant a permission you do not hold yourself: {string.Join(", ", overreach)}";
            }

            accepted = requestedDistinct;
            return null;
        }

        var preserved = existing.Where(p => !grantable.Contains(p));
        var applied = requestedDistinct.Where(grantable.Contains);
        accepted = preserved.Union(applied).ToList();
        return null;
    }

    private async Task ReplaceGrants(string roleId, Guid tenantId, List<string> permissions)
    {
        var existing = await db.RolePermissions
            .Where(rp => rp.RoleId == roleId && rp.TenantId == tenantId)
            .ToListAsync();

        db.RolePermissions.RemoveRange(existing);
        db.RolePermissions.AddRange(permissions.Select(p => new RolePermission
        {
            Id = Guid.NewGuid(), RoleId = roleId, TenantId = tenantId, Permission = p
        }));

        await db.SaveChangesAsync();
    }
}
