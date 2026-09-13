using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceSeatApiTests
{
    [Fact]
    public async Task A_person_is_given_a_seat_on_a_per_user_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);

            var detail = Payload(await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1", Notes = "New starter" }, default));

            var seat = Assert.Single(detail.Seats);
            Assert.Equal("Maria Santos", seat.UserName);
            Assert.True(seat.IsActive);
            Assert.Equal(ActingUserId, (await db.LicenceSeatAssignments.SingleAsync()).AssignedByUserId);
        }
    }

    [Fact]
    public async Task A_device_cannot_take_a_seat_on_a_per_user_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_person_cannot_take_a_seat_on_a_per_device_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_seat_must_name_exactly_one_holder()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = await SeedLicenceAsync(db, tenantId);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var neither = await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto(), default);
            var both = await controller.AssignSeat(
                licence.Id, new AssignLicenceSeatDto { UserId = "user-1", AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(neither.Result);
            Assert.IsType<BadRequestObjectResult>(both.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_second_active_seat_for_the_same_person_is_refused_until_the_first_is_released()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);
            var controller = Controller(db, new FakeTenantProvider(tenantId));
            var assign = new AssignLicenceSeatDto { UserId = "user-1" };

            var first = Payload(await controller.AssignSeat(licence.Id, assign, default));
            var second = await controller.AssignSeat(licence.Id, assign, default);
            Assert.IsType<BadRequestObjectResult>(second.Result);
            Assert.Equal(1, await db.LicenceSeatAssignments.CountAsync());

            await controller.ReleaseSeat(licence.Id, first.Seats.Single().Id, default);
            var again = await controller.AssignSeat(licence.Id, assign, default);

            Assert.IsType<OkObjectResult>(again.Result);
            Assert.Equal(2, await db.LicenceSeatAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Assigning_beyond_what_is_owned_is_allowed_and_counted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 1);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);
            var detail = Payload(await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-2" }, default));

            Assert.Equal(2, detail.Licence.SeatsAssigned);
            Assert.Equal(1, detail.Licence.OverAssigned);
        }
    }

    [Fact]
    public async Task A_deactivated_person_cannot_be_given_a_seat()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var user = await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            user.IsActive = false;
            await db.SaveChangesAsync();
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_retired_device_cannot_be_given_a_seat()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001", AssetStatus.Retired);
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task Another_tenants_person_cannot_be_given_a_seat()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", "Someone Else");
            var ours = await SeedLicenceAsync(db, tenantA);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AssignSeat(ours.Id, new AssignLicenceSeatDto { UserId = "their-user" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("User not found.", Message(result));
            Assert.Empty(db.LicenceSeatAssignments.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Another_tenants_device_cannot_be_given_a_seat()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirAsset = await TestDb.SeedAssetAsync(db, tenantB, "LAP-9999");
            var ours = await SeedLicenceAsync(db, tenantA, "Windows 11 Pro", LicenceModels.PerDevice);

            // Super admin, current tenant A: the Assets filter admits tenant B's laptop, so only the
            // explicit predicate refuses it.
            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AssignSeat(ours.Id, new AssignLicenceSeatDto { AssetId = theirAsset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Asset not found.", Message(result));
            Assert.Empty(db.LicenceSeatAssignments.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Releasing_a_seat_records_who_and_when_and_cannot_happen_twice()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);
            var seat = await SeedUserSeatAsync(db, tenantId, licence.Id, "user-1");
            var controller = Controller(db, new FakeTenantProvider(tenantId), userId: "staff-7");

            var released = await controller.ReleaseSeat(licence.Id, seat.Id, default);
            var twice = await controller.ReleaseSeat(licence.Id, seat.Id, default);

            Assert.IsType<OkObjectResult>(released.Result);
            Assert.IsType<BadRequestObjectResult>(twice.Result);
            var saved = await db.LicenceSeatAssignments.AsNoTracking().SingleAsync();
            Assert.NotNull(saved.ReleasedAt);
            Assert.Equal("staff-7", saved.ReleasedByUserId);
        }
    }

    [Fact]
    public async Task Another_tenants_seat_cannot_be_released()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", "Someone Else");
            var theirs = await SeedLicenceAsync(db, tenantB);
            var seat = await SeedUserSeatAsync(db, tenantB, theirs.Id, "their-user");

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .ReleaseSeat(theirs.Id, seat.Id, default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Null((await db.LicenceSeatAssignments.IgnoreQueryFilters().AsNoTracking().SingleAsync()).ReleasedAt);
        }
    }

    [Fact]
    public async Task Seats_whose_holders_have_gone_are_flagged_reclaimable()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var retired = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var working = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002", AssetStatus.InUse);
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            await SeedDeviceSeatAsync(db, tenantId, licence.Id, retired.Id);
            await SeedDeviceSeatAsync(db, tenantId, licence.Id, working.Id);
            retired.Status = AssetStatus.Retired;
            await db.SaveChangesAsync();

            var detail = Payload(await Controller(db, new FakeTenantProvider(tenantId)).GetById(licence.Id, default));

            Assert.True(detail.Seats.Single(s => s.AssetTag == "LAP-0001").IsReclaimable);
            Assert.False(detail.Seats.Single(s => s.AssetTag == "LAP-0002").IsReclaimable);
            Assert.Equal(1, detail.Licence.Reclaimable);
        }
    }

    [Fact]
    public async Task The_device_view_lists_only_active_seats_on_that_device()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var laptop = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var other = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002");
            var windows = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            var office = await SeedLicenceAsync(db, tenantId, "Office LTSC 2024", LicenceModels.PerDevice);
            var autocad = await SeedLicenceAsync(db, tenantId, "AutoCAD", LicenceModels.PerDevice);
            await SeedDeviceSeatAsync(db, tenantId, windows.Id, laptop.Id);
            var released = await SeedDeviceSeatAsync(db, tenantId, office.Id, laptop.Id);
            released.ReleasedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SeedDeviceSeatAsync(db, tenantId, autocad.Id, other.Id);

            var rows = Payload(await Controller(db, new FakeTenantProvider(tenantId)).GetDeviceLicences(laptop.Id, default));

            Assert.Equal("Windows 11 Pro", Assert.Single(rows).Name);
        }
    }
}
