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
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            var bSupplier = new Supplier { TenantId = tenantB, Name = "Acme Computers" };
            db.Suppliers.Add(bSupplier);
            await db.SaveChangesAsync();

            // A super admin has no tenant of their own; the controller must refuse rather than
            // reach into whichever tenant's row happens to match.
            var result = await ControllerFor(db, new FakeTenantProvider(null, isSuperAdmin: true))
                .Update(bSupplier.Id, Dto("Renamed By Mistake"));

            Assert.IsNotType<OkObjectResult>(result.Result);
            var reloaded = await db.Suppliers.IgnoreQueryFilters().SingleAsync(s => s.Id == bSupplier.Id);
            Assert.Equal("Acme Computers", reloaded.Name);
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
}
