using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Cross-tenant isolation for AssetsController. The Asset global query filter is bypassed
/// outright for a super-admin (AppDbContext: `_tenantProvider.IsSuperAdmin() || ...`), so a
/// controller that leans on the filter alone lets a super admin whose current tenant is A read,
/// rewrite and hard-delete tenant B's assets, and see them in every list and total.
/// DepreciationPoliciesController shipped a Critical for exactly this shape.
///
/// Every caller here has a current tenant, so a "Select an organisation first." guard cannot be
/// what refuses them - only an explicit TenantId predicate can turn tenant B's row into a 404
/// or keep it out of a list.
/// </summary>
public class AssetTenantIsolationTests
{
    private static AssetsController ControllerFor(Data.AppDbContext db, ITenantProvider tenantProvider)
    {
        var controller = new AssetsController(
            db,
            new QrCodeService(new ConfigurationBuilder().Build()),
            null!,
            new LookupService(db),
            new AssetTagGenerator(db),
            tenantProvider);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "root-1"), new Claim(ClaimTypes.Role, Roles.SuperAdmin)],
                    "TestAuth"))
            }
        };
        return controller;
    }

    /// <summary>
    /// Seeds tenants A and B with an asset in each, returning tenant B's. The context itself runs
    /// as a super admin in tenant A, so the global filter lets every row through - which is the
    /// production condition being reproduced.
    /// </summary>
    private static async Task<(Data.AppDbContext Db, Microsoft.Data.Sqlite.SqliteConnection Conn,
        Guid TenantA, Asset OwnAsset, Asset OtherAsset)> SeedAsync()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));

        await TestDb.SeedTenantAsync(db, tenantA);
        await TestDb.SeedTenantAsync(db, tenantB);
        var own = await TestDb.SeedAssetAsync(db, tenantA, "LAP-A-001");
        var other = await TestDb.SeedAssetAsync(db, tenantB, "LAP-B-001");
        other.Notes = "Tenant B's notes";
        await db.SaveChangesAsync();

        // Precondition: the filter really is bypassed, so a pass below cannot come from it.
        Assert.True(await db.Assets.AnyAsync(a => a.Id == other.Id));

        db.ChangeTracker.Clear();
        return (db, conn, tenantA, own, other);
    }

    private static async Task<Asset?> ReloadAsync(Data.AppDbContext db, int id)
    {
        db.ChangeTracker.Clear();
        return await db.Assets.IgnoreQueryFilters().SingleOrDefaultAsync(a => a.Id == id);
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_delete_another_tenants_asset()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .DeleteAsset(other.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.NotNull(await ReloadAsync(db, other.Id));
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_delete_their_own_tenants_asset()
    {
        var (db, conn, tenantA, own, _) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .DeleteAsset(own.Id);

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Null(await ReloadAsync(db, own.Id));
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_update_another_tenants_asset()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .UpdateAsset(other.Id, new UpdateAssetDto { Notes = "Rewritten from tenant A" });

            Assert.IsType<NotFoundObjectResult>(result.Result);
            var reloaded = await ReloadAsync(db, other.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Tenant B's notes", reloaded.Notes);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_update_their_own_tenants_asset()
    {
        var (db, conn, tenantA, own, _) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .UpdateAsset(own.Id, new UpdateAssetDto { Notes = "Updated in tenant A" });

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal("Updated in tenant A", (await ReloadAsync(db, own.Id))!.Notes);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_read_another_tenants_asset()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAsset(other.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.NotNull(await ReloadAsync(db, other.Id));
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_read_their_own_tenants_asset()
    {
        var (db, conn, tenantA, own, _) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAsset(own.Id);

            Assert.IsType<OkObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_render_another_tenants_asset_qr_png()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetQrCodePng(other.Id, contentType: "tag");

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_render_another_tenants_asset_qr_svg()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetQrCodeSvg(other.Id, contentType: "tag");

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }

    // AssetTag is only unique *within* a tenant (IX TenantId+AssetTag), which is the exact
    // ambiguity the depreciation Critical hinged on: without the predicate, a tag lookup from
    // tenant A resolves whichever tenant's row the database happens to return first.

    [Fact]
    public async Task A_super_admin_caller_cannot_render_another_tenants_asset_qr_by_tag()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetQrCodeByTagPng(other.AssetTag, contentType: "tag");

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_scan_another_tenants_asset_tag()
    {
        var (db, conn, tenantA, _, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAssetByTag(other.AssetTag);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_scan_their_own_tenants_asset_tag()
    {
        var (db, conn, tenantA, own, _) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAssetByTag(own.AssetTag);

            Assert.IsType<OkObjectResult>(result.Result);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The list endpoints. Same bypass, read-only: a super admin in tenant A was shown every
    // tenant's assets, tags and totals. Each asset below is given a price, an assignee and an
    // expiring warranty so that every sub-query in the summary is pinned, not just the count.
    // ---------------------------------------------------------------------------------------

    private static async Task DecorateBothAsync(Data.AppDbContext db, Guid tenantA, Asset own, Asset other)
    {
        var tenantB = (await db.Assets.IgnoreQueryFilters().SingleAsync(a => a.Id == other.Id)).TenantId;
        await TestDb.SeedUserAsync(db, tenantA, "user-a", "Holder A");
        await TestDb.SeedUserAsync(db, tenantB, "user-b", "Holder B");

        foreach (var (id, price, userId) in new[] { (own.Id, 100m, "user-a"), (other.Id, 5000m, "user-b") })
        {
            var asset = await db.Assets.IgnoreQueryFilters().SingleAsync(a => a.Id == id);
            asset.PurchasePrice = price;
            asset.AssignedToUserId = userId;
            asset.WarrantyEndDate = DateTime.UtcNow.AddMonths(1);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_super_admin_caller_lists_only_their_own_tenants_assets()
    {
        var (db, conn, tenantA, own, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            await DecorateBothAsync(db, tenantA, own, other);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true));

            var page = Assert.IsType<PagedResponse<AssetDto>>(
                Assert.IsType<OkObjectResult>((await controller.GetAssets()).Result).Value);
            Assert.Equal(own.Id, Assert.Single(page.Items).Id);
            Assert.Equal(1, page.TotalCount);

            // A filter naming tenant B's user or tag must not reach tenant B's rows either.
            var byUser = Assert.IsType<PagedResponse<AssetDto>>(
                Assert.IsType<OkObjectResult>((await controller.GetAssets(assignedToUserId: "user-b")).Result).Value);
            Assert.Empty(byUser.Items);

            var bySearch = Assert.IsType<PagedResponse<AssetDto>>(
                Assert.IsType<OkObjectResult>((await controller.GetAssets(search: other.AssetTag)).Result).Value);
            Assert.Empty(bySearch.Items);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_lists_only_their_own_tenants_tags()
    {
        var (db, conn, tenantA, own, _) = await SeedAsync();
        using (db)
        using (conn)
        {
            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAllTags();

            var tags = Assert.IsType<List<string>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal([own.AssetTag], tags);
        }
    }

    [Fact]
    public async Task A_super_admin_callers_asset_summary_counts_only_their_own_tenant()
    {
        var (db, conn, tenantA, own, other) = await SeedAsync();
        using (db)
        using (conn)
        {
            await DecorateBothAsync(db, tenantA, own, other);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .GetAssetSummary();

            var summary = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
            object? Field(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
            int SumOfCounts(string name) =>
                ((System.Collections.IEnumerable)Field(summary, name)!).Cast<object>().Sum(g => (int)Field(g, "Count")!);

            Assert.Equal(1, Field(summary, "TotalAssets"));
            Assert.Equal(1, SumOfCounts("ByStatus"));
            Assert.Equal(1, SumOfCounts("ByDeviceType"));
            Assert.Equal(100m, Field(summary, "TotalValue"));
            Assert.Equal(1, Field(summary, "AssignedAssets"));
            Assert.Equal(1, Field(summary, "ExpiringWarranties"));
        }
    }
}
