using System.Security.Claims;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TokenService tokenService) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponseDto>>> Login(LoginDto dto)
    {
        var user = await userManager.FindByEmailAsync(dto.Email);

        if (user is null)
            return Unauthorized(ApiResponse<LoginResponseDto>.Fail("Invalid credentials"));

        var result = await signInManager.CheckPasswordSignInAsync(user, dto.Password, lockoutOnFailure: false);

        if (!result.Succeeded)
            return Unauthorized(ApiResponse<LoginResponseDto>.Fail("Invalid credentials"));

        if (!user.IsActive)
            return Forbid();

        var token = await tokenService.GenerateTokenAsync(user);
        var roles = await userManager.GetRolesAsync(user);

        var response = new LoginResponseDto
        {
            Token = token,
            ExpiresAt = tokenService.GetTokenExpiry(),
            User = MapToDto(user, roles.FirstOrDefault() ?? "Staff")
        };

        return Ok(ApiResponse<LoginResponseDto>.Ok(response));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetCurrentUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);

        if (user is null)
            return NotFound();

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff")));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<object>>> ChangePassword(ChangePasswordDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);

        if (user is null)
            return NotFound();

        var result = await userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<object>.Fail(errors));
        }

        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        return Ok(ApiResponse<object>.Ok(new { }, "Password changed successfully"));
    }

    [Authorize]
    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LoginResponseDto>>> RefreshToken()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);

        if (user is null || !user.IsActive)
            return Unauthorized();

        var token = await tokenService.GenerateTokenAsync(user);
        var roles = await userManager.GetRolesAsync(user);

        var response = new LoginResponseDto
        {
            Token = token,
            ExpiresAt = tokenService.GetTokenExpiry(),
            User = MapToDto(user, roles.FirstOrDefault() ?? "Staff")
        };

        return Ok(ApiResponse<LoginResponseDto>.Ok(response));
    }

    [AllowAnonymous]
    [HttpGet("roles")]
    public ActionResult<string[]> GetRoles()
    {
        return Ok(new[] { "Admin", "Staff", "Auditor" });
    }

    /// <summary>
    /// Request a password reset link
    /// </summary>
    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPassword(ForgotPasswordDto dto)
    {
        var user = await userManager.FindByEmailAsync(dto.Email);

        // Always return success to prevent email enumeration
        if (user is null || !user.IsActive)
            return Ok(ApiResponse<object>.Ok(new { }, "If an account exists, a reset link will be sent"));

        var token = await userManager.GeneratePasswordResetTokenAsync(user);

        // In a real application, you would send an email here with the reset link
        // For now, we'll log it to the console (development only)
        var resetUrl = $"https://localhost:5002/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}";
        Console.WriteLine($"[Password Reset] User: {user.Email}, Reset URL: {resetUrl}");

        return Ok(ApiResponse<object>.Ok(new { }, "If an account exists, a reset link will be sent"));
    }

    /// <summary>
    /// Reset password using token
    /// </summary>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<ActionResult<ApiResponse<object>>> ResetPassword(ResetPasswordDto dto)
    {
        var user = await userManager.FindByEmailAsync(dto.Email);

        if (user is null)
            return BadRequest(ApiResponse<object>.Fail("Invalid or expired token"));

        var result = await userManager.ResetPasswordAsync(user, dto.Token, dto.NewPassword);

        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            if (errors.Any(e => e.Contains("token")))
                return BadRequest(ApiResponse<object>.Fail("Invalid or expired token"));

            return BadRequest(ApiResponse<object>.Fail(string.Join(", ", errors)));
        }

        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        return Ok(ApiResponse<object>.Ok(new { }, "Password has been reset successfully"));
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
