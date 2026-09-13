using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>An order carrying the same product on two lines, as a head-office and a branch quote might.</summary>
    private static async Task<(PurchaseOrder Order, PurchaseOrderLine First, PurchaseOrderLine Second)> SeedTwoSoftwareLineOrderAsync(
        AppDbContext db, Guid tenantId)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Software" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id, Currency = Currencies.PHP,
            Status = PurchaseOrderStatus.Ordered, OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        var first = new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Software, Description = "Microsoft 365 - head office", Quantity = 40, UnitPrice = 700m
        };
        var second = new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Software, Description = "Microsoft 365 - branch", Quantity = 10, UnitPrice = 700m
        };
        order.Lines.Add(first);
        order.Lines.Add(second);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order, first, second);
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
    public async Task A_name_taken_only_in_another_tenant_does_not_block_a_new_licence()
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

            // Super-admin context: the global filter admits tenant B's licence to the name check, so
            // only the explicit tenant predicate keeps B's name from refusing A's new licence.
            var result = await ServiceFor(db).ReceiveAsync(tenantA, order.Id,
                Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence(theirs.Name) }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            var ours = await db.SoftwareLicences.IgnoreQueryFilters().SingleAsync(l => l.TenantId == tenantA);
            Assert.Equal("Microsoft 365 Business Standard", ours.Name);
            Assert.NotEqual(theirs.Id, ours.Id);
            Assert.Equal(ours.Id, (await db.LicenceEntitlements.IgnoreQueryFilters().SingleAsync()).SoftwareLicenceId);
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

    [Fact]
    public async Task Adding_seats_does_not_move_an_existing_expiry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var term = new DateTime(2027, 9, 30);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: term);

            // An add-on quote's end date, whether it would shorten the term or lengthen it.
            foreach (var quoted in new[] { new DateTime(2027, 3, 31), new DateTime(2028, 9, 30) })
            {
                var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                    Line(software.Id, 10) with { SoftwareLicenceId = licence.Id, LicenceExpiresAt = quoted }), "user-1");

                Assert.False(result.Success);
                Assert.Equal(
                    "Microsoft 365 Business Standard: 'Microsoft 365 Business Standard' already runs until Sep 30, 2027. " +
                    "Added seats take that term - use Renew to change it.",
                    result.Message);
                await AssertNothingWrittenAsync(db, licencesBefore: 1);
                Assert.Equal(term, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            }
        }
    }

    [Fact]
    public async Task Adding_seats_can_date_a_licence_that_has_no_expiry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);
            var term = new DateTime(2027, 9, 30);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id, LicenceExpiresAt = term }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(term, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            var entry = await db.LicenceEntitlements.SingleAsync();
            Assert.Equal(50, entry.SeatsAdded);
            Assert.Equal(term, entry.ExpiresAtAfter);
        }
    }

    [Fact]
    public async Task A_renewal_must_run_past_the_current_expiry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var term = new DateTime(2027, 9, 30);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: term);

            // Earlier than the running term, and the same day as it.
            foreach (var renewedTo in new[] { new DateTime(2027, 3, 31), term })
            {
                var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                    Line(software.Id, 50) with
                    {
                        SoftwareLicenceId = licence.Id,
                        LicenceMode = LicenceReceiptModes.Renew,
                        LicenceExpiresAt = renewedTo
                    }), "user-1");

                Assert.False(result.Success);
                Assert.Equal(
                    "Microsoft 365 Business Standard: a renewal must run past the licence's current expiry of Sep 30, 2027.",
                    result.Message);
                await AssertNothingWrittenAsync(db, licencesBefore: 1);
                Assert.Equal(term, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            }
        }
    }

    [Fact]
    public async Task A_hardware_line_carrying_licence_terms_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, _, laptop) = await SeedOrderAsync(db, tenantId);

            var renewing = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(laptop.Id, 2) with { LicenceMode = LicenceReceiptModes.Renew }), "user-1");

            Assert.False(renewing.Success);
            Assert.Equal("Laptop is not software, so it cannot be received into a licence.", renewing.Message);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);

            var dated = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(laptop.Id, 2) with { LicenceExpiresAt = new DateTime(2027, 9, 30) }), "user-1");

            Assert.False(dated.Success);
            Assert.Equal("Laptop is not software, so it cannot be received into a licence.", dated.Message);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    /// <summary>
    /// Stands in for a renewal on another purchase order committing between this receipt's checks
    /// and its write. Just before the first UPDATE against SoftwareLicences runs, it moves the
    /// licence's expiry past the renewal being received, on the same connection and inside the
    /// same transaction - the only place SQLite will let a second writer in while the first holds
    /// its write lock.
    /// </summary>
    private sealed class RenewalOvertaker(DateTime raisedTo) : DbCommandInterceptor
    {
        /// <summary>Zero until the test arms it, so seeding the licence cannot trip it.</summary>
        public int LicenceId { get; set; }

        public bool Raised { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await OvertakeAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await OvertakeAsync(command, cancellationToken);
            return result;
        }

        private async Task OvertakeAsync(DbCommand command, CancellationToken ct)
        {
            if (Raised || LicenceId == 0 || !command.CommandText.Contains("UPDATE \"SoftwareLicences\"", StringComparison.Ordinal))
                return;

            Raised = true;

            // Created on the connection directly rather than through the context, so it does not
            // come back through this interceptor.
            await using var overtake = command.Connection!.CreateCommand();
            overtake.Transaction = command.Transaction;
            overtake.CommandText = "UPDATE \"SoftwareLicences\" SET \"ExpiresAt\" = $raisedTo WHERE \"Id\" = $id";

            var raised = overtake.CreateParameter();
            raised.ParameterName = "$raisedTo";
            raised.Value = raisedTo;
            overtake.Parameters.Add(raised);

            var id = overtake.CreateParameter();
            id.ParameterName = "$id";
            id.Value = LicenceId;
            overtake.Parameters.Add(id);

            Assert.Equal(1, await overtake.ExecuteNonQueryAsync(ct));
        }
    }

    /// <summary>
    /// Every check before the order lock passes here - the licence reads Sep 30, 2026 and the
    /// renewal runs to Sep 30, 2027 - so the refusal can only come from the conditional update.
    /// The overtaking write is inside the refused receipt's transaction, so it rolls back with it
    /// and the licence ends at its original expiry; what proves the point is that the renewal was
    /// refused, with the message only the update's zero-row branch produces.
    /// </summary>
    [Fact]
    public async Task A_renewal_overtaken_by_a_later_one_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var original = new DateTime(2026, 9, 30);
        var overtaker = new RenewalOvertaker(raisedTo: new DateTime(2028, 9, 30));
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId), builder => builder.AddInterceptors(overtaker));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: original);

            // Armed only now: seeding the licence went through the same interceptor.
            overtaker.LicenceId = licence.Id;

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    SoftwareLicenceId = licence.Id,
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = new DateTime(2027, 9, 30)
                }), "user-1");

            Assert.True(overtaker.Raised, "The renewal never reached its conditional update.");
            Assert.False(result.Success);
            Assert.Equal(
                "Microsoft 365 Business Standard: 'Microsoft 365 Business Standard' was re-dated by another delivery - check its expiry and try again.",
                result.Message);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
            Assert.Equal(original, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
        }
    }

    /// <summary>
    /// Both renewals pass every check on their own. Without a check across lines, which one the
    /// delivery honoured turned on line order: a later date second quietly won, leaving two
    /// entitlements that disagree about the term, and an earlier date second matched nothing in the
    /// conditional update - refusing the delivery for a race with "another delivery" that does not
    /// exist. Both orders are asserted, so the refusal cannot depend on it.
    /// </summary>
    [Fact]
    public async Task A_licence_re_dated_on_two_lines_of_one_delivery_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, first, second) = await SeedTwoSoftwareLineOrderAsync(db, tenantId);
            var term = new DateTime(2026, 9, 30);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: term);
            var earlier = new DateTime(2027, 9, 30);
            var later = new DateTime(2027, 12, 31);

            foreach (var (firstTo, secondTo) in new[] { (earlier, later), (later, earlier) })
            {
                var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                    Line(first.Id, 40) with
                    {
                        SoftwareLicenceId = licence.Id,
                        LicenceMode = LicenceReceiptModes.Renew,
                        LicenceExpiresAt = firstTo
                    },
                    Line(second.Id, 10) with
                    {
                        SoftwareLicenceId = licence.Id,
                        LicenceMode = LicenceReceiptModes.Renew,
                        LicenceExpiresAt = secondTo
                    }), "user-1");

                Assert.False(result.Success);
                Assert.Equal(
                    "Microsoft 365 - branch: 'Microsoft 365 Business Standard' is re-dated on more than one line of this delivery. " +
                    "Put its new expiry on one line.",
                    result.Message);
                await AssertNothingWrittenAsync(db, licencesBefore: 1);
                Assert.Equal(term, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            }
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
