using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceEntitlementApiTests
{
    private static readonly DateTime EntryDate = new(2026, 9, 13);

    private static AddLicenceEntitlementDto Entry(int seats, DateTime? expiresAt = null, string? notes = null) => new()
    {
        SeatsAdded = seats,
        Cost = 35000m,
        Currency = Currencies.PHP,
        ExchangeRate = 1m,
        EntitlementDate = EntryDate,
        ExpiresAt = expiresAt,
        Notes = notes
    };

    [Fact]
    public async Task Adding_seats_records_the_purchase_at_its_own_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).AddEntitlement(
                licence.Id,
                Entry(50) with { Cost = 1200m, Currency = Currencies.USD, ExchangeRate = 58.20m },
                default);

            var detail = Payload(result);
            Assert.Equal(50, detail.Licence.SeatsOwned);
            Assert.Equal(69840m, detail.SpendInPesos);

            var saved = await db.LicenceEntitlements.SingleAsync();
            Assert.Equal(Currencies.USD, saved.Currency);
            Assert.Equal(58.20m, saved.ExchangeRate);
            Assert.Equal(ActingUserId, saved.CreatedByUserId);
            Assert.Null(saved.GoodsReceiptLineId);
        }
    }

    [Fact]
    public async Task A_renewal_moves_the_expiry_without_adding_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, expiresAt: new DateTime(2026, 10, 1));
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var renewedTo = new DateTime(2027, 10, 1);
            var detail = Payload(await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0, expiresAt: renewedTo), default));

            Assert.Equal(50, detail.Licence.SeatsOwned);
            Assert.Equal(renewedTo, detail.Licence.ExpiresAt);
            Assert.Equal(renewedTo, (await db.LicenceEntitlements.OrderBy(e => e.Id).LastAsync()).ExpiresAtAfter);
        }
    }

    [Fact]
    public async Task An_entry_that_changes_nothing_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task Removing_seats_needs_a_reason()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-10), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(1, await db.LicenceEntitlements.CountAsync());
        }
    }

    [Fact]
    public async Task A_reduction_within_what_is_owned_is_recorded()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var detail = Payload(await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-10, notes: "Downsized at renewal") with { Cost = 0m }, default));

            Assert.Equal(40, detail.Licence.SeatsOwned);
        }
    }

    [Fact]
    public async Task Seats_owned_cannot_go_below_zero()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-6, notes: "Cancelled"), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Only 5 seats are owned, so 6 cannot be removed.", Message(result));
            Assert.Equal(1, await db.LicenceEntitlements.CountAsync());
        }
    }

    [Fact]
    public async Task An_expiry_on_or_before_the_entry_date_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0, expiresAt: EntryDate), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
            Assert.Null((await db.SoftwareLicences.AsNoTracking().SingleAsync()).ExpiresAt);
        }
    }

    [Fact]
    public async Task A_foreign_currency_entry_at_parity_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).AddEntitlement(
                licence.Id, Entry(10) with { Currency = Currencies.USD, ExchangeRate = 1m }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task A_deactivated_licence_takes_no_entries()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, isActive: false);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(10), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task Another_tenants_licence_takes_no_entries()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AddEntitlement(theirs.Id, Entry(10), default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements.IgnoreQueryFilters());
        }
    }
}
