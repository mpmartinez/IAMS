using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        new(db, tenants, new PurchaseOrderNumberAllocator(db), new LookupService(db),
            new GoodsReceiptService(db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance),
            NullLogger<PurchaseOrdersController>.Instance, new PdfReportService());

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
    public async Task A_deactivated_supplier_cannot_be_ordered_from()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            // Deactivating is how a supplier is retired - the row stays because orders reference
            // it. The picker hides inactive suppliers, but a stale tab or a scripted client can
            // still name the id, and that must not produce a numbered, receivable order.
            await new SuppliersController(db, new FakeTenantProvider(tenantId)).Delete(supplier.Id);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(NewOrder(supplier.Id));

            var refusal = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<PurchaseOrderDto>>(refusal.Value);
            Assert.Contains("inactive", body.Message);
            Assert.Empty(await db.PurchaseOrders.ToListAsync());
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

    /// <summary>
    /// Create only produces a Draft, and nothing can be received against one. Send is where the
    /// order becomes a real commitment, so a supplier retired between the two has to stop it
    /// there - otherwise retiring a supplier never closes its pipeline, it only stops new drafts.
    ///
    /// This test and the next cover the flag rather than the mechanism: Delete and Update are two
    /// routes to the same IsActive = false, and a gate written against only one is not a gate.
    /// </summary>
    [Fact]
    public async Task A_draft_cannot_be_sent_once_its_supplier_has_been_deleted()
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

            await new SuppliersController(db, new FakeTenantProvider(tenantId)).Delete(supplier.Id);

            var result = await controller.Send(order.Id);

            var refusal = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<PurchaseOrderDto>>(refusal.Value);
            Assert.Contains("inactive", body.Message);
            Assert.Equal(PurchaseOrderStatus.Draft,
                (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task A_draft_cannot_be_sent_once_its_supplier_has_been_marked_inactive()
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

            // The other route to retirement: the Active checkbox on the supplier editor.
            await new SuppliersController(db, new FakeTenantProvider(tenantId))
                .Update(supplier.Id, new UpsertSupplierDto { Name = supplier.Name, IsActive = false });

            var result = await controller.Send(order.Id);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(PurchaseOrderStatus.Draft,
                (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }

    /// <summary>
    /// The other side of the gate. Cancelling is how an order to a supplier nobody deals with
    /// any more gets taken off the books; refusing it would strand the draft forever.
    /// </summary>
    [Fact]
    public async Task A_draft_can_still_be_cancelled_once_its_supplier_has_been_retired()
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

            await new SuppliersController(db, new FakeTenantProvider(tenantId)).Delete(supplier.Id);

            var result = await controller.Cancel(order.Id);

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(PurchaseOrderStatus.Cancelled,
                (await db.PurchaseOrders.SingleAsync()).Status);
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

    /// <summary>
    /// The rate lives on the receipt, not the order, so that two deliveries against one USD
    /// order can carry the rates they were actually booked at. Until the detail read returned
    /// the receipts, nothing outside the individual asset records ever showed either rate and
    /// there was no way to reconcile a delivery against its invoice.
    /// </summary>
    [Fact]
    public async Task The_detail_read_returns_every_delivery_newest_first_with_its_own_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var supplier = await SeedSupplierAsync(db, tenantId);

            var order = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.USD, Status = PurchaseOrderStatus.Ordered,
                OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Laptop, Description = "Dell Latitude 5540",
                Quantity = 10, UnitPrice = 1200m
            });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();
            var lineId = order.Lines.First().Id;

            var service = new GoodsReceiptService(
                db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);

            await service.ReceiveAsync(tenantId, order.Id, new ReceiveGoodsDto
            {
                ReceiptDate = new DateTime(2026, 9, 10),
                ExchangeRate = 58.20m,
                Notes = "Invoice 4471",
                Lines = [new ReceiveLineDto { PurchaseOrderLineId = lineId, QuantityReceived = 8 }]
            }, "user-1");

            await service.ReceiveAsync(tenantId, order.Id, new ReceiveGoodsDto
            {
                ReceiptDate = new DateTime(2026, 9, 12),
                ExchangeRate = 56.90m,
                Lines = [new ReceiveLineDto { PurchaseOrderLineId = lineId, QuantityReceived = 2 }]
            }, "user-1");

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).GetById(order.Id);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var dto = Assert.IsType<ApiResponse<PurchaseOrderDto>>(ok.Value).Data!;

            Assert.Equal(2, dto.Receipts.Count);

            var latest = dto.Receipts[0];
            Assert.Equal(new DateTime(2026, 9, 12), latest.ReceiptDate);
            Assert.Equal(56.90m, latest.ExchangeRate);
            Assert.Equal(2, latest.TotalUnits);

            var first = dto.Receipts[1];
            Assert.Equal(new DateTime(2026, 9, 10), first.ReceiptDate);
            Assert.Equal(58.20m, first.ExchangeRate);
            Assert.Equal(8, first.TotalUnits);
            Assert.Equal("Invoice 4471", first.Notes);
            Assert.Equal("Maria Santos", first.ReceivedByName);
            Assert.Equal(DeviceTypes.Laptop, Assert.Single(first.Lines).DeviceType);
        }
    }

    /// <summary>
    /// A receipt has to outlive the account that recorded it, so its receiver id can point at a
    /// user row that is gone. The name is then null - never the raw Identity guid, which is what
    /// a "?? ReceivedByUserId" fallback would put in front of the user.
    /// </summary>
    [Fact]
    public async Task A_delivery_whose_receiver_no_longer_exists_has_no_receiver_name()
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
            await controller.Send(order.Id);

            var service = new GoodsReceiptService(
                db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);
            var receipt = await service.ReceiveAsync(tenantId, order.Id, new ReceiveGoodsDto
            {
                ReceiptDate = new DateTime(2026, 9, 12),
                ExchangeRate = 1m,
                Lines =
                [
                    new ReceiveLineDto
                    {
                        PurchaseOrderLineId = (await db.PurchaseOrderLines.SingleAsync()).Id,
                        QuantityReceived = 3
                    }
                ]
            }, "departed-user-id");
            Assert.True(receipt.Success);
            Assert.False(await db.Users.AnyAsync(u => u.Id == "departed-user-id"));

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).GetById(order.Id);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var delivery = Assert.Single(Assert.IsType<ApiResponse<PurchaseOrderDto>>(ok.Value).Data!.Receipts);
            Assert.Equal(3, delivery.TotalUnits);
            Assert.Null(delivery.ReceivedByName);
        }
    }

    /// <summary>
    /// The list is the other half of that decision: the receipts are a per-row join for data the
    /// list renders nothing from, so only the detail read pays for them.
    /// </summary>
    [Fact]
    public async Task The_list_read_does_not_carry_the_deliveries()
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
            await controller.Send(order.Id);
            await controller.Receive(order.Id, new ReceiveGoodsDto
            {
                ReceiptDate = new DateTime(2026, 9, 12),
                ExchangeRate = 1m,
                Lines =
                [
                    new ReceiveLineDto
                    {
                        PurchaseOrderLineId = (await db.PurchaseOrderLines.SingleAsync()).Id,
                        QuantityReceived = 4
                    }
                ]
            });

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).GetAll();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var orders = Assert.IsType<ApiResponse<List<PurchaseOrderDto>>>(ok.Value).Data!;
            Assert.Empty(Assert.Single(orders).Receipts);
        }
    }

    [Fact]
    public async Task The_pdf_renders_and_is_a_real_pdf()
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

            var pdfController = new PurchaseOrdersController(
                db, new FakeTenantProvider(tenantId), new PurchaseOrderNumberAllocator(db),
                new LookupService(db), null!, NullLogger<PurchaseOrdersController>.Instance,
                new PdfReportService());

            var file = Assert.IsType<FileContentResult>(await pdfController.GetPdf(order.Id));

            Assert.Equal("application/pdf", file.ContentType);
            Assert.True(file.FileContents.Length > 1000);
            // %PDF- magic - QuestPDF would throw on a column mismatch before reaching here.
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(file.FileContents, 0, 5));
        }
    }
}
