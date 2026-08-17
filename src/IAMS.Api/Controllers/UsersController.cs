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
public class UsersController(
    UserManager<ApplicationUser> userManager,
    ITenantProvider tenantProvider,
    ISubscriptionService subscriptionService,
    TokenService tokenService,
    IPermissionResolver permissionResolver,
    AppDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewUsers")]
    public async Task<ActionResult<PagedResponse<UserDto>>> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var tenantId = tenantProvider.GetCurrentTenantId();
        var isSuperAdmin = tenantProvider.IsSuperAdmin();

        var query = userManager.Users.AsQueryable();

        // Apply tenant filter unless super admin
        if (!isSuperAdmin && tenantId.HasValue)
        {
            query = query.Where(u => u.TenantId == tenantId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(search)) ||
                u.FullName.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync();
        var users = await query
            .Include(u => u.Tenant)
            .OrderBy(u => u.FullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = new List<UserDto>();
        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            items.Add(MapToDto(user, roles.FirstOrDefault() ?? "Staff"));
        }

        return Ok(new PagedResponse<UserDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageUsers")]
    public async Task<ActionResult<ApiResponse<UserDto>>> CreateUser(CreateUserDto dto)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var actorIsSuperAdmin = tenantProvider.IsSuperAdmin();

        var role = dto.Role ?? Roles.Staff;
        var assignment = await RoleAssignmentGuard.CheckAsync(
            db, permissionResolver, User, actorIsSuperAdmin, tenantId, role);
        if (!assignment.Success)
            return BadRequest(ApiResponse<UserDto>.Fail(assignment.Error!));

        // Check subscription limits
        if (!await subscriptionService.CanCreateUserAsync(tenantId))
            return BadRequest(ApiResponse<UserDto>.Fail("User limit reached for your subscription. Please upgrade."));

        var existingUser = await userManager.FindByEmailAsync(dto.Email);
        if (existingUser is not null)
            return BadRequest(ApiResponse<UserDto>.Fail("Email already exists"));

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            FullName = dto.FullName,
            Department = dto.Department,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            TenantId = tenantId
        };

        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<UserDto>.Fail(errors));
        }

        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            // Don't leave a role-less account behind that no policy will ever authorize.
            await userManager.DeleteAsync(user);
            return BadRequest(ApiResponse<UserDto>.Fail(
                string.Join(", ", roleResult.Errors.Select(e => e.Description))));
        }

        // Update tenant user count
        await subscriptionService.UpdateUserCountAsync(tenantId);

        // Reload user with tenant
        user = await userManager.Users.Include(u => u.Tenant).FirstAsync(u => u.Id == user.Id);

        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, role)));
    }

    [HttpGet("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewUsers")]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetUser(string id)
    {
        var user = await userManager.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
            return NotFound();

        // Verify tenant access
        var tenantId = tenantProvider.GetCurrentTenantId();
        if (!tenantProvider.IsSuperAdmin() && tenantId.HasValue && user.TenantId != tenantId.Value)
            return NotFound();

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff")));
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageUsers")]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateUser(string id, UpdateUserDto dto)
    {
        var user = await userManager.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
            return NotFound();

        // Verify tenant access
        var tenantId = tenantProvider.GetCurrentTenantId();
        var actorIsSuperAdmin = tenantProvider.IsSuperAdmin();
        if (!actorIsSuperAdmin && tenantId.HasValue && user.TenantId != tenantId.Value)
            return NotFound();

        var existingRoles = await userManager.GetRolesAsync(user);

        // A platform SuperAdmin is only editable by another SuperAdmin.
        if (!actorIsSuperAdmin && existingRoles.Contains(Roles.SuperAdmin))
            return Forbid();

        ApplicationRole? targetRole = null;
        if (dto.Role is not null)
        {
            // The target user's own tenant, not the actor's: a SuperAdmin can edit a user in a
            // different tenant, and it is that tenant's custom roles and grants that govern what
            // this role name means and what it hands out.
            var assignment = await RoleAssignmentGuard.CheckAsync(
                db, permissionResolver, User, actorIsSuperAdmin, user.TenantId, dto.Role);
            if (!assignment.Success)
                return BadRequest(ApiResponse<UserDto>.Fail(assignment.Error!));
            targetRole = assignment.Role;
        }

        // Two ways this request can strip the tenant's last active role-management coverage:
        // moving this user off every role that grants iams:roles:manage, or deactivating them
        // outright. RolesController.UpdateRole guards the third way (editing a role's own
        // grants) - this is the same invariant, checked here for the paths only UsersController
        // can trigger. SuperAdmin actors are exempt, same as everywhere else this invariant is
        // enforced: they bypass every permission check anyway, so they can always fix it back up.
        if (!actorIsSuperAdmin)
        {
            if (targetRole is not null && !existingRoles.Contains(dto.Role!))
            {
                var lockoutError = await RoleManagementLockoutGuard.ForUserRoleChangeAsync(
                    db, user.TenantId, user.Id, targetRole.Id);
                if (lockoutError is not null)
                    return BadRequest(ApiResponse<UserDto>.Fail(lockoutError));
            }

            if (dto.IsActive == false && user.IsActive)
            {
                var lockoutError = await RoleManagementLockoutGuard.ForUserDeactivationAsync(
                    db, user.TenantId, user.Id);
                if (lockoutError is not null)
                    return BadRequest(ApiResponse<UserDto>.Fail(lockoutError));
            }
        }

        if (dto.Email is not null && dto.Email != user.Email)
        {
            var existingUser = await userManager.FindByEmailAsync(dto.Email);
            if (existingUser is not null && existingUser.Id != id)
                return BadRequest(ApiResponse<UserDto>.Fail("Email already exists"));

            user.Email = dto.Email;
            user.UserName = dto.Email;
        }

        if (dto.FullName is not null) user.FullName = dto.FullName;
        if (dto.Department is not null) user.Department = dto.Department;
        if (dto.IsActive.HasValue) user.IsActive = dto.IsActive.Value;

        user.UpdatedAt = DateTime.UtcNow;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<UserDto>.Fail(errors));
        }

        // Update role if changed
        if (dto.Role is not null && !existingRoles.Contains(dto.Role))
        {
            await userManager.RemoveFromRolesAsync(user, existingRoles);
            var roleResult = await userManager.AddToRoleAsync(user, dto.Role);
            if (!roleResult.Succeeded)
            {
                // Put the old roles back rather than leaving the account with none.
                await userManager.AddToRolesAsync(user, existingRoles);
                return BadRequest(ApiResponse<UserDto>.Fail(
                    string.Join(", ", roleResult.Errors.Select(e => e.Description))));
            }

            // Their live access token still carries the old role's permissions and keeps working
            // until it naturally expires - this revokes their refresh tokens, not that access
            // token. The practical effect is a hard logout on next refresh attempt, not a silent
            // upgrade to the new role's permissions. This affects one person, so that trade-off is
            // fine; editing a role's permissions (affects everyone who holds it) is handled
            // differently - see the spec's "Token and Staleness" section.
            await tokenService.RevokeAllUserTokensAsync(user.Id);
        }

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff")));
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageUsers")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();

        // Verify tenant access
        var tenantId = tenantProvider.GetCurrentTenantId();
        var actorIsSuperAdmin = tenantProvider.IsSuperAdmin();
        if (!actorIsSuperAdmin && tenantId.HasValue && user.TenantId != tenantId.Value)
            return NotFound();

        // Same invariant UpdateUser enforces on its deactivation path: a soft delete must not
        // remove the tenant's last active role-management coverage.
        if (!actorIsSuperAdmin && user.IsActive)
        {
            var lockoutError = await RoleManagementLockoutGuard.ForUserDeactivationAsync(
                db, user.TenantId, user.Id);
            if (lockoutError is not null)
                return BadRequest(ApiResponse<object>.Fail(lockoutError));
        }

        // Soft delete
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        return NoContent();
    }

    // Users with iams:users:read permission can view the users list (for asset assignment)
    [HttpGet("list")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewUsersList")]
    public async Task<ActionResult> GetUserList()
    {
        var tenantId = tenantProvider.GetCurrentTenantId();
        var isSuperAdmin = tenantProvider.IsSuperAdmin();

        var query = userManager.Users.Where(u => u.IsActive);

        // Apply tenant filter unless super admin
        if (!isSuperAdmin && tenantId.HasValue)
        {
            query = query.Where(u => u.TenantId == tenantId.Value);
        }

        var users = await query
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.Department })
            .ToListAsync();

        return Ok(users);
    }

    private static UserDto MapToDto(ApplicationUser user, string role) => new()
    {
        Id = user.Id,
        Email = user.Email!,
        FullName = user.FullName,
        Department = user.Department,
        Role = role,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        TenantId = user.TenantId,
        TenantName = user.Tenant?.Name,
        IsTenantAdmin = user.IsTenantAdmin,
        IsSuperAdmin = user.IsSuperAdmin
    };
}
