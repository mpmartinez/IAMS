using System.Security.Claims;
using System.Text.Json;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

internal static class LicenceTestKit
{
    public const string ActingUserId = "admin-1";

    public static LicencesController Controller(
        AppDbContext db, ITenantProvider tenants, string userId = ActingUserId) =>
        new(db, tenants, new LicenceUsageReader(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
                }
            }
        };

    public static async Task<SoftwareLicence> SeedLicenceAsync(
        AppDbContext db, Guid tenantId,
        string name = "Microsoft 365 Business Standard",
        string model = LicenceModels.PerUser,
        string? key = null,
        DateTime? expiresAt = null,
        bool isActive = true)
    {
        var licence = new SoftwareLicence
        {
            TenantId = tenantId, Name = name, LicenceModel = model,
            LicenceKey = key, ExpiresAt = expiresAt, IsActive = isActive
        };
        db.SoftwareLicences.Add(licence);
        await db.SaveChangesAsync();
        return licence;
    }

    public static async Task SeedEntitlementAsync(
        AppDbContext db, Guid tenantId, int licenceId, int seats,
        decimal cost = 0m, string currency = Currencies.PHP, decimal rate = 1m)
    {
        db.LicenceEntitlements.Add(new LicenceEntitlement
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, SeatsAdded = seats,
            Cost = cost, Currency = currency, ExchangeRate = rate, CreatedByUserId = ActingUserId
        });
        await db.SaveChangesAsync();
    }

    public static async Task<LicenceSeatAssignment> SeedUserSeatAsync(
        AppDbContext db, Guid tenantId, int licenceId, string userId)
    {
        var seat = new LicenceSeatAssignment
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, UserId = userId, AssignedByUserId = ActingUserId
        };
        db.LicenceSeatAssignments.Add(seat);
        await db.SaveChangesAsync();
        return seat;
    }

    public static async Task<LicenceSeatAssignment> SeedDeviceSeatAsync(
        AppDbContext db, Guid tenantId, int licenceId, int assetId)
    {
        var seat = new LicenceSeatAssignment
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, AssetId = assetId, AssignedByUserId = ActingUserId
        };
        db.LicenceSeatAssignments.Add(seat);
        await db.SaveChangesAsync();
        return seat;
    }

    /// <summary>The ApiResponse payload of a successful action.</summary>
    public static T Data<T>(ActionResult<ApiResponse<T>> result) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Data!;

    /// <summary>The refusal message of a failed action.</summary>
    public static string? Message<T>(ActionResult<ApiResponse<T>> result) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Message;

    /// <summary>
    /// Everything the action would put on the wire. Asserting a secret is absent from this, rather
    /// than from one named property, is what catches a key that leaks through a field nobody
    /// thought to check.
    /// </summary>
    public static string Wire<T>(ActionResult<ApiResponse<T>> result) =>
        JsonSerializer.Serialize(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
}
