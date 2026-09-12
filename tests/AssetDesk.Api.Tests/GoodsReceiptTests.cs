using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

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
}
