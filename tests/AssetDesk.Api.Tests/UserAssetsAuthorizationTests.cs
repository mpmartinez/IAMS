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
/// Covers the authorization gate on GET /api/assignments/users/{userId}/assets.
///
/// The endpoint carries no method-level policy - only the controller's bare [Authorize] - so
/// before the fix any authenticated principal could pass any userId and read that person's
/// assets. The tenant query filter contained the blast radius to one tenant, which is still a
/// cross-user leak inside it.
///
/// It cannot simply take "CanViewAssignments" like the neighbouring offboarding endpoint: a
/// self-service principal (Employee, Management, anyone holding only iams:tickets:file)
/// legitimately reads their OWN assets here. So the rule is self-or-permission, and both halves
/// need proving.
///
/// Controllers are constructed directly with a hand-built ClaimsPrincipal - the pattern the
/// rest of this suite uses (see PermissionGateTests, ProfileSelfServiceTests). No
/// WebApplicationFactory.
/// </summary>
public class UserAssetsAuthorizationTests
{
    private static ClaimsPrincipal BuildPrincipal(
        string userId, string[]? roles = null, string[]? permissions = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles ?? [])
            claims.Add(new Claim(ClaimTypes.Role, role));
        foreach (var permission in permissions ?? [])
            claims.Add(new Claim(Permissions.ClaimType, permission));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static AssignmentsController BuildController(AppDbContext db, ClaimsPrincipal principal) =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

    private static async Task AssignAssetAsync(AppDbContext db, Guid tenantId, string assetTag, string userId)
    {
        var asset = await TestDb.SeedAssetAsync(db, tenantId, assetTag, AssetStatus.InUse);
        asset.AssignedToUserId = userId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetUserAssets_lets_a_self_service_user_read_their_own_assets()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Self Service User");
            await AssignAssetAsync(db, tenantId, "LT-001", "emp-1");

            // The floor case: an Employee whose only grant is filing tickets. No assignments
            // permission at all, so this passes only via the self-service branch.
            var controller = BuildController(db, BuildPrincipal(
                "emp-1", roles: [Roles.Employee], permissions: [Permissions.TicketsFile]));

            var result = await controller.GetUserAssets("emp-1");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<UserAssetsDto>(ok.Value);
            Assert.Equal("emp-1", body.UserId);
            Assert.Equal("LT-001", Assert.Single(body.CurrentAssets).AssetTag);
        }
    }

    [Fact]
    public async Task GetUserAssets_forbids_a_user_without_the_permission_from_reading_someone_elses_assets()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Nosy Colleague");
            await TestDb.SeedUserAsync(db, tenantId, "emp-2", "Target");
            await AssignAssetAsync(db, tenantId, "LT-002", "emp-2");

            var controller = BuildController(db, BuildPrincipal(
                "emp-1", roles: [Roles.Employee], permissions: [Permissions.TicketsFile]));

            var result = await controller.GetUserAssets("emp-2");

            Assert.IsType<ForbidResult>(result.Result);
        }
    }

    [Fact]
    public async Task GetUserAssets_forbids_before_disclosing_whether_the_user_id_exists()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Nosy Colleague");

            var controller = BuildController(db, BuildPrincipal(
                "emp-1", roles: [Roles.Employee], permissions: [Permissions.TicketsFile]));

            // A 404 here where a real id yields 403 would hand an unprivileged caller a user-id
            // oracle. Both answers must be the same 403.
            var result = await controller.GetUserAssets("no-such-user");

            Assert.IsType<ForbidResult>(result.Result);
        }
    }

    [Fact]
    public async Task GetUserAssets_allows_an_assignments_viewer_to_read_someone_elses_assets()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Asset Manager");
            await TestDb.SeedUserAsync(db, tenantId, "emp-2", "Target");
            await AssignAssetAsync(db, tenantId, "LT-003", "emp-2");

            var controller = BuildController(db, BuildPrincipal(
                "staff-1", roles: [Roles.Staff], permissions: [Permissions.AssignmentsView]));

            var result = await controller.GetUserAssets("emp-2");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<UserAssetsDto>(ok.Value);
            Assert.Equal("emp-2", body.UserId);
            Assert.Equal("LT-003", Assert.Single(body.CurrentAssets).AssetTag);
        }
    }

    [Fact]
    public async Task GetUserAssets_keeps_the_SuperAdmin_bypass()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "root", "Platform Operator");
            await TestDb.SeedUserAsync(db, tenantId, "emp-2", "Target");
            await AssignAssetAsync(db, tenantId, "LT-004", "emp-2");

            // SuperAdmin holds no permission claims but bypasses every check elsewhere
            // (PermissionAuthorizationHandler, ClaimsPrincipalExtensions.HasPermission). This
            // gate must not become the one place that behaves differently.
            var controller = BuildController(db, BuildPrincipal("root", roles: [Roles.SuperAdmin]));

            var result = await controller.GetUserAssets("emp-2");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal("emp-2", Assert.IsType<UserAssetsDto>(ok.Value).UserId);
        }
    }
}
