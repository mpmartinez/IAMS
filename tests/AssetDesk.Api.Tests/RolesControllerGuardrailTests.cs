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

    private static async Task GrantRoleToInactiveUserAsync(AppDbContext db, Guid tenantId, string roleId, string userId)
    {
        var user = await TestDb.SeedUserAsync(db, tenantId, userId, "Inactive Role Holder");
        user.IsActive = false;
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
    public async Task UpdateRole_SilentlyDropsAPermissionTheActorDoesNotHold_AndLeavesGrantsUnchanged()
    {
        // Whole-set validation used to reject this outright. Delta-based validation (see
        // RolesController.Validate) instead applies the actor's change only within their own
        // grantable set: AssetsDelete was never in the role and the actor cannot grant it, so it
        // is simply dropped rather than added - the save succeeds and nothing changes.
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

            Assert.IsType<OkObjectResult>(result.Result);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission)
                .ToListAsync();
            Assert.Equal([Permissions.RolesManage], grants);
        }
    }

    [Fact]
    public async Task UpdateRole_ByAnActorHoldingOnlyRolesManage_OnARoleThatAlsoCarriesAssetsDelete_SucceedsAndPreservesAssetsDelete()
    {
        // The DEAD END this fix targets: a restricted role-manager (holds only iams:roles:manage)
        // opens a role that also carries assets:delete, a permission they do not hold. The UI
        // renders that box disabled-but-checked and Save still posts it, since disabled inputs
        // don't stop the bound value being submitted. Whole-set validation rejected every such
        // save with no way to clear the error - this actor could never edit a role richer than
        // their own grants. Delta-based validation preserves assets:delete (outside the actor's
        // grantable set, so untouched) while still letting the actor's own change - dropping
        // tickets:file, which they do hold - go through.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(
                db, tenantId, "RicherRole", Permissions.RolesManage, Permissions.AssetsDelete, Permissions.TicketsFile);

            var controller = BuildController(
                db, tenantId, BuildPrincipal(Permissions.RolesManage, Permissions.TicketsFile));

            // Simulates the UI submitting every checked box: the two the actor holds (one
            // unchanged, one they deliberately unchecked - TicketsFile is simply absent here) plus
            // AssetsDelete, which stayed checked because it was rendered disabled.
            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.RolesManage, Permissions.AssetsDelete]
            });

            Assert.IsType<OkObjectResult>(result.Result);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission)
                .ToListAsync();
            Assert.Contains(Permissions.AssetsDelete, grants);
            Assert.Contains(Permissions.RolesManage, grants);
            Assert.DoesNotContain(Permissions.TicketsFile, grants);
        }
    }

    [Fact]
    public async Task UpdateRole_CannotStripAPermissionTheActorDoesNotHold_EvenBySubmittingAStrictSubset()
    {
        // The DESTRUCTION this fix targets: an actor holding only iams:roles:manage submits a
        // strict subset of a richer role's permissions (e.g. every box they can see unchecked, or
        // a hand-crafted request). Whole-set validation accepted that subset as the new truth,
        // silently stripping assets:delete even though the actor never held it and never asked to
        // remove it specifically. Delta-based validation preserves it regardless of what was
        // submitted.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(
                db, tenantId, "AdminLikeRole", Permissions.RolesManage, Permissions.AssetsDelete);

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto
            {
                Permissions = [Permissions.RolesManage]
            });

            Assert.IsType<OkObjectResult>(result.Result);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission)
                .ToListAsync();
            Assert.Contains(Permissions.AssetsDelete, grants);
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

    // ---------------------------------------------------------------------------------------
    // IMPORTANT: the iams:roles:manage lockout guard, previously untested (it could be deleted
    // entirely and the suite stayed green). RolesController.UpdateRole is one of two places that
    // can strip the tenant's last active role-management coverage - UsersController's paths are
    // covered in UsersControllerRoleAssignmentTests.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateRole_StrippingRolesManageFromTheOnlyCoveringRole_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var role = await SeedCustomRoleAsync(db, tenantId, "RoleManagerOnly", Permissions.RolesManage);
            await GrantRoleToUserAsync(db, tenantId, role.Id, "holder-1");

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.UpdateRole(role.Id, new UpdateRoleDto { Permissions = [] });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<RoleDto>>(bad.Value);
            Assert.False(body.Success);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission).ToListAsync();
            Assert.Equal([Permissions.RolesManage], grants);
        }
    }

    [Fact]
    public async Task UpdateRole_StrippingRolesManage_IsAllowed_WhenAnotherUserHeldRoleStillHasIt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var roleA = await SeedCustomRoleAsync(db, tenantId, "RoleManagerA", Permissions.RolesManage);
            var roleB = await SeedCustomRoleAsync(db, tenantId, "RoleManagerB", Permissions.RolesManage);
            await GrantRoleToUserAsync(db, tenantId, roleA.Id, "holder-a");
            await GrantRoleToUserAsync(db, tenantId, roleB.Id, "holder-b");

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.UpdateRole(roleA.Id, new UpdateRoleDto { Permissions = [] });

            Assert.IsType<OkObjectResult>(result.Result);
            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == roleA.Id && rp.TenantId == tenantId)
                .ToListAsync();
            Assert.Empty(grants);
        }
    }

    [Fact]
    public async Task UpdateRole_StrippingRolesManage_IsRejected_WhenTheOnlyOtherHolderIsInactive()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var roleA = await SeedCustomRoleAsync(db, tenantId, "RoleManagerA2", Permissions.RolesManage);
            var roleB = await SeedCustomRoleAsync(db, tenantId, "RoleManagerB2", Permissions.RolesManage);
            await GrantRoleToUserAsync(db, tenantId, roleA.Id, "holder-a2");
            // The only OTHER role holding the grant is held solely by an inactive user - AuthController
            // blocks inactive users from logging in, so this role's coverage cannot actually be used.
            await GrantRoleToInactiveUserAsync(db, tenantId, roleB.Id, "holder-b2-inactive");

            var controller = BuildController(db, tenantId, BuildPrincipal(Permissions.RolesManage));

            var result = await controller.UpdateRole(roleA.Id, new UpdateRoleDto { Permissions = [] });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<RoleDto>>(bad.Value);
            Assert.False(body.Success);

            var grants = await db.RolePermissions
                .Where(rp => rp.RoleId == roleA.Id && rp.TenantId == tenantId)
                .Select(rp => rp.Permission).ToListAsync();
            Assert.Equal([Permissions.RolesManage], grants);
        }
    }
}
