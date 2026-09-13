using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Cross-tenant isolation for AssignmentsController. Two different holes:
///
/// - Assets and assignments carry a global query filter with an IsSuperAdmin() bypass, so a
///   super admin whose current tenant is A reached tenant B's assets and assignment rows.
/// - ApplicationUser has no tenant query filter at all, so every lookup of a user by id found
///   users in any tenant for every caller - a Staff user in tenant A could assign their asset
///   to tenant B's employee or read that employee's offboarding summary.
///
/// The context and the controller share one tenant provider, as they do in production. Every
/// caller has a current tenant, so a "Select an organisation first." guard cannot be what
/// refuses them - only an explicit TenantId predicate can.
/// </summary>
public class AssignmentTenantIsolationTests
{
    private sealed record Seeded(
        AppDbContext Db, SqliteConnection Conn, FakeTenantProvider Provider, bool IsSuperAdmin,
        Asset OwnFree, Asset OtherFree, Asset OwnHeld, Asset OtherHeld) : IDisposable
    {
        public void Dispose()
        {
            Db.Dispose();
            Conn.Dispose();
        }
    }

    /// <summary>
    /// Tenant A: admin-a (the caller), user-a (inactive, holding LAP-A-002), LAP-A-001 free.
    /// Tenant B: the mirror image. Both holders are inactive so both show up as pending
    /// offboardings unless the query is scoped.
    /// </summary>
    private static async Task<Seeded> SeedAsync(bool isSuperAdmin)
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var provider = new FakeTenantProvider(tenantA, isSuperAdmin);
        var (db, conn) = TestDb.Create(provider);

        await TestDb.SeedTenantAsync(db, tenantA);
        await TestDb.SeedTenantAsync(db, tenantB);
        await TestDb.SeedUserAsync(db, tenantA, "admin-a", "Admin A");
        await TestDb.SeedUserAsync(db, tenantB, "admin-b", "Admin B");
        var userA = await TestDb.SeedUserAsync(db, tenantA, "user-a", "Holder A");
        var userB = await TestDb.SeedUserAsync(db, tenantB, "user-b", "Holder B");
        userA.IsActive = false;
        userB.IsActive = false;

        var ownFree = await TestDb.SeedAssetAsync(db, tenantA, "LAP-A-001");
        var otherFree = await TestDb.SeedAssetAsync(db, tenantB, "LAP-B-001");
        var ownHeld = await HoldAsync(db, tenantA, "LAP-A-002", "user-a", "admin-a");
        var otherHeld = await HoldAsync(db, tenantB, "LAP-B-002", "user-b", "admin-b");

