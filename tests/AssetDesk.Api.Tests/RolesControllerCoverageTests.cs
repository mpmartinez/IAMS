using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Covers three guards in RolesController that had zero test coverage - each could be deleted
/// with the full suite still green - plus a happy-path CreateRole test (the existing escalation
/// test only proves CreateRole rejects overreach; it would still pass if CreateRole always
/// failed for any reason).
/// </summary>
public class RolesControllerCoverageTests
{
    private static RoleManager<ApplicationRole> CreateRoleManager(AppDbContext db)
    {
        var store = new RoleStore<ApplicationRole, AppDbContext, string>(db);
        return new RoleManager<ApplicationRole>(
            store,
            roleValidators: [],
            keyNormalizer: new UpperInvariantLookupNormalizer(),
            errors: new IdentityErrorDescriber(),
            logger: NullLogger<RoleManager<ApplicationRole>>.Instance);
    }

    private static ClaimsPrincipal BuildPrincipal(params string[] permissions) =>
        TestPrincipals.With(permissions);

    private static RolesController BuildController(
        AppDbContext db, Guid tenantId, ClaimsPrincipal principal, bool isSuperAdmin = false)
    {
        var controller = new RolesController(
            db, CreateRoleManager(db), new FakeTenantProvider(tenantId, isSuperAdmin),
            NullLogger<RolesController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static async Task<ApplicationRole> SeedBuiltInRoleAsync(AppDbContext db, string name)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}-{Guid.NewGuid():N}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = true,
            TenantId = null
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task GrantRoleToUserAsync(AppDbContext db, Guid tenantId, string roleId, string userId)
    {
        await TestDb.SeedUserAsync(db, tenantId, userId, "Role Holder");
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
        {
            UserId = userId,
            RoleId = roleId
        });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------
    // RolesController.cs:95-98 - UserCountsByRole's tenant scoping (via GetRoles, the only
    // caller that surfaces the count).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRoles_UserCount_ExcludesHoldersOfTheSameRoleInAnotherTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var role = await SeedBuiltInRoleAsync(db, "SharedBuiltIn");

            await GrantRoleToUserAsync(db, tenantA, role.Id, "a-holder");
            await GrantRoleToUserAsync(db, tenantB, role.Id, "b-holder");

            var controller = BuildController(db, tenantA, BuildPrincipal(Permissions.RolesView));

            var result = await controller.GetRoles();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<List<RoleDto>>>(ok.Value);
            var dto = Assert.Single(body.Data!, r => r.Id == role.Id);
            Assert.Equal(1, dto.UserCount);
        }
    }

    // ---------------------------------------------------------------------------------------
    // RolesController.cs:67 - GetRoles' rp.TenantId == tenantId filter on the grants read. A
    // cross-tenant disclosure guard: without it, tenant A would see tenant B's customisation of
    // a shared built-in role's grants.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRoles_OnlyReturnsThisTenantsGrantsForASharedBuiltInRole()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var role = await SeedBuiltInRoleAsync(db, "SharedBuiltIn2");

            db.RolePermissions.AddRange(
                new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantA, Permission = Permissions.AssetsView },
                new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantB, Permission = Permissions.AssetsDelete });
            await db.SaveChangesAsync();

            var controller = BuildController(db, tenantA, BuildPrincipal(Permissions.RolesView));

            var result = await controller.GetRoles();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<List<RoleDto>>>(ok.Value);
            var dto = Assert.Single(body.Data!, r => r.Id == role.Id);
            Assert.Equal([Permissions.AssetsView], dto.Permissions);
        }
    }

    // ---------------------------------------------------------------------------------------
    // RolesController.cs:114 - GetAssignable's SuperAdmin exclusion.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAssignable_ExcludesSuperAdmin_ForANonSuperAdminCaller()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedBuiltInRoleAsync(db, Roles.SuperAdmin);
            await SeedBuiltInRoleAsync(db, "Staff5");

            var controller = BuildController(db, tenantId, BuildPrincipal(), isSuperAdmin: false);

            var result = await controller.GetAssignable();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<List<AssignableRoleDto>>>(ok.Value);
            Assert.DoesNotContain(body.Data!, r => r.Name == Roles.SuperAdmin);
            Assert.Contains(body.Data!, r => r.Name == "Staff5");
        }
    }

    [Fact]
    public async Task GetAssignable_IncludesSuperAdmin_ForASuperAdminCaller()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedBuiltInRoleAsync(db, Roles.SuperAdmin);

            var controller = BuildController(db, tenantId, BuildPrincipal(), isSuperAdmin: true);

            var result = await controller.GetAssignable();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<List<AssignableRoleDto>>>(ok.Value);
            Assert.Contains(body.Data!, r => r.Name == Roles.SuperAdmin);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Happy-path CreateRole. RolesApiTests / RolesControllerGuardrailTests only exercise the
    // rejection paths - a CreateRole that always failed would still pass those.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateRole_WithGrantsTheActorHolds_PersistsTheRoleAndItsGrants()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var controller = BuildController(
                db, tenantId, BuildPrincipal(Permissions.RolesManage, Permissions.AssetsView));

            var result = await controller.CreateRole(new CreateRoleDto
            {
                Name = "FieldTech",
                Description = "Read-only asset access",
                Permissions = [Permissions.AssetsView]
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<RoleDto>>(ok.Value);
            Assert.True(body.Success);
            Assert.Equal("FieldTech", body.Data!.Name);
            Assert.Equal([Permissions.AssetsView], body.Data.Permissions);

            var persisted = await db.Roles.SingleAsync(r => r.Name == "FieldTech");
            Assert.False(persisted.IsBuiltIn);
            Assert.Equal(tenantId, persisted.TenantId);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == persisted.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission)
                .ToListAsync();
            Assert.Equal([Permissions.AssetsView], grants);
        }
    }
}
