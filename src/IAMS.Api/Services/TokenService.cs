using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IAMS.Api.Entities;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Services;

public class TokenService(IConfiguration configuration)
{
    public string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("JWT Key not configured")));

        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role)
        };

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
