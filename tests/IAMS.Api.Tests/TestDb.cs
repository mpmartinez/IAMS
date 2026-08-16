using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public static class TestDb
{
    public static (AppDbContext Db, SqliteConnection Connection) Create(
        ITenantProvider? tenantProvider = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        // Never use the tenant-provider-less constructor here. EF Core extracts
        // _tenantProvider.GetCurrentTenantId() into a query parameter and evaluates it
        // eagerly, so the `_tenantProvider == null` guard inside the global query filters
        // does NOT short-circuit the way C# || would. A null provider throws
        // NullReferenceException on the first query. A super-admin provider is the
        // supported way to see across every tenant.
        var db = new AppDbContext(
            options,
            tenantProvider ?? new FakeTenantProvider(null, isSuperAdmin: true));

        db.Database.EnsureCreated();
        return (db, connection);
    }

    public static async Task<Tenant> SeedTenantAsync(AppDbContext db, Guid tenantId)
    {
        var tenant = SubscriptionTiers.CreateWithLimits("Test Agency", $"test-{tenantId:N}", SubscriptionTiers.Pro);
        tenant.Id = tenantId;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public static async Task<ApplicationUser> SeedUserAsync(
        AppDbContext db, Guid tenantId, string id, string fullName)
    {
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{id}@test.local",
            Email = $"{id}@test.local",
            FullName = fullName,
            TenantId = tenantId
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Asset> SeedAssetAsync(
        AppDbContext db, Guid tenantId, string assetTag, string status = AssetStatus.Available)
    {
        var asset = new Asset
        {
            TenantId = tenantId,
            AssetTag = assetTag,
            DeviceType = DeviceTypes.Laptop,
            Status = status
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
