using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The same bug MixedCurrencyReportTests covers for the reports, on the four money totals that
/// hang off assignments and the asset summary: a user holding one 50,000-peso desktop and one
/// USD 1,200 laptop booked at 58.20 is holding 119,840 pesos of kit, not 51,200 of nothing in
/// particular. Rows keep the currency they were booked in; every total is pesos.
/// </summary>
public class MixedCurrencyAssignmentTotalsTests
{
    private const decimal PesoDesktop = 50000m;
    private const decimal UsdLaptop = 1200m;
    private const decimal Rate = 58.20m;

    // 50,000 + (1,200 * 58.20). Naive summing gives 51,200.
    private const decimal ExpectedPesoTotal = 119840m;

    private static ClaimsPrincipal BuildPrincipal(string userId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(Permissions.ClaimType, Permissions.AssignmentsView)
            ],
            "TestAuth"));

    private static AssignmentsController BuildController(AppDbContext db, string callerId) =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildPrincipal(callerId) }
            }
        };

    /// <summary>
    /// Gives <paramref name="userId"/> one peso asset and one USD asset, both currently held,
    /// each with a live (unreturned) assignment row so the offboarding queries see them too.
    /// </summary>
    private static async Task SeedMixedHoldingAsync(
        AppDbContext db, Guid tenantId, string userId, string tagPrefix)
    {
        var peso = new Asset
        {
            TenantId = tenantId,
            AssetTag = $"{tagPrefix}-PHP",
            DeviceType = DeviceTypes.Desktop,
            Status = AssetStatus.InUse,
            AssignedToUserId = userId,
            PurchasePrice = PesoDesktop,
            Currency = Currencies.PHP,
            ExchangeRate = 1m
        };
        var usd = new Asset
        {
            TenantId = tenantId,
            AssetTag = $"{tagPrefix}-USD",
            DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.InUse,
            AssignedToUserId = userId,
            PurchasePrice = UsdLaptop,
            Currency = Currencies.USD,
            ExchangeRate = Rate
        };
        db.Assets.AddRange(peso, usd);
        await db.SaveChangesAsync();

        db.AssetAssignments.AddRange(
            new AssetAssignment
            {
                TenantId = tenantId,
                AssetId = peso.Id,
                UserId = userId,
                AssignedByUserId = userId
            },
            new AssetAssignment
            {
                TenantId = tenantId,
                AssetId = usd.Id,
                UserId = userId,
                AssignedByUserId = userId
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetUserAssets_totals_the_holding_in_pesos()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Mixed Holder");
            await SeedMixedHoldingAsync(db, tenantId, "emp-1", "UA");

            var result = await BuildController(db, "emp-1").GetUserAssets("emp-1");

            var body = Assert.IsType<UserAssetsDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(ExpectedPesoTotal, body.TotalAssetValue);

            // Each row still reports what it was booked in, so the client can render the
            // invoice figure rather than a converted one.
            var usdRow = Assert.Single(body.CurrentAssets, a => a.AssetTag == "UA-USD");
            Assert.Equal(UsdLaptop, usdRow.PurchasePrice);
            Assert.Equal(Currencies.USD, usdRow.Currency);
        }
    }

    [Fact]
    public async Task GetOffboardingSummary_totals_the_unreturned_kit_in_pesos()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Leaver");
            await SeedMixedHoldingAsync(db, tenantId, "emp-1", "OB");

            var result = await BuildController(db, "emp-1").GetOffboardingSummary("emp-1");

            var body = Assert.IsType<OffboardingDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(2, body.TotalUnreturnedAssets);
            Assert.Equal(ExpectedPesoTotal, body.TotalUnreturnedValue);

            var usdRow = Assert.Single(body.UnreturnedAssets, a => a.AssetTag == "OB-USD");
            Assert.Equal(UsdLaptop, usdRow.PurchasePrice);
            Assert.Equal(Currencies.USD, usdRow.Currency);
        }
    }

    [Fact]
    public async Task GetPendingOffboardings_totals_each_leaver_in_pesos()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var leaver = await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Deactivated Leaver");
            leaver.IsActive = false;
            await db.SaveChangesAsync();
            await SeedMixedHoldingAsync(db, tenantId, "emp-1", "PO");

            var result = await BuildController(db, "emp-1").GetPendingOffboardings();

            var rows = Assert.IsType<List<OffboardingSummaryItem>>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            var row = Assert.Single(rows);
            Assert.Equal("emp-1", row.UserId);
            Assert.Equal(2, row.UnreturnedCount);
            Assert.Equal(ExpectedPesoTotal, row.TotalValue);
        }
    }

    [Fact]
    public async Task GetAssetSummary_totals_the_register_in_pesos()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Mixed Holder");
            await SeedMixedHoldingAsync(db, tenantId, "emp-1", "SM");

            var controller = new AssetsController(db, null!, null!, null!, null!);
            var result = await controller.GetAssetSummary();

            // The endpoint answers with an anonymous type, so read TotalValue off it reflectively
            // rather than reshaping the response just to make it assertable.
            var summary = Assert.IsType<ApiResponse<object>>(
                Assert.IsType<OkObjectResult>(result).Value).Data;
            Assert.NotNull(summary);

            var totalValue = summary.GetType().GetProperty("TotalValue")!.GetValue(summary);
            Assert.Equal(ExpectedPesoTotal, Assert.IsType<decimal>(totalValue));
        }
    }
}