        db.ChangeTracker.Clear();
        return new Seeded(db, conn, provider, isSuperAdmin, ownFree, otherFree, ownHeld, otherHeld);
    }

    private static async Task<Asset> HoldAsync(
        AppDbContext db, Guid tenantId, string assetTag, string userId, string assignedBy)
    {
        var asset = await TestDb.SeedAssetAsync(db, tenantId, assetTag, AssetStatus.InUse);
        asset.AssignedToUserId = userId;
        db.AssetAssignments.Add(new AssetAssignment
        {
            TenantId = tenantId,
            AssetId = asset.Id,
            UserId = userId,
            AssignedByUserId = assignedBy,
            AssignedAt = DateTime.UtcNow.AddDays(-30)
        });
        await db.SaveChangesAsync();
        return asset;
    }

    private static AssignmentsController ControllerFor(Seeded s)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-a"),
            new(Permissions.ClaimType, Permissions.AssignmentsView)
        };
        if (s.IsSuperAdmin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.SuperAdmin));

        return new AssignmentsController(s.Db, s.Provider)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
    }

    private static async Task<Asset> ReloadAssetAsync(Seeded s, int id)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.Assets.IgnoreQueryFilters().SingleAsync(a => a.Id == id);
    }

    private static async Task<bool> HasOpenAssignmentAsync(Seeded s, int assetId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.AssetAssignments.IgnoreQueryFilters()
            .AnyAsync(a => a.AssetId == assetId && a.ReturnedAt == null);
    }

    // --- Asset by id: the super-admin bypass ---------------------------------------------------

    [Fact]
    public async Task A_super_admin_caller_cannot_assign_another_tenants_asset()
    {
        using var s = await SeedAsync(isSuperAdmin: true);

        var result = await ControllerFor(s).AssignAsset(s.OtherFree.Id, new AssignAssetRequest { UserId = "user-a" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Null((await ReloadAssetAsync(s, s.OtherFree.Id)).AssignedToUserId);
        Assert.False(await HasOpenAssignmentAsync(s, s.OtherFree.Id));
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_return_another_tenants_asset()
    {
        using var s = await SeedAsync(isSuperAdmin: true);

        var result = await ControllerFor(s).ReturnAsset(s.OtherHeld.Id, new ReturnAssetRequest());

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("user-b", (await ReloadAssetAsync(s, s.OtherHeld.Id)).AssignedToUserId);
        Assert.True(await HasOpenAssignmentAsync(s, s.OtherHeld.Id));
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_read_another_tenants_asset_history()
    {
        using var s = await SeedAsync(isSuperAdmin: true);

        var result = await ControllerFor(s).GetAssetHistory(s.OtherHeld.Id);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // --- User by id: no filter on ApplicationUser, so every caller ------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assigning_to_a_user_in_another_tenant_is_rejected(bool isSuperAdmin)
    {
        using var s = await SeedAsync(isSuperAdmin);

        var result = await ControllerFor(s).AssignAsset(s.OwnFree.Id, new AssignAssetRequest { UserId = "user-b" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null((await ReloadAssetAsync(s, s.OwnFree.Id)).AssignedToUserId);
        Assert.False(await HasOpenAssignmentAsync(s, s.OwnFree.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_tenants_users_assets_are_not_found(bool isSuperAdmin)
    {
        using var s = await SeedAsync(isSuperAdmin);

        var result = await ControllerFor(s).GetUserAssets("user-b");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_tenants_users_offboarding_summary_is_not_found(bool isSuperAdmin)
    {
        using var s = await SeedAsync(isSuperAdmin);

        var result = await ControllerFor(s).GetOffboardingSummary("user-b");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_tenants_user_cannot_be_bulk_returned(bool isSuperAdmin)
    {
        using var s = await SeedAsync(isSuperAdmin);

        var result = await ControllerFor(s).BulkReturnAssets(
            "user-b", new BulkReturnRequest { AssetIds = [s.OtherHeld.Id], ReturnCondition = "Good" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("user-b", (await ReloadAssetAsync(s, s.OtherHeld.Id)).AssignedToUserId);
        Assert.True(await HasOpenAssignmentAsync(s, s.OtherHeld.Id));
    }

    // --- Lists: the super-admin bypass ----------------------------------------------------------

    [Fact]
    public async Task A_super_admin_callers_pending_offboardings_are_their_own_tenants()
    {
        using var s = await SeedAsync(isSuperAdmin: true);

        var result = await ControllerFor(s).GetPendingOffboardings();

        var items = Assert.IsType<List<OffboardingSummaryItem>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("user-a", Assert.Single(items).UserId);
    }

    [Fact]
    public async Task A_super_admin_callers_assignment_audit_log_is_their_own_tenants()
    {
        using var s = await SeedAsync(isSuperAdmin: true);
        var controller = ControllerFor(s);

        var all = Assert.IsType<PagedResponse<AssetAssignmentDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetAuditLog()).Result).Value);
        Assert.Equal(s.OwnHeld.Id, Assert.Single(all.Items).AssetId);
        Assert.Equal(1, all.TotalCount);

        var byUser = Assert.IsType<PagedResponse<AssetAssignmentDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetAuditLog(userId: "user-b")).Result).Value);
        Assert.Empty(byUser.Items);
    }

    // --- The caller's own tenant still works ----------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_callers_own_tenant_is_unaffected(bool isSuperAdmin)
    {
        using var s = await SeedAsync(isSuperAdmin);
        var controller = ControllerFor(s);

        Assert.IsType<OkObjectResult>(
            (await controller.AssignAsset(s.OwnFree.Id, new AssignAssetRequest { UserId = "user-a" })).Result);
        s.Db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>((await controller.ReturnAsset(s.OwnFree.Id, new ReturnAssetRequest())).Result);
        s.Db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>((await controller.GetAssetHistory(s.OwnFree.Id)).Result);
        Assert.IsType<OkObjectResult>((await controller.GetUserAssets("user-a")).Result);
        Assert.IsType<OkObjectResult>((await controller.GetOffboardingSummary("user-a")).Result);
        s.Db.ChangeTracker.Clear();

        var bulk = await controller.BulkReturnAssets(
            "user-a", new BulkReturnRequest { AssetIds = [s.OwnHeld.Id], ReturnCondition = "Good" });
        var body = Assert.IsType<ApiResponse<BulkReturnResult>>(Assert.IsType<OkObjectResult>(bulk.Result).Value);
        Assert.Equal(1, body.Data!.TotalReturned);
        Assert.False(await HasOpenAssignmentAsync(s, s.OwnHeld.Id));
    }

    // Separate from the predicate tests above: this one is the guard. With no current tenant the
    // tenant stamping in SaveChanges skips, and the assignment row would belong to no one.
    [Fact]
    public async Task Assigning_with_no_current_tenant_is_refused()
    {
        using var s = await SeedAsync(isSuperAdmin: true);
        s.Provider.ClearTenantOverride();

        var result = await ControllerFor(s).AssignAsset(s.OwnFree.Id, new AssignAssetRequest { UserId = "user-a" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await HasOpenAssignmentAsync(s, s.OwnFree.Id));
    }
}
