using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The bug this whole feature would otherwise introduce: a USD 1,200 laptop adding 1,200 to a
/// peso total. Rows carry the currency they were booked in; totals are always pesos.
/// </summary>
public class MixedCurrencyReportTests
{
    private static async Task SeedMixedEstateAsync(AssetDesk.Api.Data.AppDbContext db, Guid tenantId)
    {
        // 50,000 pesos outright.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId,
            AssetTag = "LAP-0001",
            DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available,
            PurchasePrice = 50000m,
            Currency = Currencies.PHP,
            ExchangeRate = 1m
        });

        // USD 1,200 at 58.20 = 69,840 pesos.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId,
            AssetTag = "LAP-0002",
            DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available,
            PurchasePrice = 1200m,
            Currency = Currencies.USD,
            ExchangeRate = 58.20m
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_asset_value_report_totals_in_pesos_not_in_mixed_units()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedMixedEstateAsync(db, tenantId);

            var controller = new ReportsController(db, null!);
            var result = await controller.GetAssetValueReport();

            var summary = Assert.IsType<AssetValueSummaryDto>(
                Assert.IsType<ApiResponse<AssetValueSummaryDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value).Data);

            // 50,000 + (1,200 * 58.20) = 119,840. Naive summing would give 51,200.
            Assert.Equal(119840m, summary.GrandTotalValue);
            Assert.Equal(2, summary.TotalAssetCount);
            Assert.Equal(59920m, summary.AverageAssetValue);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);

            var laptops = Assert.Single(summary.ByDeviceType);
            Assert.Equal(119840m, laptops.TotalValue);
            Assert.Equal(59920m, laptops.AverageValue);

            var available = Assert.Single(summary.ByStatus);
            Assert.Equal(119840m, available.TotalValue);
        }
    }

    [Fact]
    public async Task The_dashboard_total_is_in_pesos_too()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedMixedEstateAsync(db, tenantId);

            var controller = new DashboardController(db);
            var result = await controller.GetDashboard();

            var dashboard = Assert.IsType<DashboardDto>(
                Assert.IsType<ApiResponse<DashboardDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value).Data);

            Assert.Equal(119840m, dashboard.TotalAssetValue);
            Assert.Equal(119840m, Assert.Single(dashboard.AssetsByType).TotalValue);
        }
    }
}
