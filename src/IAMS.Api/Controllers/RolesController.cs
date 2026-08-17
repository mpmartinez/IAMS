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
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>
    /// The permissions this actor may hand to a role. Without this cap anyone holding
    /// iams:roles:manage could mint a role with every permission and assign it to themselves,
    /// which would make every other permission decorative.
    /// </summary>
    public static IReadOnlyList<string> GrantableKeys(ClaimsPrincipal actor, bool isSuperAdmin)
    {
        if (isSuperAdmin) return Permissions.Keys;

        return actor.FindAll(Permissions.ClaimType)
            .Select(c => c.Value)
            .Where(Permissions.IsValid)
            .Distinct()
            .ToList();
    }

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
            // it out - same rule as Roles.TenantAssignable.
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

        if (await roleManager.FindByNameAsync(name) is not null)
            return BadRequest(ApiResponse<RoleDto>.Fail($"A role named \"{name}\" already exists."));

        var rejected = Validate(dto.Permissions, out var accepted);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        var role = new ApplicationRole(name)
        {
            TenantId = tenantId,
            IsBuiltIn = false,
            Description = dto.Description?.Trim()
        };

        var created = await roleManager.CreateAsync(role);
        if (!created.Succeeded)
            return BadRequest(ApiResponse<RoleDto>.Fail(
                string.Join(", ", created.Errors.Select(e => e.Description))));

        await ReplaceGrants(role.Id, tenantId, accepted);

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

        var role = await VisibleRoles(tenantId).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        // SuperAdmin bypasses every check, so letting anyone edit its grants would present a
        // control that does nothing while implying it does something.
        if (role.Name == Roles.SuperAdmin)
            return BadRequest(ApiResponse<RoleDto>.Fail("The SuperAdmin role cannot be edited."));

        var rejected = Validate(dto.Permissions, out var accepted);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        if (!role.IsBuiltIn && dto.Description is not null)
        {
            role.Description = dto.Description.Trim();
            await roleManager.UpdateAsync(role);
        }

        await ReplaceGrants(role.Id, tenantId, accepted);

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

        var grants = await db.RolePermissions
            .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
            .ToListAsync();
        db.RolePermissions.RemoveRange(grants);
        await db.SaveChangesAsync();

        await roleManager.DeleteAsync(role);
        return NoContent();
    }

    /// Built-in roles plus this tenant's own. Another tenant's custom role is simply not there,
    /// so every lookup by id 404s rather than leaking its existence.
    private IQueryable<ApplicationRole> VisibleRoles(Guid tenantId) =>
        db.Roles.Where(r => r.TenantId == null || r.TenantId == tenantId);

    /// Returns an error message, or null and the accepted key list.
    private string? Validate(List<string> requested, out List<string> accepted)
    {
        accepted = requested.Distinct().ToList();

        var unknown = accepted.Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
            return $"Unknown permission{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown)}";

        var grantable = GrantableKeys(User, tenantProvider.IsSuperAdmin());
        var overreach = accepted.Where(p => !grantable.Contains(p)).ToList();
        if (overreach.Count > 0)
            return $"You cannot grant a permission you do not hold yourself: {string.Join(", ", overreach)}";

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
