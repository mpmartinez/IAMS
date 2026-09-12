using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class PurchaseOrderApiTests
{
    [Fact]
    public async Task A_new_order_starts_as_a_draft_with_nothing_received()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                TenantId = tenantId,
                PoNumber = 1,
                SupplierId = supplier.Id,
                Currency = Currencies.PHP,
                Status = PurchaseOrderStatus.Draft,
                OrderDate = new DateTime(2026, 9, 12),
                CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Laptop,
                Description = "Dell Latitude 5540",
                Quantity = 10,
                UnitPrice = 50000m
            });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var saved = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
            var line = Assert.Single(saved.Lines);
            Assert.Equal(10, line.Quantity);
            Assert.Equal(0, line.ReceivedQuantity);
        }
    }

    [Fact]
    public async Task Po_numbers_are_sequential_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var allocator = new PurchaseOrderNumberAllocator(db);

            Assert.Equal(1, await allocator.NextAsync(tenantId));

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Draft,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();

            Assert.Equal(2, await allocator.NextAsync(tenantId));
        }
    }

    [Fact]
    public async Task Po_numbers_restart_per_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var supplierA = new Supplier { TenantId = tenantA, Name = "Acme" };
            db.Suppliers.Add(supplierA);
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantA, PoNumber = 7, SupplierId = supplierA.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Draft,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();

            var allocator = new PurchaseOrderNumberAllocator(db);

            Assert.Equal(8, await allocator.NextAsync(tenantA));
            Assert.Equal(1, await allocator.NextAsync(tenantB));
        }
    }

    private static PurchaseOrdersController ControllerFor(
        AssetDesk.Api.Data.AppDbContext db, ITenantProvider tenants) =>
        new(db, tenants, new PurchaseOrderNumberAllocator(db), new LookupService(db));

    private static async Task<Supplier> SeedSupplierAsync(
        AssetDesk.Api.Data.AppDbContext db, Guid tenantId, string name = "Acme Computers")
    {
        var supplier = new Supplier { TenantId = tenantId, Name = name };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static CreatePurchaseOrderDto NewOrder(int supplierId, int quantity = 10) => new()
    {
        SupplierId = supplierId,
        Currency = Currencies.PHP,
        OrderDate = new DateTime(2026, 9, 12),
        Lines =
        [
            new PurchaseOrderLineInputDto
            {
                DeviceType = DeviceTypes.Laptop,
                Description = "Dell Latitude 5540",
                Quantity = quantity,
                UnitPrice = 50000m
            }
        ]
    };

    [Fact]
    public async Task Creating_an_order_allocates_the_next_number_and_starts_it_as_a_draft()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(NewOrder(supplier.Id));

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(1, saved.PoNumber);
            Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
            Assert.Equal(10, Assert.Single(saved.Lines).Quantity);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_line_quantity_of_zero_or_less_is_rejected(int quantity)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(NewOrder(supplier.Id, quantity));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.PurchaseOrders.ToListAsync());
        }
    }

    [Fact]
    public async Task An_order_with_no_lines_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var dto = NewOrder(supplier.Id) with { Lines = [] };
            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).Create(dto);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_supplier_from_another_tenant_cannot_be_ordered_from()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var bSupplier = await SeedSupplierAsync(db, tenantB);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantA))
                .Create(NewOrder(bSupplier.Id));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.PurchaseOrders.IgnoreQueryFilters().ToListAsync());
        }
    }

    [Fact]
    public async Task Sending_a_draft_makes_it_Ordered_and_sending_twice_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(NewOrder(supplier.Id));
            var order = await db.PurchaseOrders.SingleAsync();

            var first = await controller.Send(order.Id);
            Assert.IsType<OkObjectResult>(first.Result);
            Assert.Equal(PurchaseOrderStatus.Ordered,
                (await db.PurchaseOrders.SingleAsync()).Status);

            var second = await controller.Send(order.Id);
            Assert.IsType<BadRequestObjectResult>(second.Result);
        }
    }

    [Fact]
    public async Task A_received_order_cannot_be_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Received,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();
            var order = await db.PurchaseOrders.SingleAsync();

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).Cancel(order.Id);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(PurchaseOrderStatus.Received,
                (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }
}
