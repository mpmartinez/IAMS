using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class SupplierApiTests
{
    private static SuppliersController ControllerFor(
        AssetDesk.Api.Data.AppDbContext db, ITenantProvider tenants) => new(db, tenants);

    private static UpsertSupplierDto Dto(string name) => new()
    {
        Name = name,
        ContactName = "Maria Santos",
        Email = "maria@example.com",
        Phone = "+63 2 8888 0000"
    };

    [Fact]
    public async Task A_supplier_is_created_against_the_callers_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(Dto("Acme Computers"));

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.Suppliers.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal("Acme Computers", saved.Name);
            Assert.True(saved.IsActive);
        }
    }

    /// <summary>
    /// UpsertSupplierDto.IsActive defaults to true, so the ordinary create is unaffected - but a
    /// caller that says false means it, and a supplier that comes back active despite having
    /// asked for the opposite can be ordered from.
    /// </summary>
    [Fact]
    public async Task A_supplier_created_as_inactive_stays_inactive()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(Dto("Acme Computers") with { IsActive = false });

            Assert.IsType<CreatedAtActionResult>(result.Result);
            Assert.False((await db.Suppliers.SingleAsync()).IsActive);
        }
    }

    [Fact]
    public async Task A_duplicate_name_within_a_tenant_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(Dto("Acme Computers"));

            var result = await controller.Create(Dto("Acme Computers"));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(1, await db.Suppliers.CountAsync());
        }
    }

    [Fact]
    public async Task Two_tenants_may_each_have_a_supplier_of_the_same_name()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            await ControllerFor(db, new FakeTenantProvider(tenantA)).Create(Dto("Acme Computers"));
            await ControllerFor(db, new FakeTenantProvider(tenantB)).Create(Dto("Acme Computers"));

            Assert.Equal(2, await db.Suppliers.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_edit_another_tenants_supplier()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            var bSupplier = new Supplier { TenantId = tenantB, Name = "Acme Computers" };
            db.Suppliers.Add(bSupplier);
            await db.SaveChangesAsync();

            // A super admin whose current tenant is A must still be refused when the row
            // belongs to tenant B. The global query filter's IsSuperAdmin() bypass lets tenant
            // B's row through - it is the explicit .Where(TenantId == tenantId) in Update that
            // must be the thing doing the refusing.
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .Update(bSupplier.Id, Dto("Renamed By Mistake"));

            Assert.IsNotType<OkObjectResult>(result.Result);
            var reloaded = await db.Suppliers.IgnoreQueryFilters().SingleAsync(s => s.Id == bSupplier.Id);
            Assert.Equal("Acme Computers", reloaded.Name);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_deactivate_another_tenants_supplier()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            var bSupplier = new Supplier { TenantId = tenantB, Name = "Acme Computers" };
            db.Suppliers.Add(bSupplier);
            await db.SaveChangesAsync();

            // Same shape as the Update test above: a super admin in tenant A must not be able
            // to deactivate tenant B's supplier via Delete.
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .Delete(bSupplier.Id);

            Assert.IsNotType<OkObjectResult>(result.Result);
            var reloaded = await db.Suppliers.IgnoreQueryFilters().SingleAsync(s => s.Id == bSupplier.Id);
            Assert.True(reloaded.IsActive);
        }
    }

    [Fact]
    public async Task Deactivating_a_supplier_keeps_the_row()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(Dto("Acme Computers"));
            var supplier = await db.Suppliers.SingleAsync();

            await controller.Delete(supplier.Id);

            // Purchase orders reference suppliers, so a supplier is deactivated rather than
            // deleted - the same reasoning LookupValue documents for its rows.
            var reloaded = await db.Suppliers.SingleAsync();
            Assert.False(reloaded.IsActive);
        }
    }

    [Fact]
    public async Task Updating_a_deactivated_supplier_with_IsActive_true_reactivates_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(Dto("Acme Computers"));
            var supplier = await db.Suppliers.SingleAsync();
            await controller.Delete(supplier.Id);

            // A deactivated supplier is not a dead end: reactivating it (rather than creating a
            // duplicate) must be possible via Update.
            var result = await controller.Update(supplier.Id, Dto("Acme Computers") with { IsActive = true });

            Assert.IsType<OkObjectResult>(result.Result);
            var reloaded = await db.Suppliers.SingleAsync();
            Assert.True(reloaded.IsActive);
            Assert.Equal(1, await db.Suppliers.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public void Both_procurement_keys_are_in_the_catalog_under_one_group()
    {
        var view = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.ProcurementView);
        var manage = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.ProcurementManage);

        Assert.Equal("iams:procurement:view", view.Key);
        Assert.Equal("iams:procurement:manage", manage.Key);
        Assert.Equal("Procurement", view.Group);
        Assert.Equal("Procurement", manage.Group);
    }

    [Fact]
    public void Staff_can_run_procurement_and_an_Auditor_can_only_read_it()
    {
        var staff = AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Staff);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementView, staff);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementManage, staff);

        var auditor = AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Auditor);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementView, auditor);
        Assert.DoesNotContain(AssetDesk.Api.Authorization.Permissions.ProcurementManage, auditor);
    }
}
