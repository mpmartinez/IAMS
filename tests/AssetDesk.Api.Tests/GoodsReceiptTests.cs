using System.Data.Common;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>
    /// The over-receipt guard is a conditional UPDATE, not a read-then-compare. There is no
    /// in-memory pre-check left in ReceiveAsync, so the only thing that can refuse the second
    /// delivery below is the claim's own WHERE clause - the line already holds 8 of 10, and
    /// "ReceivedQuantity + 3 &lt;= Quantity" matches no row, so ExecuteUpdateAsync affects zero
    /// rows and the transaction is abandoned. The message is asserted in full because that exact
    /// string is produced nowhere but the claim-rejected branch.
    ///
    /// This proves the guard is *conditional*, which is what a single caller can demonstrate.
    /// It does not prove it is *atomic* - see the note on a genuine concurrency test in the
    /// task 8 report.
    /// </summary>
    [Fact]
    public async Task Over_receiving_is_refused_by_the_conditional_claim_not_by_a_stale_read()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            var service = ServiceFor(db);

            Assert.True((await service.ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1")).Success);

            var result = await service.ReceiveAsync(order.Id, Receive(line.Id, 3), "user-1");

            Assert.False(result.Success);
            Assert.Equal("Laptop: 2 of 10 outstanding, cannot receive 3.", result.Message);

            // Read the database, not the graph the service left behind: the claim writes round
            // the change tracker, so an assertion served by identity resolution would be
            // asserting against pre-claim values rather than what was committed.
            db.ChangeTracker.Clear();

            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(8, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(PurchaseOrderStatus.PartiallyReceived, reloaded.Status);
            Assert.Equal(8, await db.Assets.CountAsync());
            Assert.Equal(1, await db.GoodsReceipts.CountAsync());
        }
    }

    /// <summary>
    /// The counts the claim wrote are the ones a caller sees afterwards. ExecuteUpdateAsync
    /// bypasses the change tracker, so without the detach at the end of ReceiveAsync this query -
    /// tracked, exactly like the one the controller re-reads the order with to build its
    /// response - would hand back the stale instances and report nothing was received.
    /// </summary>
    [Fact]
    public async Task A_tracked_re_read_after_receiving_reports_the_claimed_quantity()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");

            var reloaded = await db.PurchaseOrders
                .Where(p => p.Id == order.Id)
                .Include(p => p.Lines)
                .FirstAsync();

            Assert.Equal(8, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(2, reloaded.Lines.First().OutstandingQuantity);
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
                null!,
                new PdfReportService());

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

    // ---------------------------------------------------------------------------------------
    // Multi-line deliveries. Every test above receives exactly one line, because the DTO helper
    // they share only builds one - which left the loop, the duplicate-line guard and the
    // partial-failure rollback (the whole point of doing this in one transaction) unexercised.
    // ---------------------------------------------------------------------------------------

    /// <summary>Seeds one order with two lines: 10 Laptops and 5 Monitors.</summary>
    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Laptops, PurchaseOrderLine Monitors)>
        SeedTwoLineOrderAsync(AssetDesk.Api.Data.AppDbContext db, Guid tenantId)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
            Currency = Currencies.PHP, Status = PurchaseOrderStatus.Ordered,
            OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop, Description = "Dell Latitude 5540",
            Quantity = 10, UnitPrice = 50000m
        });
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Monitor, Description = "Dell P2422H",
            Quantity = 5, UnitPrice = 9000m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order,
            order.Lines.First(l => l.DeviceType == DeviceTypes.Laptop),
            order.Lines.First(l => l.DeviceType == DeviceTypes.Monitor));
    }

    private static ReceiveGoodsDto ReceiveMany(params (int LineId, int Quantity)[] lines) => new()
    {
        ReceiptDate = new DateTime(2026, 9, 12),
        ExchangeRate = 1m,
        Lines = [.. lines.Select(l => new ReceiveLineDto
        {
            PurchaseOrderLineId = l.LineId, QuantityReceived = l.Quantity
        })]
    };

    [Fact]
    public async Task One_delivery_can_advance_two_lines_and_creates_assets_of_both_device_types()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, laptops, monitors) = await SeedTwoLineOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(
                order.Id, ReceiveMany((laptops.Id, 4), (monitors.Id, 5)), "user-1");

            Assert.True(result.Success);

            // Read the database, not the graph the service left behind: the claims write round
            // the change tracker.
            db.ChangeTracker.Clear();

            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(4, reloaded.Lines.Single(l => l.DeviceType == DeviceTypes.Laptop).ReceivedQuantity);
            Assert.Equal(5, reloaded.Lines.Single(l => l.DeviceType == DeviceTypes.Monitor).ReceivedQuantity);

            // 9 of the 15 ordered units have arrived, so the order is not finished yet.
            Assert.Equal(PurchaseOrderStatus.PartiallyReceived, reloaded.Status);

            var assets = await db.Assets.ToListAsync();
            Assert.Equal(9, assets.Count);
            Assert.Equal(4, assets.Count(a => a.DeviceType == DeviceTypes.Laptop));
            Assert.Equal(5, assets.Count(a => a.DeviceType == DeviceTypes.Monitor));
            Assert.Equal(9, assets.Select(a => a.AssetTag).Distinct().Count());

            // One delivery, one receipt - with a line per purchase-order line it touched.
            var receipt = await db.GoodsReceipts.Include(r => r.Lines).SingleAsync();
            Assert.Equal(2, receipt.Lines.Count);
        }
    }

    /// <summary>
    /// The core partial-failure promise: a delivery is all or nothing. Line 1 claims
    /// successfully - the row really is updated inside the transaction - and then line 2 is
    /// refused, and line 1's claim has to go away with it. Nothing else in the suite exercises
    /// a refusal that arrives *after* a successful claim in the same receipt.
    /// </summary>
    [Fact]
    public async Task A_line_refused_after_another_was_claimed_rolls_the_whole_delivery_back()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, laptops, monitors) = await SeedTwoLineOrderAsync(db, tenantId);

            // 4 of 10 Laptops is fine and is claimed; 6 of 5 Monitors is not.
            var result = await ServiceFor(db).ReceiveAsync(
                order.Id, ReceiveMany((laptops.Id, 4), (monitors.Id, 6)), "user-1");

            Assert.False(result.Success);
            Assert.Equal("Monitor: 5 of 5 outstanding, cannot receive 6.", result.Message);

            db.ChangeTracker.Clear();

            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.All(reloaded.Lines, l => Assert.Equal(0, l.ReceivedQuantity));
            Assert.Equal(PurchaseOrderStatus.Ordered, reloaded.Status);
            Assert.Equal(0, await db.Assets.CountAsync());
            Assert.Equal(0, await db.GoodsReceipts.CountAsync());
            Assert.Equal(0, await db.GoodsReceiptLines.CountAsync());
        }
    }

    [Fact]
    public async Task The_same_line_twice_in_one_delivery_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, laptops, _) = await SeedTwoLineOrderAsync(db, tenantId);

            // 6 + 6 is 12 against a line of 10. Claimed one at a time each would pass its own
            // predicate on a stale read; the guard is what stops the DTO getting that far.
            var result = await ServiceFor(db).ReceiveAsync(
                order.Id, ReceiveMany((laptops.Id, 6), (laptops.Id, 6)), "user-1");

            Assert.False(result.Success);
            Assert.Equal("The same line appears more than once.", result.Message);

            db.ChangeTracker.Clear();

            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.All(reloaded.Lines, l => Assert.Equal(0, l.ReceivedQuantity));
            Assert.Equal(0, await db.Assets.CountAsync());
            Assert.Equal(0, await db.GoodsReceipts.CountAsync());
        }
    }

    /// <summary>
    /// Records the SQL the context issues, so a test can assert the *order* of two statements.
    /// EF routes ExecuteUpdateAsync through the non-query path and SaveChanges/SELECT through
    /// the reader path, so both are captured into one list to keep the sequence intact.
    /// </summary>
    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Two receipts against *different* lines of one order do not contend: the per-line claim
    /// locks only the line it claims, so nothing serialises them. Each then reads the order's
    /// line totals seeing only its own line advanced, each computes PartiallyReceived, and both
    /// commit - leaving a fully delivered order stranded at PartiallyReceived with no way back,
    /// because the claim now refuses every further quantity.
    ///
    /// What fixes that is taking the order's own row lock before any line is claimed, so
    /// receipts against one order queue behind each other and the second reads totals that
    /// include the first. A lock is not observable from a single-threaded test; the statement
    /// that acquires it is. This asserts that statement is issued against PurchaseOrders
    /// *before* the first claim against PurchaseOrderLines - which is the whole of the fix, since
    /// PostgreSQL holds a row's write lock until the transaction ends.
    /// </summary>
    [Fact]
    public async Task The_orders_row_is_locked_before_any_line_is_claimed()
    {
        var tenantId = Guid.NewGuid();
        var recorder = new SqlRecorder();
        var (db, conn) = TestDb.Create(
            new FakeTenantProvider(tenantId), builder => builder.AddInterceptors(recorder));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, laptops, monitors) = await SeedTwoLineOrderAsync(db, tenantId);

            recorder.Commands.Clear();

            var result = await ServiceFor(db).ReceiveAsync(
                order.Id, ReceiveMany((laptops.Id, 4), (monitors.Id, 5)), "user-1");

            Assert.True(result.Success);

            var lockedOrder = recorder.Commands.FindIndex(
                c => c.Contains("UPDATE \"PurchaseOrders\"", StringComparison.Ordinal));
            var claimedLine = recorder.Commands.FindIndex(
                c => c.Contains("UPDATE \"PurchaseOrderLines\"", StringComparison.Ordinal));

            Assert.True(claimedLine >= 0, "No claim against PurchaseOrderLines was issued at all.");
            Assert.True(lockedOrder >= 0,
                "The order's row was never written before the lines were claimed, so nothing " +
                "serialises two receipts against different lines of the same order.");
            Assert.True(lockedOrder < claimedLine,
                $"The order's row was written at statement {lockedOrder} but the first line was " +
                $"claimed at {claimedLine}; the order lock has to come first.");
        }
    }

    /// <summary>
    /// Stands in for Npgsql's EnableRetryOnFailure strategy - SQLite has no retrying strategy of
    /// its own, so this is the only way to exercise a replay outside a real Postgres failover.
    /// It runs the delegate, discards that attempt's result exactly as a caller would if its
    /// acknowledgement never arrived, and then does what a real retrying strategy does before
    /// ever replaying anything: asks verifySucceeded whether the "lost" attempt actually landed.
    /// Only if it did not does it run the delegate a second time.
    /// </summary>
    private sealed class ReplayingExecutionStrategy : IExecutionStrategy, IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies _dependencies;

        public ReplayingExecutionStrategy(ExecutionStrategyDependencies dependencies) =>
            _dependencies = dependencies;

        public bool RetriesOnFailure => true;

        public IExecutionStrategy Create() => this;

        // EnsureCreated (schema setup, not ReceiveAsync) goes through this overload. It needs no
        // replay behaviour of its own, so it is a plain pass-through.
        public TResult Execute<TState, TResult>(
            TState state,
            Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) =>
            operation(_dependencies.CurrentContext.Context, state);

        public async Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
            CancellationToken cancellationToken)
        {
            var context = _dependencies.CurrentContext.Context;

            // ExecuteUpdateAsync and SaveChanges route through the context's execution strategy
            // too, with no verifySucceeded of their own - a plain pass-through for those, or the
            // conditional claim inside ReceiveAsync's delegate would fire twice on every replay
            // regardless of the marker, which is not the scenario this fake exists to model.
            if (verifySucceeded is null)
                return await operation(context, state, cancellationToken);

            // The one call that matters: discard this attempt's result exactly as a caller would
            // if a commit's acknowledgement never arrived, then ask - as a real retrying
            // strategy would before ever replaying - whether it actually landed.
            await operation(context, state, cancellationToken);

            var verified = await verifySucceeded(context, state, cancellationToken);
            if (verified.IsSuccessful)
                return verified.Result;

            return await operation(context, state, cancellationToken);
        }
    }

    /// <summary>
    /// ReceiveAsync has run through the execution strategy since it was first written, so a
    /// replay has always been possible; what changed is the fix that made a replay safe - the
    /// request id minted once outside the strategy and checked by verifySucceeded - added after
    /// exactly this failure was found: one delivery of 5 replaying into 10 received, 10 assets
    /// and two receipts. Nothing else in the suite drives an actual replay, so nothing else would
    /// notice that fix quietly regressing. ReplayingExecutionStrategy above models the failure
    /// that makes a replay happen at all - a commit whose acknowledgement never reached the
    /// caller - closely enough to prove the fix still holds rather than just the absence of
    /// symptoms on a provider (SQLite) that never replays anything.
    /// </summary>
    [Fact]
    public async Task A_replayed_delivery_is_not_recorded_twice()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(
            new FakeTenantProvider(tenantId),
            builder => builder.ReplaceService<IExecutionStrategyFactory, ReplayingExecutionStrategy>());
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 5), "user-1");

            Assert.True(result.Success);

            db.ChangeTracker.Clear();

            Assert.Equal(1, await db.GoodsReceipts.CountAsync());
            Assert.Equal(5, await db.Assets.CountAsync());
            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(5, reloaded.Lines.First().ReceivedQuantity);
        }
    }
}
