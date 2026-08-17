using IAMS.Api.Controllers;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

/// <summary>
/// Covers the two things the lookups feature can get badly wrong: an admin-added value
/// silently failing validation because a controller still checks the old hardcoded constant,
/// and a locked vocabulary (one code branches on) being editable through the API despite the
/// UI hiding it.
/// </summary>
public class LookupValueTests
{
    // Deliberately not a device type in the DeviceTypes constant - if AssetsController still
    // validated against DeviceTypes.All this would be rejected.
    private const string NewDeviceType = "Drone";

    // TestDb.Create() uses EnsureCreated(), and EF Core applies HasData seed rows as part of
    // creating the schema (not just via migrations) - so every test database already carries
    // the same 53 rows the AddLookupValues migration inserts in production. Tests that want an
    // unseeded type have to clear it out explicitly, below.

    [Fact]
    public async Task LookupService_falls_back_to_the_constant_list_when_a_type_has_no_rows()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            // Simulate a deployment where the seeding migration has not run yet for this type.
            db.LookupValues.RemoveRange(db.LookupValues.Where(l => l.LookupType == LookupTypes.DeviceType));
            await db.SaveChangesAsync();

            var lookups = new LookupService(db);

            // Every constant value must still validate, exactly as it did before lookups
            // existed, while a value that was never a DeviceTypes constant still is not one.
            Assert.True(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, DeviceTypes.Laptop));
            Assert.False(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, NewDeviceType));
        }
    }

    [Fact]
    public async Task LookupService_recognizes_a_newly_added_value_alongside_the_seeded_ones()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            // Not one of the DeviceTypes constants - proves the check reads the table rather
            // than only ever falling back to the compile-time list.
            db.LookupValues.Add(new LookupValue
            {
                LookupType = LookupTypes.DeviceType,
                Value = NewDeviceType,
                Label = NewDeviceType,
                SortOrder = 100,
                IsActive = true
            });
            await db.SaveChangesAsync();

            var lookups = new LookupService(db);

            Assert.True(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, NewDeviceType));
            Assert.False(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, "NotARealDeviceType"));
        }
    }

    [Fact]
    public async Task LookupService_rejects_a_value_that_was_deactivated()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var laptop = await db.LookupValues
                .SingleAsync(l => l.LookupType == LookupTypes.DeviceType && l.Value == DeviceTypes.Laptop);
            laptop.IsActive = false;
            await db.SaveChangesAsync();

            var lookups = new LookupService(db);

            Assert.False(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, DeviceTypes.Laptop));
            // A different, still-active device type is unaffected.
            Assert.True(await lookups.IsActiveValueAsync(LookupTypes.DeviceType, DeviceTypes.Desktop));
        }
    }

    [Fact]
    public async Task AssetsController_accepts_a_newly_added_device_type_on_create()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            db.LookupValues.Add(new LookupValue
            {
                LookupType = LookupTypes.DeviceType,
                Value = NewDeviceType,
                Label = NewDeviceType,
                SortOrder = 0,
                IsActive = true
            });
            await db.SaveChangesAsync();

            var controller = new AssetsController(db, null!, null!, new LookupService(db));

            var result = await controller.CreateAsset(new CreateAssetDto
            {
                DeviceType = NewDeviceType,
                Status = AssetStatus.Available,
                Currency = "USD"
            });

            var created = Assert.IsType<CreatedAtActionResult>(result.Result);
            var payload = Assert.IsType<ApiResponse<AssetDto>>(created.Value);
            Assert.True(payload.Success);
            Assert.Equal(NewDeviceType, payload.Data!.DeviceType);
        }
    }

    [Fact]
    public async Task AssetsController_rejects_a_device_type_that_is_not_active_lookup_data()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            // DeviceType rows already exist from the model's seed data, so an unrecognised
            // type must be rejected even though it "looks" like a device type string.
            var controller = new AssetsController(db, null!, null!, new LookupService(db));

            var result = await controller.CreateAsset(new CreateAssetDto
            {
                DeviceType = NewDeviceType,
                Status = AssetStatus.Available,
                Currency = "USD"
            });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var payload = Assert.IsType<ApiResponse<AssetDto>>(badRequest.Value);
            Assert.False(payload.Success);
        }
    }

    [Theory]
    [InlineData(LookupTypes.AssetStatus)]
    [InlineData(LookupTypes.TicketStatus)]
    [InlineData(LookupTypes.TicketType)]
    [InlineData(LookupTypes.TicketPriority)]
    public async Task LookupsController_rejects_creating_a_value_for_a_locked_type(string lockedType)
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var controller = new LookupsController(db);

            var result = await controller.Create(new CreateLookupValueDto
            {
                LookupType = lockedType,
                Value = "Whatever",
                Label = "Whatever"
            }, default);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var payload = Assert.IsType<ApiResponse<LookupValueDto>>(badRequest.Value);
            Assert.False(payload.Success);

            // Nothing was persisted - the rejection happens before any write, not just in the
            // response shape.
            Assert.False(await db.LookupValues.AnyAsync(l => l.LookupType == lockedType && l.Value == "Whatever"));
        }
    }

    [Fact]
    public async Task LookupsController_rejects_updating_a_locked_types_existing_value()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            // Already seeded by the model's HasData - no need to add it.
            var existing = await db.LookupValues
                .SingleAsync(l => l.LookupType == LookupTypes.AssetStatus && l.Value == AssetStatus.Available);

            var controller = new LookupsController(db);

            var result = await controller.Update(existing.Id, new UpdateLookupValueDto
            {
                Label = "Renamed"
            }, default);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var payload = Assert.IsType<ApiResponse<LookupValueDto>>(badRequest.Value);
            Assert.False(payload.Success);

            await db.Entry(existing).ReloadAsync();
            Assert.Equal("Available", existing.Label);
        }
    }

    [Fact]
    public async Task LookupsController_rejects_reordering_a_locked_type()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var controller = new LookupsController(db);

            var result = await controller.Reorder(LookupTypes.TicketPriority, [4, 3, 2, 1], default);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
            var payload = Assert.IsType<ApiResponse<object>>(badRequest.Value);
            Assert.False(payload.Success);
        }
    }

    [Fact]
    public async Task LookupsController_allows_creating_and_reordering_an_editable_type()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var controller = new LookupsController(db);

            var created = await controller.Create(new CreateLookupValueDto
            {
                LookupType = LookupTypes.DeviceType,
                Value = NewDeviceType,
                Label = NewDeviceType
            }, default);

            var createdResult = Assert.IsType<CreatedAtActionResult>(created.Result);
            var createdPayload = Assert.IsType<ApiResponse<LookupValueDto>>(createdResult.Value);
            Assert.True(createdPayload.Success);

            var updateResult = await controller.Update(createdPayload.Data!.Id, new UpdateLookupValueDto
            {
                IsActive = false
            }, default);

            var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
            var updatePayload = Assert.IsType<ApiResponse<LookupValueDto>>(updateOk.Value);
            Assert.True(updatePayload.Success);
            Assert.False(updatePayload.Data!.IsActive);
        }
    }

    [Fact]
    public void LookupsController_delete_is_unavailable()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var controller = new LookupsController(db);

            var result = controller.Delete(1);

            var response = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.StatusCode);
        }
    }
}
