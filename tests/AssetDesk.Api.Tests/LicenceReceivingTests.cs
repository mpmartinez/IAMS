using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// A Software line on a purchase order becomes seats on a licence, not an asset per seat. Every
/// refusal is asserted to write nothing at all - no receipt, no entitlement, no licence, no asset,
/// no advanced received quantity - because a partial delivery is exactly what the transaction
/// around receiving exists to prevent.
/// </summary>
public class LicenceReceivingTests
{
    private static readonly DateTime ReceiptDate = new(2026, 9, 12);

    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Software, PurchaseOrderLine Laptop)> SeedOrderAsync(
        AppDbContext db, Guid tenantId, string currency = Currencies.PHP)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Software" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id, Currency = currency,
            Status = PurchaseOrderStatus.Ordered, OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Software, Description = "Microsoft 365 Business Standard",
            Quantity = 50, UnitPrice = 700m
        });
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop, Description = "Dell Latitude 5540",
            Quantity = 2, UnitPrice = 50000m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order, order.Lines.Single(l => l.DeviceType == DeviceTypes.Software),
            order.Lines.Single(l => l.DeviceType == DeviceTypes.Laptop));
    }

    private static GoodsReceiptService ServiceFor(AppDbContext db) =>
        new(db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);

    private static ReceiveGoodsDto Receive(decimal rate, params ReceiveLineDto[] lines) => new()
    {
        ReceiptDate = ReceiptDate,
        ExchangeRate = rate,
        Notes = "INV-2211",
        Lines = [.. lines]
    };

    private static ReceiveLineDto Line(int lineId, int quantity) =>
        new() { PurchaseOrderLineId = lineId, QuantityReceived = quantity };

    private static NewLicenceInputDto NewLicence(string name = "Microsoft 365 Business Standard") =>
        new() { Name = name, Publisher = "Microsoft", LicenceModel = LicenceModels.PerUser };

    private static async Task AssertNothingWrittenAsync(AppDbContext db, int licencesBefore)
    {
        db.ChangeTracker.Clear();
        Assert.Empty(db.GoodsReceipts.IgnoreQueryFilters());
        Assert.Empty(db.LicenceEntitlements.IgnoreQueryFilters());
        Assert.Empty(db.Assets.IgnoreQueryFilters());
        Assert.Equal(licencesBefore, await db.SoftwareLicences.IgnoreQueryFilters().CountAsync());
        Assert.All(await db.PurchaseOrderLines.ToListAsync(), l => Assert.Equal(0, l.ReceivedQuantity));
    }

    [Fact]
    public async Task A_software_line_becomes_an_entitlement_rather_than_assets()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId, Currencies.USD);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(58.20m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Empty(db.Assets);
            var entry = await db.LicenceEntitlements.SingleAsync();
            Assert.Equal(licence.Id, entry.SoftwareLicenceId);
            Assert.Equal(50, entry.SeatsAdded);
            Assert.Equal(35000m, entry.Cost);
            Assert.Equal(Currencies.USD, entry.Currency);
            Assert.Equal(58.20m, entry.ExchangeRate);
            Assert.Equal(ReceiptDate, entry.EntitlementDate);
            Assert.Equal((await db.GoodsReceiptLines.SingleAsync()).Id, entry.GoodsReceiptLineId);
            Assert.Equal(50, (await db.PurchaseOrderLines.SingleAsync(l => l.Id == software.Id)).ReceivedQuantity);
        }
    }

    [Fact]
    public async Task A_renewal_adds_no_seats_and_moves_the_expiry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: new DateTime(2026, 9, 30));
            await LicenceTestKit.SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);
            var renewedTo = new DateTime(2027, 9, 30);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    SoftwareLicenceId = licence.Id,
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = renewedTo
                }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(50, await db.LicenceEntitlements.SumAsync(e => e.SeatsAdded));
            Assert.Equal(renewedTo, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            var renewal = await db.LicenceEntitlements.SingleAsync(e => e.GoodsReceiptLineId != null);
            Assert.Equal(0, renewal.SeatsAdded);
            Assert.Equal(renewedTo, renewal.ExpiresAtAfter);
            Assert.Equal(50, (await db.PurchaseOrderLines.SingleAsync(l => l.Id == software.Id)).ReceivedQuantity);
        }
    }

    [Fact]
    public async Task A_new_licence_is_created_with_the_delivery_that_brings_its_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            var licence = await db.SoftwareLicences.SingleAsync();
            Assert.Equal(tenantId, licence.TenantId);
            Assert.Equal("Microsoft 365 Business Standard", licence.Name);
            Assert.Equal(LicenceModels.PerUser, licence.LicenceModel);
            Assert.Equal(order.SupplierId, licence.SupplierId);
            Assert.Equal(licence.Id, (await db.LicenceEntitlements.SingleAsync()).SoftwareLicenceId);
        }
    }

    [Fact]
    public async Task A_mixed_delivery_creates_assets_for_hardware_and_an_entitlement_for_software()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id },
                Line(laptop.Id, 2)), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(2, await db.Assets.CountAsync());
            Assert.All(await db.Assets.ToListAsync(), a => Assert.Equal(DeviceTypes.Laptop, a.DeviceType));
            Assert.Single(await db.LicenceEntitlements.ToListAsync());
            Assert.Equal(PurchaseOrderStatus.Received, (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task A_software_line_with_no_licence_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m, Line(software.Id, 50)), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_software_line_naming_both_an_existing_and_a_new_licence_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, "Existing");

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id, NewLicence = NewLicence() }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_licence_given_for_a_hardware_line_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, _, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(laptop.Id, 2) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_renewal_without_a_new_expiry_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id, LicenceMode = LicenceReceiptModes.Renew }),
                "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task An_expiry_on_or_before_the_receipt_date_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    SoftwareLicenceId = licence.Id,
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = ReceiptDate
                }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
            Assert.Null((await db.SoftwareLicences.SingleAsync()).ExpiresAt);
        }
    }

    [Fact]
    public async Task A_renewal_cannot_create_a_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    NewLicence = NewLicence(),
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = new DateTime(2027, 9, 30)
                }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_deactivated_licence_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, isActive: false);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task Another_tenants_licence_is_refused_and_writes_nothing()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var (order, software, _) = await SeedOrderAsync(db, tenantA);
            var theirs = await LicenceTestKit.SeedLicenceAsync(db, tenantB);

            // Super-admin context: the global filter admits tenant B's licence, so only the explicit
            // tenant predicate refuses it.
            var result = await ServiceFor(db).ReceiveAsync(tenantA, order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = theirs.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_refused_delivery_leaves_no_new_licence_behind()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);

            // The software line passes every check and its quantity is claimed; the laptop line then
            // over-receives (3 of 2), so the whole receipt rolls back. A licence created while
            // resolving the software line would survive that unless it is inside the transaction.
            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { NewLicence = NewLicence() },
                Line(laptop.Id, 3)), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_new_licence_cannot_reuse_an_existing_name()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() }), "user-1");

            Assert.False(result.Success);
            Assert.Equal("A licence named 'Microsoft 365 Business Standard' already exists.", result.Message);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    private static PurchaseOrdersController ControllerFor(AppDbContext db, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "buyer-1") };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        return new PurchaseOrdersController(
            db, new FakeTenantProvider(tenantId), new PurchaseOrderNumberAllocator(db), new LookupService(db),
            ServiceFor(db), NullLogger<PurchaseOrdersController>.Instance, new PdfReportService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    [Fact]
    public async Task Creating_a_licence_while_receiving_needs_permission_to_manage_licences()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var dto = Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() });

            var buyerOnly = await ControllerFor(db, tenantId, Permissions.ProcurementManage).Receive(order.Id, dto);

            var refusal = Assert.IsType<ObjectResult>(buyerOnly.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, refusal.StatusCode);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);

            var licenceManager = await ControllerFor(db, tenantId, Permissions.ProcurementManage, Permissions.LicencesManage)
                .Receive(order.Id, dto);

            Assert.IsType<OkObjectResult>(licenceManager.Result);
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task The_delivery_history_names_where_the_seats_went()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ControllerFor(db, tenantId, Permissions.ProcurementManage).Receive(order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }, Line(laptop.Id, 2)));

            var detail = Assert.IsType<ApiResponse<PurchaseOrderDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
            var lines = detail.Receipts.Single().Lines;

            var softwareLine = lines.Single(l => l.DeviceType == DeviceTypes.Software);
            Assert.Equal(licence.Id, softwareLine.SoftwareLicenceId);
            Assert.Equal("Microsoft 365 Business Standard", softwareLine.LicenceName);
            Assert.Null(softwareLine.RenewedTo);

            var laptopLine = lines.Single(l => l.DeviceType == DeviceTypes.Laptop);
            Assert.Null(laptopLine.LicenceName);
        }
    }
}
