using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The rules the database itself holds. These run on SQLite, which proves the constraints are
/// modelled; the generated PostgreSQL SQL is read by hand and exercised on a real database before
/// merge.
/// </summary>
public class LicenceSchemaTests
{
    private static SoftwareLicence Licence(
        Guid tenantId, string name = "Microsoft 365 Business Standard", string model = LicenceModels.PerUser) =>
        new() { TenantId = tenantId, Name = name, LicenceModel = model };

    private static LicenceSeatAssignment Seat(
        Guid tenantId, int licenceId, string? userId = null, int? assetId = null) =>
        new()
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId,
            UserId = userId, AssetId = assetId, AssignedByUserId = "admin-1"
        };

    [Fact]
    public async Task A_licence_name_is_unique_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            db.SoftwareLicences.Add(Licence(tenantId));
            await db.SaveChangesAsync();

            db.SoftwareLicences.Add(Licence(tenantId));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task The_same_licence_name_is_allowed_in_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            db.SoftwareLicences.Add(Licence(tenantA));
            db.SoftwareLicences.Add(Licence(tenantB));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.SoftwareLicences.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task A_seat_with_no_target_is_refused_by_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_seat_naming_both_a_person_and_a_device_is_refused_by_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1", assetId: asset.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_person_holds_one_active_seat_per_licence_until_it_is_released()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var first = Seat(tenantId, licence.Id, userId: "user-1");
            db.LicenceSeatAssignments.Add(first);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var reloaded = await db.LicenceSeatAssignments.SingleAsync();
            reloaded.ReleasedAt = DateTime.UtcNow;
            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1"));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.LicenceSeatAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task A_device_holds_one_active_seat_per_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_device_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();
            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // The seat is deliberately not loaded: the database's cascade has to do this, not
            // EF's change tracker, because AssetsController.DeleteAsset loads only the asset.
            db.Assets.Remove(await db.Assets.SingleAsync());
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.LicenceSeatAssignments.CountAsync());
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task A_receipt_line_feeds_at_most_one_entitlement()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Software" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Status = PurchaseOrderStatus.Ordered, CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine { DeviceType = DeviceTypes.Software, Quantity = 50, UnitPrice = 700m });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var receipt = new GoodsReceipt
            {
                TenantId = tenantId, PurchaseOrderId = order.Id, RequestId = Guid.NewGuid(),
                ReceivedByUserId = "user-1"
            };
            receipt.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = order.Lines.First().Id, QuantityReceived = 50 });
            db.GoodsReceipts.Add(receipt);

            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var receiptLineId = receipt.Lines.First().Id;
            LicenceEntitlement Entry() => new()
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, SeatsAdded = 50, Cost = 35000m,
                GoodsReceiptLineId = receiptLineId, CreatedByUserId = "user-1"
            };

            db.LicenceEntitlements.Add(Entry());
            await db.SaveChangesAsync();

            db.LicenceEntitlements.Add(Entry());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
