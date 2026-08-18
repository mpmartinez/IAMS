using AssetDesk.Api.Authorization;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

public class PermissionResolverTests
{
    private static async Task<ApplicationRole> AddRoleAsync(AppDbContext db, string name, Guid? tenantId = null)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}-{tenantId?.ToString("N")[..4] ?? "global"}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = tenantId is null,
            TenantId = tenantId
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task GrantAsync(AppDbContext db, string roleId, Guid tenantId, params string[] permissions)
    {
        foreach (var p in permissions)
        {
            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(), RoleId = roleId, TenantId = tenantId, Permission = p
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolves_GrantsForTheRoleInThatTenant()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var role = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, role.Id, tenantId, Permissions.AssetsView, Permissions.AssetsEdit);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Staff], tenantId);

        Assert.Equal(
            new[] { Permissions.AssetsEdit, Permissions.AssetsView },
            result.OrderBy(x => x));
    }

    [Fact]
    public async Task DoesNotLeakAnotherTenantsGrants()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, mine);
        await TestDb.SeedTenantAsync(db, theirs);
        var role = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, role.Id, mine, Permissions.AssetsView);
        await GrantAsync(db, role.Id, theirs, Permissions.AssetsDelete);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Staff], mine);

        Assert.Equal([Permissions.AssetsView], result);
    }

    [Fact]
    public async Task UnionsAcrossRoles_AndDeduplicates()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var admin = await AddRoleAsync(db, Roles.Admin);
        var staff = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, admin.Id, tenantId, Permissions.AssetsView, Permissions.AssetsDelete);
        await GrantAsync(db, staff.Id, tenantId, Permissions.AssetsView);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Admin, Roles.Staff], tenantId);

        Assert.Equal(
            new[] { Permissions.AssetsDelete, Permissions.AssetsView },
            result.OrderBy(x => x));
    }

    [Fact]
    public async Task ReturnsEmpty_ForNoRoles()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;

        var result = await new PermissionResolver(db).GetPermissionsAsync([], Guid.NewGuid());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReturnsEmpty_ForARoleWithNoGrants()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        await AddRoleAsync(db, Roles.Employee);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Employee], tenantId);

        Assert.Empty(result);
    }
}
