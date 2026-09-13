using System.Reflection;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceApiTests
{
    private const string Key = "XXXXX-YYYYY-ZZZZZ-X7Q2";

    private static UpsertSoftwareLicenceDto Upsert(
        string name = "Microsoft 365 Business Standard", string model = LicenceModels.PerUser) => new()
    {
        Name = name,
        Publisher = "Microsoft",
        LicenceModel = model
    };

    [Fact]
    public async Task A_licence_is_created_against_the_callers_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .Create(Upsert() with { LicenceKey = Key }, default);

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.SoftwareLicences.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal(Key, saved.LicenceKey);
            Assert.Equal("****-X7Q2", LicenceTestKit.Payload(result).Licence.MaskedKey);
        }
    }

    [Fact]
    public async Task A_duplicate_licence_name_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).Create(Upsert(), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("A licence named 'Microsoft 365 Business Standard' already exists.", Message(result));
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task An_unknown_licence_model_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .Create(Upsert(model: "Concurrent"), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.SoftwareLicences);
        }
    }

    [Fact]
    public async Task A_licence_cannot_name_another_tenants_supplier()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = new Supplier { TenantId = tenantB, Name = "Their Reseller" };
            db.Suppliers.Add(theirs);
            await db.SaveChangesAsync();

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .Create(Upsert() with { SupplierId = theirs.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Supplier not found.", Message(result));
            Assert.Empty(db.SoftwareLicences.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task No_read_puts_the_full_key_on_the_wire()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var list = await controller.GetAll(default);
            var detail = await controller.GetById(licence.Id, default);

            Assert.DoesNotContain(Key, Wire(list));
            Assert.DoesNotContain(Key, Wire(detail));
            Assert.Contains("****-X7Q2", Wire(detail));
        }
    }

    [Fact]
    public async Task Saving_without_a_key_keeps_it_a_new_key_replaces_it_and_clearing_removes_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            await controller.Update(licence.Id, Upsert() with { Notes = "Renewed through reseller" }, default);
            Assert.Equal(Key, (await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);

            await controller.Update(licence.Id, Upsert() with { LicenceKey = "NEWKEY-0000-9999" }, default);
            Assert.Equal("NEWKEY-0000-9999", (await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);

            await controller.Update(licence.Id, Upsert() with { ClearKey = true }, default);
            Assert.Null((await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);
        }
    }

    [Fact]
    public async Task The_licence_model_is_fixed_once_a_seat_exists()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var unused = await SeedLicenceAsync(db, tenantId, "Zoom Workplace");
            var inUse = await SeedLicenceAsync(db, tenantId);
            var seat = await SeedUserSeatAsync(db, tenantId, inUse.Id, "user-1");

            // A released seat still counts: it points at a person, and a per-device licence
            // would leave that history pointing at the wrong kind of holder.
            seat.ReleasedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var changedFreely = await controller.Update(
                unused.Id, Upsert("Zoom Workplace", LicenceModels.PerDevice), default);
            Assert.IsType<OkObjectResult>(changedFreely.Result);

            var refused = await controller.Update(
                inUse.Id, Upsert(model: LicenceModels.PerDevice), default);
            Assert.IsType<BadRequestObjectResult>(refused.Result);
            Assert.Equal(LicenceModels.PerUser,
                (await db.SoftwareLicences.AsNoTracking().SingleAsync(l => l.Id == inUse.Id)).LicenceModel);
        }
    }

    [Fact]
    public async Task The_list_reports_seats_owned_assigned_over_and_reclaimable()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var departed = await TestDb.SeedUserAsync(db, tenantId, "user-3", "Ana Cruz");
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 2);
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-1");
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-2");
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-3");
            departed.IsActive = false;
            await db.SaveChangesAsync();

            var row = LicenceTestKit.Payload(await Controller(db, new FakeTenantProvider(tenantId)).GetAll(default)).Single();

            Assert.Equal(2, row.SeatsOwned);
            Assert.Equal(3, row.SeatsAssigned);
            Assert.Equal(1, row.OverAssigned);
            Assert.Equal(1, row.Reclaimable);
            Assert.True(row.ModelLocked);
        }
    }

    [Fact]
    public async Task Revealing_a_key_returns_it_and_records_who_saw_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);

            var result = await Controller(db, new FakeTenantProvider(tenantId), userId: "staff-7")
                .RevealKey(licence.Id, default);

            Assert.Equal(Key, LicenceTestKit.Payload(result).LicenceKey);
            var entry = await db.AuditLogs.SingleAsync();
            Assert.Equal(AuditActions.LicenceKeyRevealed, entry.Action);
            Assert.Equal(nameof(SoftwareLicence), entry.EntityType);
            Assert.Equal(licence.Id.ToString(), entry.EntityId);
            Assert.Equal("staff-7", entry.UserId);
            Assert.Equal(tenantId, entry.TenantId);
            Assert.Null(entry.Changes);
        }
    }

    [Fact]
    public async Task Another_tenants_key_cannot_be_revealed()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB, key: Key);

            // Super admin, current tenant A: the global filter admits tenant B's licence, so only
            // the explicit tenant predicate stands between this caller and the key.
            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .RevealKey(theirs.Id, default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.DoesNotContain(Key, Wire(result));
            Assert.Empty(db.AuditLogs.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Another_tenants_licence_cannot_be_updated_or_deactivated()
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
            var controller = Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true));

            var update = await controller.Update(theirs.Id, Upsert("Renamed"), default);
            var deactivate = await controller.Deactivate(theirs.Id, default);

            Assert.IsType<NotFoundObjectResult>(update.Result);
            Assert.IsType<NotFoundObjectResult>(deactivate.Result);
            var unchanged = await db.SoftwareLicences.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal("Microsoft 365 Business Standard", unchanged.Name);
            Assert.True(unchanged.IsActive);
        }
    }

    [Fact]
    public async Task Deactivating_a_licence_keeps_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            await Controller(db, new FakeTenantProvider(tenantId)).Deactivate(licence.Id, default);

            Assert.False((await db.SoftwareLicences.AsNoTracking().SingleAsync()).IsActive);
        }
    }

    [Fact]
    public async Task The_renewal_count_includes_only_active_licences_that_are_due_or_expired()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.Date;
            await SeedLicenceAsync(db, tenantId, "Expired", expiresAt: today.AddDays(-3));
            await SeedLicenceAsync(db, tenantId, "Due", expiresAt: today.AddDays(30));
            await SeedLicenceAsync(db, tenantId, "Active", expiresAt: today.AddDays(200));
            await SeedLicenceAsync(db, tenantId, "Perpetual");
            await SeedLicenceAsync(db, tenantId, "Retired and expired", expiresAt: today.AddDays(-10), isActive: false);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).GetRenewalCount(default);

            Assert.Equal(2, Assert.IsType<OkObjectResult>(result.Result).Value);
        }
    }

    [Fact]
    public async Task A_caller_with_no_organisation_is_refused()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var result = await Controller(db, new FakeTenantProvider(null, isSuperAdmin: true)).GetAll(default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Select an organisation first.", Message(result));
        }
    }

    [Theory]
    [InlineData(nameof(LicencesController.Create), "CanManageLicences")]
    [InlineData(nameof(LicencesController.Update), "CanManageLicences")]
    [InlineData(nameof(LicencesController.Deactivate), "CanManageLicences")]
    [InlineData(nameof(LicencesController.RevealKey), "CanRevealLicenceKeys")]
    [InlineData(nameof(LicencesController.AddEntitlement), "CanManageLicences")]
    public void Each_write_is_gated_on_its_own_policy(string action, string policy)
    {
        var method = typeof(LicencesController).GetMethod(action)!;
        Assert.Equal(policy, method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal("CanViewLicences", typeof(LicencesController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }
}
