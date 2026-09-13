using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using Microsoft.AspNetCore.Mvc;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Every read of the licence tables, from a super admin whose current organisation is A, against
/// rows that belong to B. The super-admin flag switches the global query filter off, so each of
/// these passes only because of the explicit tenant predicate on the query under test - take that
/// predicate away and the test fails.
/// </summary>
public class LicenceIsolationTests
{
    private const string TheirLicence = "Adobe Creative Cloud";
    private const string TheirUser = "Brenda Villanueva";

    [Fact]
    public async Task The_list_holds_no_other_tenants_licence()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await SeedLicenceAsync(db, tenantB, TheirLicence);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true)).GetAll(default);

            Assert.Empty(LicenceTestKit.Payload(result));
            Assert.DoesNotContain(TheirLicence, Wire(result));
        }
    }

    [Fact]
    public async Task Another_tenants_licence_is_not_found()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", TheirUser);
            var theirs = await SeedLicenceAsync(db, tenantB, TheirLicence);
            await SeedEntitlementAsync(db, tenantB, theirs.Id, seats: 10);
            await SeedUserSeatAsync(db, tenantB, theirs.Id, "their-user");

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetById(theirs.Id, default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.DoesNotContain(TheirLicence, Wire(result));
            Assert.DoesNotContain(TheirUser, Wire(result));
        }
    }

    /// <summary>
    /// The licence-level predicate would 404 another tenant's licence on its own, so it cannot prove
    /// the entitlement and seat queries are scoped. These rows are tenant B's but point at tenant A's
    /// licence - the foreign keys do not check tenant, so a bad import or a hand-written fix could
    /// leave exactly this - and the only thing keeping them off A's page, and out of A's seat counts,
    /// is the tenant predicate on each of those queries.
    /// </summary>
    [Fact]
    public async Task Another_tenants_rows_pointing_at_this_tenants_licence_are_not_read()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", TheirUser);
            var ours = await SeedLicenceAsync(db, tenantA);
            await SeedLicenceAsync(db, tenantB, TheirLicence);
            await SeedEntitlementAsync(db, tenantA, ours.Id, seats: 5);

            await SeedEntitlementAsync(db, tenantB, ours.Id, seats: 7);
            await SeedUserSeatAsync(db, tenantB, ours.Id, "their-user");

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetById(ours.Id, default);

            var detail = LicenceTestKit.Payload(result);
            Assert.Equal(5, Assert.Single(detail.Entitlements).SeatsAdded);
            Assert.Empty(detail.Seats);
            Assert.Equal(5, detail.Licence.SeatsOwned);
            Assert.Equal(0, detail.Licence.SeatsAssigned);
            Assert.False(detail.Licence.ModelLocked);
            Assert.DoesNotContain(TheirUser, Wire(result));
        }
    }

    [Fact]
    public async Task The_renewal_count_ignores_another_tenants_licences()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await SeedLicenceAsync(db, tenantB, TheirLicence, expiresAt: DateTime.UtcNow.Date.AddDays(30));

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetRenewalCount(default);

            Assert.Equal(0, Assert.IsType<OkObjectResult>(result.Result).Value);
        }
    }

    [Fact]
    public async Task Another_tenants_device_shows_no_licences()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirDevice = await TestDb.SeedAssetAsync(db, tenantB, "LAP-B-0001");
            var theirs = await SeedLicenceAsync(db, tenantB, TheirLicence, model: LicenceModels.PerDevice);
            await SeedDeviceSeatAsync(db, tenantB, theirs.Id, theirDevice.Id);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetDeviceLicences(theirDevice.Id, default);

            Assert.Empty(LicenceTestKit.Payload(result));
            Assert.DoesNotContain(TheirLicence, Wire(result));
        }
    }
}
