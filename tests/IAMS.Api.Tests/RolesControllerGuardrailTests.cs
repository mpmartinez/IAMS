using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Controllers;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAMS.Api.Tests;

/// <summary>
/// Covers the four security guardrails of RolesController that RolesApiTests (pinned by the
/// task brief) does not reach, because that file only exercises the pure static GrantableKeys
/// helper. RoleManager here is a real RoleStore over the test AppDbContext, following the
/// pattern TokenServiceClaimsTests established for UserManager - not a mock.
/// </summary>
public class RolesControllerGuardrailTests
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
        var controller = new RolesController(db, CreateRoleManager(db), new FakeTenantProvider(tenantId, isSuperAdmin));
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

    private static async Task<ApplicationRole> SeedCustomRoleAsync(
        AppDbContext db, Guid tenantId, string name, params string[] grants)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}-{Guid.NewGuid():N}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = false,
            TenantId = tenantId
        };
        db.Roles.Add(role);
        db.RolePermissions.AddRange(grants.Select(p => new RolePermission
        {
            Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantId, Permission = p
        }));
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
    // Guardrail 1: no privilege escalation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateRole_RejectsAPermissionTheActorDoesNotHold_AndCreatesNothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            // Actor holds RolesManage (enough to pass the [Authorize] policy in production) but
            // not AssetsDelete, and tries to mint a role holding both.
            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.CreateRole(new CreateRoleDto
            {
                Name = "PowerRole",
                Permissions = [Permissions.RolesManage, Permissions.AssetsDelete]
            });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<RoleDto>>(bad.Value);
            Assert.False(body.Success);
            Assert.False(await db.Roles.AnyAsync(r => r.Name == "PowerRole"));
            Assert.Empty(await db.RolePermissions.Where(rp => rp.TenantId == tenantId).ToListAsync());
        }
    }

    [Fact]
    public async Task UpdateRole_RejectsAPermissionTheActorDoesNotHold_AndLeavesGrantsUnchanged()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(db, tenantId, "CustomRole", Permissions.RolesManage);

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.RolesManage, Permissions.AssetsDelete]
            });

            Assert.IsType<BadRequestObjectResult>(result.Result);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission)
                .ToListAsync();
            Assert.Equal([Permissions.RolesManage], grants);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Guardrail 2: no cross-tenant access - a foreign tenant's custom role 404s
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateRole_OnAnotherTenantsCustomRole_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var roleInA = await SeedCustomRoleAsync(db, tenantA, "TenantARole", Permissions.AssetsView);

            // Actor belongs to tenant B, holds every permission it attempts to grant.
            var controller = BuildController(
                db, tenantB, BuildPrincipal(Permissions.RolesManage, Permissions.AssetsView));

            var result = await controller.UpdateRole(roleInA.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.AssetsView]
            });

            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    [Fact]
    public async Task DeleteRole_OnAnotherTenantsCustomRole_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var roleInA = await SeedCustomRoleAsync(db, tenantA, "TenantARole", Permissions.AssetsView);

            var controller = BuildController(db, tenantB, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.DeleteRole(roleInA.Id);

            Assert.IsType<NotFoundResult>(result);
            // The role and its grants must survive an attempt that never should have found it.
            Assert.True(await db.Roles.AnyAsync(r => r.Id == roleInA.Id));
            Assert.True(await db.RolePermissions.AnyAsync(rp => rp.RoleId == roleInA.Id));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Guardrail 4 (delete half): built-in roles cannot be deleted; custom roles held by users
    // cannot be deleted either, and both survive the attempt.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteRole_OnABuiltInRole_IsRejected_AndSurvives()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedBuiltInRoleAsync(db, "SomeBuiltIn");

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.DeleteRole(role.Id);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.True(await db.Roles.AnyAsync(r => r.Id == role.Id));
        }
    }

    [Fact]
    public async Task DeleteRole_WithCurrentHolders_Returns409_AndRoleAndGrantsSurvive()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(db, tenantId, "HeldRole", Permissions.AssetsView);
            await GrantRoleToUserAsync(db, tenantId, role.Id, "holder-1");

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.DeleteRole(role.Id);

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            var body = Assert.IsType<ApiResponse<object>>(conflict.Value);
            Assert.False(body.Success);

            Assert.True(await db.Roles.AnyAsync(r => r.Id == role.Id));
            Assert.True(await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.TenantId == tenantId));
        }
    }

    [Fact]
    public async Task DeleteRole_OnACustomRoleNobodyHolds_RemovesRoleAndItsGrants()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(db, tenantId, "UnheldRole", Permissions.AssetsView);

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.DeleteRole(role.Id);

            Assert.IsType<NoContentResult>(result);
            Assert.False(await db.Roles.AnyAsync(r => r.Id == role.Id));
            Assert.False(await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id));
        }
    }

    // ---------------------------------------------------------------------------------------
    // TenantId isolation on built-in roles: editing one tenant's copy of a shared built-in
    // role's grants must not disturb another tenant's copy of the same role.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateRole_OnABuiltInRole_DoesNotChangeAnotherTenantsGrantsForTheSameRole()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var role = await SeedBuiltInRoleAsync(db, "Staff2");

            db.RolePermissions.AddRange(
                new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantA, Permission = Permissions.AssetsView },
                new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantB, Permission = Permissions.AssetsView });
            await db.SaveChangesAsync();

            // SuperAdmin actor to isolate this test to the tenant-scoping guarantee, not the
            // escalation guard already covered above.
            var controller = BuildController(db, tenantA, BuildPrincipal(), isSuperAdmin: true);

            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.AssetsDelete]
            });

            Assert.IsType<OkObjectResult>(result.Result);

            var tenantAGrants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantA)
                .Select(rp => rp.Permission).ToListAsync();
            var tenantBGrants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantB)
                .Select(rp => rp.Permission).ToListAsync();

            Assert.Equal([Permissions.AssetsDelete], tenantAGrants);
            Assert.Equal([Permissions.AssetsView], tenantBGrants);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Guardrail 3: SuperAdmin's grants are immutable, even to another SuperAdmin
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateRole_OnSuperAdminRole_IsRejected_EvenForASuperAdminActor()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedBuiltInRoleAsync(db, Roles.SuperAdmin);
            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantId, Permission = Permissions.AssetsView
            });
            await db.SaveChangesAsync();

            var controller = BuildController(db, tenantId, BuildPrincipal(), isSuperAdmin: true);

            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.AssetsDelete]
            });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<RoleDto>>(bad.Value);
            Assert.False(body.Success);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission).ToListAsync();
            Assert.Equal([Permissions.AssetsView], grants);
        }
    }
}
