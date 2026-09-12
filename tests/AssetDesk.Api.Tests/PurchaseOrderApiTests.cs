using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
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
}
