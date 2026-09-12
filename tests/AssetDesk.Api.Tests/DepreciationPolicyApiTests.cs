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

    private static DepreciationPoliciesController ControllerFor(
        AssetDesk.Api.Data.AppDbContext db, ITenantProvider tenantProvider) =>
        new(db, new LookupService(db), tenantProvider);

    [Fact]
    public async Task Upsert_creates_a_policy_then_updates_it_in_place()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));

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
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));

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
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));

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
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));

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

    // ---------------------------------------------------------------------------------------
    // Cross-tenant isolation. The entity's global query filter is bypassed outright for a
    // super-admin (AppDbContext: `_tenantProvider.IsSuperAdmin() || ...`), and DeviceType is
    // only unique *within* a tenant - so a controller that leans on the filter alone reads,
    // rewrites and deletes other organisations' rows. Same hazard ReportsController already
    // keys around; these pin the controller side of it.
    // ---------------------------------------------------------------------------------------

    /// <summary>Seeds two tenants, only the second of which owns a Laptop policy.</summary>
    private static async Task<(Guid Caller, Guid Other)> SeedTwoTenantsAsync(
        AssetDesk.Api.Data.AppDbContext db, int otherUsefulLife = 48, decimal otherResidual = 0m)
    {
        var caller = Guid.NewGuid();
        var other = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, caller);
        await TestDb.SeedTenantAsync(db, other);

        db.DepreciationPolicies.Add(new DepreciationPolicy
        {
            TenantId = other,
            DeviceType = DeviceTypes.Laptop,
            UsefulLifeMonths = otherUsefulLife,
            ResidualPercent = otherResidual
        });
        await db.SaveChangesAsync();

        return (caller, other);
    }

    [Fact]
    public async Task Upsert_creates_a_policy_for_the_caller_rather_than_rewriting_another_tenants()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            var (caller, other) = await SeedTwoTenantsAsync(db);
            var tenants = new FakeTenantProvider(caller, isSuperAdmin: true);

            var result = await ControllerFor(db, tenants).Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });

            Assert.IsType<OkObjectResult>(result.Result);

            var all = await db.DepreciationPolicies.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, all.Count);

            // The other organisation's accounting policy is exactly as it was.
            var untouched = Assert.Single(all, p => p.TenantId == other);
            Assert.Equal(48, untouched.UsefulLifeMonths);
            Assert.Equal(0m, untouched.ResidualPercent);

            // And the caller now actually has one of its own.
            var mine = Assert.Single(all, p => p.TenantId == caller);
            Assert.Equal(36, mine.UsefulLifeMonths);
            Assert.Equal(10m, mine.ResidualPercent);
        }
    }

    [Fact]
    public async Task Delete_does_not_remove_another_tenants_policy()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            var (caller, other) = await SeedTwoTenantsAsync(db);
            var tenants = new FakeTenantProvider(caller, isSuperAdmin: true);

            var result = await ControllerFor(db, tenants).Delete(DeviceTypes.Laptop);

            Assert.IsType<NotFoundObjectResult>(result.Result);

            var survivor = Assert.Single(await db.DepreciationPolicies.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(other, survivor.TenantId);
        }
    }

    [Fact]
    public async Task GetAll_returns_only_the_callers_own_policies()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            var (caller, _) = await SeedTwoTenantsAsync(db);
            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = caller, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });
            await db.SaveChangesAsync();

            var tenants = new FakeTenantProvider(caller, isSuperAdmin: true);
            var result = await ControllerFor(db, tenants).GetAll();

            var payload = Assert.IsType<ApiResponse<List<DepreciationPolicyDto>>>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            // One row, not two - which is also what keeps Admin/Depreciation.razor's
            // ToDictionary(p => p.DeviceType) from throwing on a duplicate key.
            var only = Assert.Single(payload.Data!);
            Assert.Equal(DeviceTypes.Laptop, only.DeviceType);
            Assert.Equal(36, only.UsefulLifeMonths);
            Assert.Single(payload.Data!.GroupBy(p => p.DeviceType));
        }
    }

    [Fact]
    public async Task A_super_admin_with_no_organisation_selected_is_refused_rather_than_served_someone_elses()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            var (_, other) = await SeedTwoTenantsAsync(db);
            var noTenant = new FakeTenantProvider(null, isSuperAdmin: true);
            var controller = ControllerFor(db, noTenant);

            Assert.IsType<BadRequestObjectResult>((await controller.GetAll()).Result);

            Assert.IsType<BadRequestObjectResult>((await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            })).Result);

            Assert.IsType<BadRequestObjectResult>((await controller.Delete(DeviceTypes.Laptop)).Result);

            // Nothing was created, rewritten or removed on the way to those refusals.
            var untouched = Assert.Single(await db.DepreciationPolicies.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(other, untouched.TenantId);
            Assert.Equal(48, untouched.UsefulLifeMonths);
        }
    }
}
