using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

public class GoodsReceiptTests
{
    [Fact]
    public async Task An_asset_remembers_the_receipt_line_that_created_it()
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
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.USD, Status = PurchaseOrderStatus.Ordered,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Laptop, Quantity = 10, UnitPrice = 1200m
            });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var receipt = new GoodsReceipt
            {
                TenantId = tenantId,
                PurchaseOrderId = order.Id,
                ReceiptDate = new DateTime(2026, 9, 12),
                ExchangeRate = 58.20m,
                ReceivedByUserId = "user-1"
            };
            receipt.Lines.Add(new GoodsReceiptLine
            {
                PurchaseOrderLineId = order.Lines.First().Id,
                QuantityReceived = 2
            });
            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var asset = new Asset
            {
                TenantId = tenantId,
                AssetTag = "LAP-0001",
                DeviceType = DeviceTypes.Laptop,
                Status = AssetStatus.Available,
                GoodsReceiptLineId = receipt.Lines.First().Id
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            var saved = await db.Assets.SingleAsync(a => a.AssetTag == "LAP-0001");
            Assert.Equal(receipt.Lines.First().Id, saved.GoodsReceiptLineId);

            // A hand-entered asset keeps it null - the system knows which assets it can
            // account for and which it cannot.
            var manual = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002");
            Assert.Null(manual.GoodsReceiptLineId);
        }
    }

    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Line)> SeedOrderedAsync(
        AssetDesk.Api.Data.AppDbContext db, Guid tenantId,
        string currency = "PHP", int quantity = 10, decimal unitPrice = 50000m)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
            Currency = currency, Status = PurchaseOrderStatus.Ordered,
            OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop,
            Description = "Dell Latitude 5540",
            Quantity = quantity,
            UnitPrice = unitPrice
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order, order.Lines.First());
    }

    private static GoodsReceiptService ServiceFor(AssetDesk.Api.Data.AppDbContext db) =>
        new(db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);

    private static ReceiveGoodsDto Receive(int lineId, int qty, decimal rate = 1m, DateTime? date = null) => new()
    {
        ReceiptDate = date ?? new DateTime(2026, 9, 12),
        ExchangeRate = rate,
        Lines = [new ReceiveLineDto { PurchaseOrderLineId = lineId, QuantityReceived = qty }]
    };

    [Fact]
    public async Task Receiving_part_of_a_line_leaves_the_order_partially_received()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");

            Assert.True(result.Success);
            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.PartiallyReceived, reloaded.Status);
            Assert.Equal(8, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(8, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task Receiving_the_rest_closes_the_order()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            var service = ServiceFor(db);

            await service.ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");
            var result = await service.ReceiveAsync(order.Id, Receive(line.Id, 2), "user-1");

            Assert.True(result.Success);
            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.Received, reloaded.Status);
            Assert.Equal(10, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(10, await db.Assets.CountAsync());
            Assert.Equal(2, await db.GoodsReceipts.CountAsync());
        }
    }

    [Fact]
    public async Task Receiving_more_than_was_ordered_is_refused_and_changes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 11), "user-1");

            Assert.False(result.Success);
            Assert.Contains("10", result.Message);
            Assert.Equal(0, await db.Assets.CountAsync());
            Assert.Equal(0, await db.GoodsReceipts.CountAsync());
            Assert.Equal(0, (await db.PurchaseOrders.Include(p => p.Lines).SingleAsync()).Lines.First().ReceivedQuantity);
        }
    }

    [Fact]
    public async Task Receiving_more_than_remains_after_a_partial_receipt_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            var service = ServiceFor(db);
            await service.ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");

            var result = await service.ReceiveAsync(order.Id, Receive(line.Id, 3), "user-1");

            Assert.False(result.Success);
            Assert.Equal(8, await db.Assets.CountAsync());
        }
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Received)]
    public async Task Receiving_against_a_closed_or_unsent_order_is_refused(string status)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            order.Status = status;
            await db.SaveChangesAsync();

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task A_quantity_of_zero_or_less_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 0), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task Each_created_asset_carries_the_line_price_the_order_currency_and_the_receipt_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId, Currencies.USD, 10, 1200m);

            await ServiceFor(db).ReceiveAsync(
                order.Id, Receive(line.Id, 2, rate: 58.20m, date: new DateTime(2026, 9, 12)), "user-1");

            var assets = await db.Assets.ToListAsync();
            Assert.Equal(2, assets.Count);
            foreach (var asset in assets)
            {
                Assert.Equal(1200m, asset.PurchasePrice);
                Assert.Equal(Currencies.USD, asset.Currency);
                Assert.Equal(58.20m, asset.ExchangeRate);
                Assert.Equal(new DateTime(2026, 9, 12), asset.PurchaseDate);
                Assert.Equal(DeviceTypes.Laptop, asset.DeviceType);
                Assert.Equal(AssetStatus.Available, asset.Status);
                Assert.NotNull(asset.GoodsReceiptLineId);
            }

            // Distinct tags, not ten copies of one.
            Assert.Equal(2, assets.Select(a => a.AssetTag).Distinct().Count());
        }
    }

    [Fact]
    public async Task Two_deliveries_at_different_rates_produce_assets_carrying_each_batchs_own_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId, Currencies.USD, 10, 1200m);
            var service = ServiceFor(db);

            await service.ReceiveAsync(order.Id, Receive(line.Id, 8, rate: 58.20m), "user-1");
            await service.ReceiveAsync(order.Id, Receive(line.Id, 2, rate: 52.00m), "user-1");

            var rates = await db.Assets.GroupBy(a => a.ExchangeRate)
                .Select(g => new { Rate = g.Key, Count = g.Count() })
                .ToListAsync();

            Assert.Equal(8, Assert.Single(rates, r => r.Rate == 58.20m).Count);
            Assert.Equal(2, Assert.Single(rates, r => r.Rate == 52.00m).Count);
        }
    }

    [Fact]
    public async Task An_unknown_purchase_order_line_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, _) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(99999, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task A_super_admin_in_one_tenant_cannot_receive_against_another_tenants_order()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // The context bypasses the global filter for a super admin, so only the controller's
        // explicit tenant check stands between the caller and tenant B's order.
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var (orderB, lineB) = await SeedOrderedAsync(db, tenantB);

            // A super admin whose CURRENT tenant is A - GetCurrentTenantId returns A, so the
            // no-tenant-selected guard does not fire and only the .Where can refuse this.
            var controller = new PurchaseOrdersController(
                db,
                new FakeTenantProvider(tenantA, isSuperAdmin: true),
                new PurchaseOrderNumberAllocator(db),
                new LookupService(db),
                ServiceFor(db),
                null!);

            var result = await controller.Receive(orderB.Id, Receive(lineB.Id, 1));

            Assert.IsNotType<OkObjectResult>(result.Result);
            Assert.Equal(0, await db.Assets.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, (await db.PurchaseOrders.IgnoreQueryFilters()
                .Include(p => p.Lines).SingleAsync()).Lines.First().ReceivedQuantity);
        }
    }

    [Fact]
    public async Task A_line_belonging_to_a_different_order_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (orderA, _) = await SeedOrderedAsync(db, tenantId);

            var supplierB = new Supplier { TenantId = tenantId, Name = "Beta Supplies" };
            db.Suppliers.Add(supplierB);
            await db.SaveChangesAsync();
            var orderB = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 2, SupplierId = supplierB.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Ordered,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            };
            orderB.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Monitor, Quantity = 5, UnitPrice = 9000m
            });
            db.PurchaseOrders.Add(orderB);
            await db.SaveChangesAsync();

            // Receiving order A but naming a line that belongs to order B.
            var result = await ServiceFor(db)
                .ReceiveAsync(orderA.Id, Receive(orderB.Lines.First().Id, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }
}
