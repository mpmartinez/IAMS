using System.Security.Claims;
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
public class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TokenService tokenService,
    AppDbContext db) : ControllerBase
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

        var ipAddress = GetIpAddress();
        var accessToken = await tokenService.GenerateTokenAsync(user);
        var refreshToken = await tokenService.GenerateRefreshTokenAsync(user.Id, ipAddress);
        var roles = await userManager.GetRolesAsync(user);

        // Get tenant info
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId);

        var response = new LoginResponseDto
        {
            Token = accessToken,
            RefreshToken = refreshToken.Token,
            ExpiresAt = tokenService.GetTokenExpiry(),
            RefreshTokenExpiresAt = refreshToken.ExpiresAt,
            User = MapToDto(user, roles.FirstOrDefault() ?? "Staff", tenant?.Name),
            Tenant = tenant != null ? MapToTenantSummary(tenant) : null
        };

        return Ok(ApiResponse<LoginResponseDto>.Ok(response));
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetCurrentUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);

        if (user is null)
            return NotFound();

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId);

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(MapToDto(user, roles.FirstOrDefault() ?? "Staff", tenant?.Name)));
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
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

    /// <summary>
    /// Refresh access token using refresh token (no auth required)
    /// </summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LoginResponseDto>>> RefreshToken(RefreshTokenRequestDto dto)
    {
        var ipAddress = GetIpAddress();

        // Rotate refresh token (revoke old, create new)
        var result = await tokenService.RotateRefreshTokenAsync(dto.RefreshToken, ipAddress);

        if (result is null)
            return Unauthorized(ApiResponse<LoginResponseDto>.Fail("Invalid or expired refresh token"));

        var (newRefreshToken, oldRefreshToken) = result.Value;
        var user = newRefreshToken.User;

        if (!user.IsActive)
        {
            await tokenService.RevokeRefreshTokenAsync(newRefreshToken.Token, ipAddress);
            return Unauthorized(ApiResponse<LoginResponseDto>.Fail("User account is disabled"));
        }

        var accessToken = await tokenService.GenerateTokenAsync(user);
        var roles = await userManager.GetRolesAsync(user);

        // Get tenant info
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId);

        var response = new LoginResponseDto
        {
            Token = accessToken,
            RefreshToken = newRefreshToken.Token,
            ExpiresAt = tokenService.GetTokenExpiry(),
            RefreshTokenExpiresAt = newRefreshToken.ExpiresAt,
            User = MapToDto(user, roles.FirstOrDefault() ?? "Staff", tenant?.Name),
            Tenant = tenant != null ? MapToTenantSummary(tenant) : null
        };

        return Ok(ApiResponse<LoginResponseDto>.Ok(response));
    }

    /// <summary>
    /// Logout - revoke refresh token
    /// </summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object>>> Logout(RefreshTokenRequestDto dto)
    {
        var ipAddress = GetIpAddress();
        await tokenService.RevokeRefreshTokenAsync(dto.RefreshToken, ipAddress);
        return Ok(ApiResponse<object>.Ok(new { }, "Logged out successfully"));
    }

    /// <summary>
    /// Logout from all devices - revoke all refresh tokens for user
    /// </summary>
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpPost("logout-all")]
    public async Task<ActionResult<ApiResponse<object>>> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var ipAddress = GetIpAddress();

        await tokenService.RevokeAllUserTokensAsync(userId!, ipAddress);
        return Ok(ApiResponse<object>.Ok(new { }, "Logged out from all devices"));
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

        // Revoke all refresh tokens on password reset for security
        await tokenService.RevokeAllUserTokensAsync(user.Id, GetIpAddress());

        return Ok(ApiResponse<object>.Ok(new { }, "Password has been reset successfully"));
    }

    private string? GetIpAddress()
    {
        if (Request.Headers.ContainsKey("X-Forwarded-For"))
            return Request.Headers["X-Forwarded-For"].FirstOrDefault();

        return HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    }

    private static UserDto MapToDto(ApplicationUser user, string role, string? tenantName = null) => new()
    {
        Id = user.Id,
        Email = user.Email!,
        FullName = user.FullName,
        Department = user.Department,
        Role = role,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        TenantId = user.TenantId,
        TenantName = tenantName,
        IsTenantAdmin = user.IsTenantAdmin,
        IsSuperAdmin = user.IsSuperAdmin
    };

    private static TenantSummaryDto MapToTenantSummary(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        Slug = tenant.Slug,
        LogoUrl = tenant.LogoUrl,
        PrimaryColor = tenant.PrimaryColor,
        SubscriptionTier = tenant.SubscriptionTier,
        IsActive = tenant.IsActive
    };
}
