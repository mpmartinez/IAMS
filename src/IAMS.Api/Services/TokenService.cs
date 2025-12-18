using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Services;

public class TokenService(IConfiguration configuration, UserManager<ApplicationUser> userManager)
{
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

        var expireMinutes = int.Parse(configuration["Jwt:ExpireMinutes"] ?? "60");

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
        var expireMinutes = int.Parse(configuration["Jwt:ExpireMinutes"] ?? "60");
        return DateTime.UtcNow.AddMinutes(expireMinutes);
    }
}
