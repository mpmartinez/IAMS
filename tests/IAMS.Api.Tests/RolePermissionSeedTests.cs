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
        var tenant = await TestDb.SeedTenantAsync(db, tenantId);
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

        // New contract: provisioning stamps the tenant-level marker that replaces the old
        // per-role "has any grant row" heuristic.
        Assert.NotNull(tenant.RolePermissionsSeededAt);
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var tenant = await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterFirst = await db.RolePermissions.CountAsync();
        var stampedAtFirst = tenant.RolePermissionsSeededAt;
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterSecond = await db.RolePermissions.CountAsync();

        Assert.Equal(afterFirst, afterSecond);
        // The second call must be a true no-op once the marker is set - it should return before
        // even re-reading the clock, not merely happen to insert nothing.
        Assert.Equal(stampedAtFirst, tenant.RolePermissionsSeededAt);
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
        var tenantAEntity = await TestDb.SeedTenantAsync(db, tenantA);
        var tenantBEntity = await TestDb.SeedTenantAsync(db, tenantB);
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

        // Each tenant gets its own marker, stamped independently of the other's.
        Assert.NotNull(tenantAEntity.RolePermissionsSeededAt);
        Assert.NotNull(tenantBEntity.RolePermissionsSeededAt);
    }

    // -------------------------------------------------------------------------------------------
    // The Critical fix: the marker, not "does this role have any grant row", is what decides
    // whether a tenant gets (re-)provisioned. This is what closes the escalation chain where
    // emptying a built-in role's grants, assigning it, then restarting the app used to silently
    // restore every default permission onto the role the attacker had just been handed.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureRolePermissionsAsync_DoesNotReprovision_WhenMarkerIsSet_EvenIfARoleHasZeroGrants()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        // First call provisions normally and stamps the marker.
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        // Simulate PUT /api/roles/{admin-role-id} { "permissions": [] } - Admin now has zero
        // grant rows in this tenant, exactly as in the exploit. Every OTHER role's grants are
        // also cleared here so this test isolates the marker check itself: with grants left on
        // some other role, the tenant-level "already has grants" backfill check (used only when
        // the marker is null - see EnsureRolePermissionsAsync_StampsMarker... below) would also
        // happen to block re-provisioning, and this test would pass even if the marker check
        // were deleted.
        var adminId = (await db.Roles.FirstAsync(r => r.Name == Roles.Admin)).Id;
        var allGrants = await db.RolePermissions.Where(rp => rp.TenantId == tenantId).ToListAsync();
        Assert.NotEmpty(allGrants); // sanity: provisioning really did insert rows to empty
        db.RolePermissions.RemoveRange(allGrants);
        await db.SaveChangesAsync();

        // The next "application start" - this must NOT treat the now-fully-empty tenant as
        // unprovisioned and restore every role's defaults, because the marker (not the presence
        // of grant rows) is what EnsureRolePermissionsAsync now trusts.
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        Assert.False(await db.RolePermissions.AnyAsync(rp => rp.RoleId == adminId && rp.TenantId == tenantId));
        Assert.False(await db.RolePermissions.AnyAsync(rp => rp.TenantId == tenantId));
    }

    [Fact]
    public async Task EnsureRolePermissionsAsync_StampsMarker_WithoutInsertingRows_WhenTenantAlreadyHasGrants()
    {
        // Simulates a tenant provisioned before this fix shipped: it already has grant rows from
        // the OLD per-role heuristic, but the new column is null because it didn't exist yet.
        // On first startup after the upgrade this must stamp the marker and insert nothing - not
        // reprovision, which could resurrect a permission that tenant deliberately revoked.
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var tenant = await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);
        Assert.Null(tenant.RolePermissionsSeededAt);

        // Hand-seed a partial grant set for Staff only - as if an admin had already customised it
        // under the old logic - without ever calling EnsureRolePermissionsAsync.
        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(), RoleId = staffId, TenantId = tenantId, Permission = Permissions.AssetsView
        });
        await db.SaveChangesAsync();
        var countBefore = await db.RolePermissions.CountAsync();

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        Assert.NotNull(tenant.RolePermissionsSeededAt);
        Assert.Equal(countBefore, await db.RolePermissions.CountAsync());
        // The rest of Staff's defaults must NOT have been inserted alongside the hand-seeded one.
        Assert.False(await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == staffId && rp.TenantId == tenantId && rp.Permission == Permissions.AssetsCreate));
    }
}
