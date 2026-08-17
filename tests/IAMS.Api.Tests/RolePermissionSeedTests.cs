using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class RolePermissionSeedTests
{
    private static async Task<ApplicationRole> SeedRoleAsync(AppDbContext db, string name)
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

    [Fact]
    public async Task Backfill_GivesEachBuiltInRoleItsDefaults()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var granted = await db.RolePermissions
            .Where(rp => rp.RoleId == staffId && rp.TenantId == tenantId)
            .Select(rp => rp.Permission)
            .ToListAsync();

        Assert.Equal(
            Permissions.DefaultsFor(Roles.Staff).OrderBy(k => k),
            granted.OrderBy(k => k));
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterFirst = await db.RolePermissions.CountAsync();
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterSecond = await db.RolePermissions.CountAsync();

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task Backfill_DoesNotRestoreARevokedGrant()
    {
        // A tenant that unticks a box must not have it reappear on the next restart.
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var revoked = await db.RolePermissions.FirstAsync(rp =>
            rp.RoleId == staffId && rp.Permission == Permissions.AssetsCreate);
        db.RolePermissions.Remove(revoked);
        await db.SaveChangesAsync();

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        Assert.False(await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == staffId
            && rp.TenantId == tenantId
            && rp.Permission == Permissions.AssetsCreate));
    }

    [Fact]
    public async Task Backfill_KeepsTenantsIndependent()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantA);
        await TestDb.SeedTenantAsync(db, tenantB);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantA);
        await SeedData.EnsureRolePermissionsAsync(db, tenantB);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var toRevoke = await db.RolePermissions.FirstAsync(rp =>
            rp.RoleId == staffId && rp.TenantId == tenantA && rp.Permission == Permissions.AssetsEdit);
        db.RolePermissions.Remove(toRevoke);
        await db.SaveChangesAsync();

        Assert.True(await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == staffId && rp.TenantId == tenantB && rp.Permission == Permissions.AssetsEdit));
    }
}
