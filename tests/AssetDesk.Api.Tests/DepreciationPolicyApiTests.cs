using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
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

    [Fact]
    public void The_depreciation_key_is_in_the_catalog_under_its_own_group()
    {
        var descriptor = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.DepreciationManage);

        Assert.Equal("iams:depreciation:manage", descriptor.Key);
        Assert.Equal("Depreciation", descriptor.Group);
    }

    [Fact]
    public void Admin_and_SuperAdmin_get_the_depreciation_key_by_default()
    {
        Assert.Contains(
            AssetDesk.Api.Authorization.Permissions.DepreciationManage,
            AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Admin));
        Assert.Contains(
            AssetDesk.Api.Authorization.Permissions.DepreciationManage,
            AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.SuperAdmin));
    }

    private static DepreciationPoliciesController ControllerFor(AssetDesk.Api.Data.AppDbContext db) =>
        new(db, new LookupService(db));

    [Fact]
    public async Task Upsert_creates_a_policy_then_updates_it_in_place()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });
            await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 48, ResidualPercent = 5m
            });

            var saved = Assert.Single(await db.DepreciationPolicies.ToListAsync());
            Assert.Equal(48, saved.UsefulLifeMonths);
            Assert.Equal(5m, saved.ResidualPercent);
            Assert.NotNull(saved.UpdatedAt);
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(36, -1)]
    [InlineData(36, 101)]
    public async Task Invalid_life_or_residual_is_rejected(int months, decimal residual)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            var result = await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = months, ResidualPercent = residual
            });

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.DepreciationPolicies.ToListAsync());
        }
    }

    [Fact]
    public async Task An_unknown_device_type_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            var result = await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = "Submarine", UsefulLifeMonths = 36, ResidualPercent = 0m
            });

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task Delete_removes_the_policy_for_that_device_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            // Upsert and verify it succeeded
            var upsertResult = await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 0m
            });
            Assert.IsType<OkObjectResult>(upsertResult.Result);

            // Verify the policy exists before deletion
            var policiesBeforeDelete = await db.DepreciationPolicies.ToListAsync();
            Assert.Single(policiesBeforeDelete);

            // Delete and verify it succeeded
            var deleteResult = await controller.Delete(DeviceTypes.Laptop);
            Assert.IsType<OkObjectResult>(deleteResult.Result);

            // Verify the policy is gone
            Assert.Empty(await db.DepreciationPolicies.ToListAsync());
        }
    }
}
