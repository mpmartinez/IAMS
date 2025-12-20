using IAMS.Api.Entities;
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
public class UsersController(UserManager<ApplicationUser> userManager) : ControllerBase
{
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
    public async Task<ActionResult<PagedResponse<UserDto>>> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = userManager.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(search)) ||
                u.FullName.ToLower().Contains(search));
        }

        var totalCount = await query.CountAsync();
        var users = await query
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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<UserDto>>> CreateUser(CreateUserDto dto)
    {
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
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<UserDto>.Fail(errors));
        }

        var role = dto.Role ?? "Staff";
        await userManager.AddToRoleAsync(user, role);

        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, role)));
    }

    [HttpGet("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetUser(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff")));
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateUser(string id, UpdateUserDto dto)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();

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
        if (dto.Role is not null)
        {
            var currentRoles = await userManager.GetRolesAsync(user);
            if (!currentRoles.Contains(dto.Role))
            {
                await userManager.RemoveFromRolesAsync(user, currentRoles);
                await userManager.AddToRoleAsync(user, dto.Role);
            }
        }

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff")));
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();

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
        var users = await userManager.Users
            .Where(u => u.IsActive)
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
        CreatedAt = user.CreatedAt
    };
}
