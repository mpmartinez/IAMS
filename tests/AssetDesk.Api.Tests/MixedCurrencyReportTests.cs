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

        // A second group, so the per-group assertions below are about grouping and not just
        // about the grand total wearing a different name. USD 580 at 52.00 = 30,160 pesos, and
        // it is the only asset in both its device type and its status - so if the conversion
        // leaked into the wrong bucket, or the grouping key were ignored, the numbers move.
        // (580 * 52.00, rather than the laptops' rate of 58.20, is what makes the grand total -
        // and so the grand average below - divide evenly by the asset count.)
        db.Assets.Add(new Asset
        {
            TenantId = tenantId,
            AssetTag = "MON-0001",
            DeviceType = DeviceTypes.Monitor,
            Status = AssetStatus.InUse,
            PurchasePrice = 580m,
            Currency = Currencies.USD,
            ExchangeRate = 52.00m
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

            // 50,000 + (1,200 * 58.20) + (580 * 52.00) = 150,000. Naive summing gives 51,780.
            Assert.Equal(150000m, summary.GrandTotalValue);
            Assert.Equal(50000m, summary.AverageAssetValue);
            Assert.Equal(3, summary.TotalAssetCount);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);

            // Two device types, and the monitor's converted value belongs to exactly one of them.
            Assert.Equal(2, summary.ByDeviceType.Count);

            var laptops = Assert.Single(summary.ByDeviceType, g => g.DeviceType == DeviceTypes.Laptop);
            Assert.Equal(2, laptops.AssetCount);
            Assert.Equal(119840m, laptops.TotalValue);
            Assert.Equal(59920m, laptops.AverageValue);

            var monitors = Assert.Single(summary.ByDeviceType, g => g.DeviceType == DeviceTypes.Monitor);
            Assert.Equal(1, monitors.AssetCount);
            Assert.Equal(30160m, monitors.TotalValue);
            Assert.Equal(30160m, monitors.AverageValue);

            Assert.Equal(summary.GrandTotalValue, summary.ByDeviceType.Sum(g => g.TotalValue));

            // Same split by status, which is a different grouping key over the same rows.
            Assert.Equal(2, summary.ByStatus.Count);
            Assert.Equal(119840m, Assert.Single(summary.ByStatus, g => g.Status == AssetStatus.Available).TotalValue);
            Assert.Equal(30160m, Assert.Single(summary.ByStatus, g => g.Status == AssetStatus.InUse).TotalValue);
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

            Assert.Equal(150000m, dashboard.TotalAssetValue);

            Assert.Equal(2, dashboard.AssetsByType.Count);
            Assert.Equal(119840m,
                Assert.Single(dashboard.AssetsByType, g => g.DeviceType == DeviceTypes.Laptop).TotalValue);
            Assert.Equal(30160m,
                Assert.Single(dashboard.AssetsByType, g => g.DeviceType == DeviceTypes.Monitor).TotalValue);
        }
    }
}
