using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The policy is per device type per organisation. It cannot live on the shared Currency-style
/// LookupValue rows, which carry no TenantId by deliberate design - useful life is each
/// organisation's own accounting policy, not shared vocabulary.
/// </summary>
public class DepreciationPolicyApiTests
{
    [Fact]
    public async Task A_policy_is_scoped_to_its_tenant_and_stamped_automatically()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                DeviceType = DeviceTypes.Laptop,
                UsefulLifeMonths = 36,
                ResidualPercent = 10m
            });
            await db.SaveChangesAsync();

            var saved = await db.DepreciationPolicies.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal(DeviceTypes.Laptop, saved.DeviceType);
            Assert.Equal(36, saved.UsefulLifeMonths);
            Assert.Equal(10m, saved.ResidualPercent);
        }
    }

    [Fact]
    public async Task Two_tenants_may_each_have_their_own_policy_for_the_same_device_type()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantA, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });
            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantB, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 48, ResidualPercent = 0m
            });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.DepreciationPolicies.CountAsync());
        }
    }
}
