using System.IdentityModel.Tokens.Jwt;
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace IAMS.Api.Tests;

/// <summary>
/// Proves the Task 6 keystone: a generated JWT actually carries the "permission" claims the
/// resolver produces, for a real Identity role assignment - not just that the resolver itself
/// returns the right set (PermissionResolverTests already covers that in isolation).
///
/// UserManager is constructed directly against a real UserStore over the test AppDbContext,
/// bypassing the ASP.NET Core DI container entirely (no mocking library is referenced by this
/// project). That is a real UserStore/UserManager pair, not a fake, so CreateAsync/AddToRoleAsync
/// exercise the same Identity code path AuthController relies on.
/// </summary>
public class TokenServiceClaimsTests
{
    private static async Task<ApplicationRole> SeedBuiltInRoleAsync(AppDbContext db, string name)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = true
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static UserManager<ApplicationUser> CreateUserManager(AppDbContext db)
    {
        var store = new UserStore<ApplicationUser, ApplicationRole, AppDbContext>(db);
        return new UserManager<ApplicationUser>(
            store,
            optionsAccessor: Options.Create(new IdentityOptions()),
            passwordHasher: new PasswordHasher<ApplicationUser>(),
            userValidators: [],
            passwordValidators: [],
            keyNormalizer: new UpperInvariantLookupNormalizer(),
            errors: new IdentityErrorDescriber(),
            services: null!,
            logger: NullLoggerFactory.CreateLogger());
    }

    private static IConfiguration CreateJwtConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-signing-key-that-is-long-enough-for-hmac-sha256",
            ["Jwt:Issuer"] = "IAMS.Tests",
            ["Jwt:Audience"] = "IAMS.Tests",
            ["Jwt:ExpireMinutes"] = "30",
        })
        .Build();

    [Fact]
    public async Task GenerateTokenAsync_EmitsExactlyTheStaffDefaultPermissionClaims()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);

        // Built-in roles seeded, then their default grants provisioned - the real startup path.
        foreach (var r in Roles.All) await SeedBuiltInRoleAsync(db, r);
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var user = new ApplicationUser
        {
            Id = "staff-user-1",
            UserName = "staff@test.local",
            Email = "staff@test.local",
            FullName = "Staff Person",
            TenantId = tenantId
        };
        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(e => e.Description)));

        var roleResult = await userManager.AddToRoleAsync(user, Roles.Staff);
        Assert.True(roleResult.Succeeded, string.Join(", ", roleResult.Errors.Select(e => e.Description)));

        var tokenService = new TokenService(
            CreateJwtConfiguration(), userManager, new PermissionResolver(db), db);

        var jwt = await tokenService.GenerateTokenAsync(user);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        var permissionClaims = token.Claims
            .Where(c => c.Type == Permissions.ClaimType)
            .Select(c => c.Value)
            .ToList();

        // Exactly the 13 keys DefaultsFor(Staff) grants - no more, no fewer, no duplicates.
        Assert.Equal(13, Permissions.DefaultsFor(Roles.Staff).Count);
        Assert.Equal(
            Permissions.DefaultsFor(Roles.Staff).OrderBy(k => k),
            permissionClaims.OrderBy(k => k));
        Assert.Equal(permissionClaims.Count, permissionClaims.Distinct().Count());
    }
}

file static class NullLoggerFactory
{
    public static Microsoft.Extensions.Logging.ILogger<UserManager<ApplicationUser>> CreateLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>.Instance;
}
