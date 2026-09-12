using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The report's job is to be trustworthy about what it could not compute. An asset with no
/// price, no purchase date, or no policy for its device type is counted and named, never
/// silently folded in as zero.
/// </summary>
public class DepreciationReportTests
{
    private static async Task SeedAsync(AppDbContext db, Guid tenantId)
    {
        db.DepreciationPolicies.Add(new DepreciationPolicy
        {
            TenantId = tenantId, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
        });

        // Depreciable: 60,000 pesos, bought 36 months before the as-of date, so fully depreciated
        // and holding at its 6,000 residual.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0001", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 60000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2023, 1, 15)
        });

        // Depreciable and USD: 1,200 at 58.20 = 69,840 pesos.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0002", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 1200m, Currency = Currencies.USD,
            ExchangeRate = 58.20m, PurchaseDate = new DateTime(2026, 1, 10)
        });

        // Not depreciable - no purchase date.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0003", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 40000m, Currency = Currencies.PHP,
            ExchangeRate = 1m
        });

        // Not depreciable - Monitor has no policy row.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "MON-0001", DeviceType = DeviceTypes.Monitor,
            Status = AssetStatus.Available, PurchasePrice = 15000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2025, 6, 1)
        });

        // Excluded entirely - Retired, matching the existing value report's exclusions.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0099", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Retired, PurchasePrice = 99000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2020, 1, 1)
        });

        // Excluded entirely - Lost, same exclusion as Retired.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0098", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Lost, PurchasePrice = 88000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2020, 1, 1)
        });

        await db.SaveChangesAsync();
    }

    private static DepreciationSummaryDto Unwrap(ActionResult<ApiResponse<DepreciationSummaryDto>> r) =>
        Assert.IsType<DepreciationSummaryDto>(
            Assert.IsType<ApiResponse<DepreciationSummaryDto>>(
                Assert.IsType<OkObjectResult>(r.Result).Value).Data);

    [Fact]
    public async Task Retired_assets_are_excluded_and_the_rest_are_reported()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            Assert.Equal(4, summary.Rows.Count);
            Assert.DoesNotContain(summary.Rows, r => r.AssetTag == "LAP-0099");
            Assert.DoesNotContain(summary.Rows, r => r.AssetTag == "LAP-0098");
        }
    }

    [Fact]
    public async Task The_not_depreciable_assets_are_counted_and_named()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            Assert.Equal(2, summary.DepreciableCount);
            Assert.Equal(2, summary.NotDepreciableCount);

            Assert.Equal(1, Assert.Single(summary.NotDepreciableByReason,
                x => x.Reason == NotDepreciableReasons.NoPurchaseDate).Count);
            Assert.Equal(1, Assert.Single(summary.NotDepreciableByReason,
                x => x.Reason == NotDepreciableReasons.NoPolicy).Count);

            // The whole point of the feature: an undepreciable row carries null, not zero, so
            // it can never be silently folded into a total or rendered as "0.00".
            var noPolicy = Assert.Single(summary.Rows, r => r.AssetTag == "MON-0001");
            Assert.Null(noPolicy.NetBookValue);
            Assert.Null(noPolicy.AccumulatedDepreciation);
            Assert.Null(noPolicy.ElapsedMonths);

            var noPurchaseDate = Assert.Single(summary.Rows, r => r.AssetTag == "LAP-0003");
            Assert.Null(noPurchaseDate.NetBookValue);
            Assert.Null(noPurchaseDate.AccumulatedDepreciation);
            Assert.Null(noPurchaseDate.ElapsedMonths);
        }
    }

    [Fact]
    public async Task Totals_are_in_pesos_and_only_depreciable_assets_carry_book_value()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            // Cost basis spans every reported asset: 60,000 + 69,840 + 40,000 + 15,000.
            Assert.Equal(184840m, summary.TotalCostBasis);

            // LAP-0001 bought Jan 2023 is month 38 of 36 - fully depreciated, holding at its
            // 6,000 residual, so 54,000 accumulated.
            // LAP-0002 bought Jan 2026, as-of Feb 2026, is month 2 of 36 on 69,840 with 10%
            // residual: (69,840 - 6,984) / 36 * 2 = 3,492.
            Assert.Equal(57492m, summary.TotalAccumulatedDepreciation);

            // Book value counts only the two depreciable assets: 6,000 + (69,840 - 3,492).
            Assert.Equal(72348m, summary.TotalNetBookValue);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);
        }
    }

    [Fact]
    public async Task A_fully_depreciated_asset_is_flagged_as_such()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            var old = Assert.Single(summary.Rows, r => r.AssetTag == "LAP-0001");
            Assert.True(old.IsFullyDepreciated);
            Assert.Equal(6000m, old.NetBookValue);

            var newer = Assert.Single(summary.Rows, r => r.AssetTag == "LAP-0002");
            Assert.False(newer.IsFullyDepreciated);
        }
    }

    [Fact]
    public async Task A_super_admin_matches_each_asset_against_its_own_tenants_policy()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Super-admin provider so the query filter admits every tenant's rows - the scenario
        // where a naive DeviceType-only dictionary collides on the duplicate Laptop key.
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            // Same device type, deliberately different useful life so the two book values
            // cannot coincidentally match.
            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantA, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 0m
            });
            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantB, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 60, ResidualPercent = 0m
            });

            // Both bought Feb 2024, as-of Feb 2026 - 25 elapsed months either way (full-month
            // convention counts the purchase month), so only the useful life differs between
            // the two.
            db.Assets.Add(new Asset
            {
                TenantId = tenantA, AssetTag = "A-LAP-0001", DeviceType = DeviceTypes.Laptop,
                Status = AssetStatus.Available, PurchasePrice = 36000m, Currency = Currencies.PHP,
                ExchangeRate = 1m, PurchaseDate = new DateTime(2024, 2, 1)
            });
            db.Assets.Add(new Asset
            {
                TenantId = tenantB, AssetTag = "B-LAP-0001", DeviceType = DeviceTypes.Laptop,
                Status = AssetStatus.Available, PurchasePrice = 36000m, Currency = Currencies.PHP,
                ExchangeRate = 1m, PurchaseDate = new DateTime(2024, 2, 1)
            });

            await db.SaveChangesAsync();

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            // Tenant A: 36,000 / 36 * 25 = 25,000 accumulated -> 11,000 book value.
            var rowA = Assert.Single(summary.Rows, r => r.AssetTag == "A-LAP-0001");
            Assert.Equal(11000m, rowA.NetBookValue);

            // Tenant B: 36,000 / 60 * 25 = 15,000 accumulated -> 21,000 book value.
            var rowB = Assert.Single(summary.Rows, r => r.AssetTag == "B-LAP-0001");
            Assert.Equal(21000m, rowB.NetBookValue);
        }
    }
}
