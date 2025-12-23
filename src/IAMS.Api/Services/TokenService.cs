using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Services;

public class TokenService(
    IConfiguration configuration,
    UserManager<ApplicationUser> userManager,
    AppDbContext db)
{
    private const int RefreshTokenExpiryDays = 7;

    public async Task<string> GenerateTokenAsync(ApplicationUser user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("JWT Key not configured")));

        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var roles = await userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email!),
            new(ClaimTypes.Name, user.FullName),
            new("department", user.Department ?? "")
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var expireMinutes = int.Parse(configuration["Jwt:ExpireMinutes"] ?? "30");

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expireMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public DateTime GetTokenExpiry()
    {
        var expireMinutes = int.Parse(configuration["Jwt:ExpireMinutes"] ?? "30");
        return DateTime.UtcNow.AddMinutes(expireMinutes);
    }

    public async Task<RefreshToken> GenerateRefreshTokenAsync(string userId, string? ipAddress = null)
    {
        var refreshToken = new RefreshToken
        {
            Token = GenerateSecureToken(),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays),
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = ipAddress
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        return refreshToken;
    }

    public async Task<RefreshToken?> GetRefreshTokenAsync(string token)
    {
        return await db.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == token);
    }

    public async Task<(RefreshToken NewToken, RefreshToken OldToken)?> RotateRefreshTokenAsync(
        string oldToken,
        string? ipAddress = null)
    {
        var existingToken = await db.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == oldToken);

        if (existingToken == null || !existingToken.IsActive)
            return null;

        // Revoke old token
        existingToken.RevokedAt = DateTime.UtcNow;
        existingToken.RevokedByIp = ipAddress;

        // Create new token
        var newToken = new RefreshToken
        {
            Token = GenerateSecureToken(),
            UserId = existingToken.UserId,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays),
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = ipAddress
        };

        existingToken.ReplacedByToken = newToken.Token;

        db.RefreshTokens.Add(newToken);
        await db.SaveChangesAsync();

        return (newToken, existingToken);
    }

    public async Task RevokeRefreshTokenAsync(string token, string? ipAddress = null)
    {
        var refreshToken = await db.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == token);
        if (refreshToken != null && refreshToken.IsActive)
        {
            refreshToken.RevokedAt = DateTime.UtcNow;
            refreshToken.RevokedByIp = ipAddress;
            await db.SaveChangesAsync();
        }
    }

    public async Task RevokeAllUserTokensAsync(string userId, string? ipAddress = null)
    {
        var tokens = await db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ToListAsync();

        foreach (var token in tokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedByIp = ipAddress;
        }

        await db.SaveChangesAsync();
    }

    public async Task CleanupExpiredTokensAsync()
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-30); // Keep tokens for 30 days for audit
        var expiredTokens = await db.RefreshTokens
            .Where(rt => rt.ExpiresAt < cutoffDate || (rt.RevokedAt != null && rt.RevokedAt < cutoffDate))
            .ToListAsync();

        db.RefreshTokens.RemoveRange(expiredTokens);
        await db.SaveChangesAsync();
    }

    private static string GenerateSecureToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }
}
