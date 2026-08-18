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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Covers the Critical fix: UsersController.CreateUser/UpdateUser used to gate role assignment on
/// Roles.CanAssign, a name whitelist that only checked the target role's NAME, never what it
/// GRANTS. A user holding nothing but iams:users:manage could hand themselves (or anyone) the
/// Admin role - every permission there is - in one request. It also fixes the flip side: a
/// custom role created through POST /api/roles was never assignable to anyone, because the old
/// whitelist only knew the five built-in role names.
///
/// UserManager/RoleManager here are real Identity stores over the test AppDbContext (the same
/// non-mock pattern TokenServiceClaimsTests and RolesControllerGuardrailTests use), not mocks.
/// </summary>
public class UsersControllerRoleAssignmentTests
{
    private static UserManager<ApplicationUser> CreateUserManager(AppDbContext db)
    {
        var store = new UserStore<ApplicationUser, ApplicationRole, AppDbContext>(db);
        return new UserManager<ApplicationUser>(
            store,
            optionsAccessor: Options.Create(new IdentityOptions()),
            passwordHasher: new PasswordHasher<ApplicationUser>(),
            userValidators: [],
            passwordValidators: [],
            keyNormalizer: new UpperInvariantLookupNormalizer(),
            errors: new IdentityErrorDescriber(),
            services: null!,
            logger: NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    private static TokenService CreateTokenService(AppDbContext db, UserManager<ApplicationUser> userManager) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-that-is-long-enough-for-hmac-sha256",
                ["Jwt:Issuer"] = "AssetDesk.Tests",
                ["Jwt:Audience"] = "AssetDesk.Tests",
                ["Jwt:ExpireMinutes"] = "30",
            }).Build(),
            userManager,
            new PermissionResolver(db),
            db);

    /// Only what CreateUser needs (CanCreateUserAsync, UpdateUserCountAsync). Anything else
    /// throws loudly rather than masking a real behavioural change with a default value.
    private class StubSubscriptionService : ISubscriptionService
    {
        public Task<bool> CanCreateAssetAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> CanCreateUserAsync(Guid tenantId) => Task.FromResult(true);
        public Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes) => throw new NotSupportedException();
        public Task<bool> CanCreateTicketAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateAssetCountAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateUserCountAsync(Guid tenantId) => Task.CompletedTask;
        public Task UpdateStorageUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<TenantUsageDto> GetUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> IsSubscriptionActiveAsync(Guid tenantId) => throw new NotSupportedException();
    }

    private static UsersController BuildController(
        AppDbContext db, Guid tenantId, ClaimsPrincipal principal, UserManager<ApplicationUser> userManager,
        bool isSuperAdmin = false) =>
        new(
            userManager,
            new FakeTenantProvider(tenantId, isSuperAdmin),
            new StubSubscriptionService(),
            CreateTokenService(db, userManager),
            new PermissionResolver(db),
            db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

    private static async Task<ApplicationRole> SeedBuiltInRoleWithGrantsAsync(
        AppDbContext db, Guid tenantId, string name, IEnumerable<string> grants)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}-{Guid.NewGuid():N}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = true,
            TenantId = null
        };
        db.Roles.Add(role);
        db.RolePermissions.AddRange(grants.Select(p => new RolePermission
        {
            Id = Guid.NewGuid(), RoleId = role.Id, TenantId = tenantId, Permission = p
        }));
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task<ApplicationRole> SeedCustomRoleAsync(
        AppDbContext db, Guid tenantId, string name, IEnumerable<string> grants)
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

    // ---------------------------------------------------------------------------------------
    // The escalation: a holder of only iams:users:manage cannot hand themselves Admin.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateUser_RejectsSelfAssignmentOfAdmin_WhenActorHoldsOnlyUsersManage()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            // Admin holds every permission in this tenant, exactly as SeedData/
            // EnsureRolePermissionsAsync provisions it in production.
            await SeedBuiltInRoleWithGrantsAsync(db, tenantId, Roles.Admin, Permissions.Keys);

            var userManager = CreateUserManager(db);
            var actor = new ApplicationUser
            {
                Id = "actor-1", UserName = "actor@test.local", Email = "actor@test.local",
                FullName = "Actor", TenantId = tenantId
            };
            Assert.True((await userManager.CreateAsync(actor)).Succeeded);

            var principal = TestPrincipals.With(Permissions.UsersManage);
            var controller = BuildController(db, tenantId, principal, userManager);

            var result = await controller.UpdateUser("actor-1", new UpdateUserDto { Role = Roles.Admin });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<UserDto>>(bad.Value);
            Assert.False(body.Success);

            var roles = await userManager.GetRolesAsync(actor);
            Assert.DoesNotContain(Roles.Admin, roles);
        }
    }

    // ---------------------------------------------------------------------------------------
    // No regression: an actor holding every permission (an Admin) can still assign every
    // built-in role.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateUser_AdminActor_CanAssignEveryBuiltInRole()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            foreach (var r in Roles.All.Where(r => r != Roles.SuperAdmin))
                await SeedBuiltInRoleWithGrantsAsync(db, tenantId, r, Permissions.DefaultsFor(r));

            var userManager = CreateUserManager(db);
            // A real Admin's token carries a permission claim for all 22 keys - see
            // TokenService.GenerateTokenAsync and Permissions.DefaultsFor(Admin).
            var principal = TestPrincipals.With(Permissions.Keys);
            var controller = BuildController(db, tenantId, principal, userManager);

            foreach (var r in Roles.All.Where(r => r != Roles.SuperAdmin))
            {
                var result = await controller.CreateUser(new CreateUserDto
                {
                    Email = $"{r.ToLowerInvariant()}@test.local",
                    Password = "Sup3r$ecret!",
                    FullName = $"{r} User",
                    Role = r
                });

                var ok = Assert.IsType<OkObjectResult>(result.Result);
                var body = Assert.IsType<ApiResponse<UserDto>>(ok.Value);
                Assert.True(body.Success, $"Admin should be able to assign built-in role {r}: {body.Message}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // The other half of the same fix: a custom role, previously un-assignable to anyone, can
    // now be assigned by an actor who holds its grants.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateUser_CustomRole_CanBeAssignedByAnActorHoldingItsGrants()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var customRole = await SeedCustomRoleAsync(
                db, tenantId, "HelpdeskTier1", [Permissions.AssetsView, Permissions.TicketsFile]);

            var userManager = CreateUserManager(db);
            var principal = TestPrincipals.With(
                Permissions.UsersManage, Permissions.AssetsView, Permissions.TicketsFile);
            var controller = BuildController(db, tenantId, principal, userManager);

            var result = await controller.CreateUser(new CreateUserDto
            {
                Email = "helpdesk@test.local",
                Password = "Sup3r$ecret!",
                FullName = "Helpdesk Person",
                Role = customRole.Name!
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<UserDto>>(ok.Value);
            Assert.True(body.Success);

            var created = await userManager.FindByEmailAsync("helpdesk@test.local");
            var roles = await userManager.GetRolesAsync(created!);
            Assert.Contains("HelpdeskTier1", roles);
        }
    }

    // ---------------------------------------------------------------------------------------
    // CRITICAL fix: an emptied built-in role is no longer trivially assignable. Step 2 of the
    // exploit chain - PUT /api/roles/{admin-id} { "permissions": [] } then assign that
    // now-zero-grant Admin role to yourself - relied on an empty grant set being treated as
    // "nothing to worry about".
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateUser_RejectsAssigningABuiltInRoleWhoseGrantsWereEmptied()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            // Admin exists as a built-in role but holds zero grants in this tenant - as if
            // PUT /api/roles/{admin-id} { "permissions": [] } had just run.
            await SeedBuiltInRoleWithGrantsAsync(db, tenantId, Roles.Admin, []);

            var userManager = CreateUserManager(db);
            var actor = new ApplicationUser
            {
                Id = "actor-2", UserName = "actor2@test.local", Email = "actor2@test.local",
                FullName = "Actor Two", TenantId = tenantId
            };
            Assert.True((await userManager.CreateAsync(actor)).Succeeded);

            // Enough to have reached this point in the real exploit (users:manage + roles:manage),
            // but Admin currently grants nothing, so the subset check has nothing to compare against
            // - which is exactly why the guard needs its own check for a zero-grant built-in role.
            var principal = TestPrincipals.With(Permissions.UsersManage, Permissions.RolesManage);
            var controller = BuildController(db, tenantId, principal, userManager);

            var result = await controller.UpdateUser("actor-2", new UpdateUserDto { Role = Roles.Admin });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<UserDto>>(bad.Value);
            Assert.False(body.Success);

            var roles = await userManager.GetRolesAsync(actor);
            Assert.DoesNotContain(Roles.Admin, roles);
        }
    }

    // ---------------------------------------------------------------------------------------
    // IMPORTANT: the iams:roles:manage lockout guard was only enforced in RolesController.
    // UsersController.UpdateUser (move the last holder to another role) and DeleteUser
    // (deactivate the last holder) could each break the same invariant with no check at all.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateUser_MovingTheLastRolesManageHolderToAnotherRole_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var managerRole = await SeedCustomRoleAsync(db, tenantId, "RoleManager", [Permissions.RolesManage]);
            await SeedCustomRoleAsync(db, tenantId, "Basic", [Permissions.AssetsView]);

            var userManager = CreateUserManager(db);
            var target = new ApplicationUser
            {
                Id = "manager-1", UserName = "manager1@test.local", Email = "manager1@test.local",
                FullName = "Role Manager", TenantId = tenantId
            };
            Assert.True((await userManager.CreateAsync(target)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(target, managerRole.Name!)).Succeeded);

            var principal = TestPrincipals.With(
                Permissions.UsersManage, Permissions.RolesManage, Permissions.AssetsView);
            var controller = BuildController(db, tenantId, principal, userManager);

            var result = await controller.UpdateUser("manager-1", new UpdateUserDto { Role = "Basic" });

            var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<UserDto>>(bad.Value);
            Assert.False(body.Success);

            var roles = await userManager.GetRolesAsync(target);
            Assert.Contains("RoleManager", roles);
            Assert.DoesNotContain("Basic", roles);
        }
    }

    [Fact]
    public async Task DeleteUser_DeactivatingTheLastRolesManageHolder_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var managerRole = await SeedCustomRoleAsync(db, tenantId, "RoleManager2", [Permissions.RolesManage]);

            var userManager = CreateUserManager(db);
            var target = new ApplicationUser
            {
                Id = "manager-2", UserName = "manager2@test.local", Email = "manager2@test.local",
                FullName = "Role Manager Two", TenantId = tenantId
            };
            Assert.True((await userManager.CreateAsync(target)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(target, managerRole.Name!)).Succeeded);

            var principal = TestPrincipals.With(Permissions.UsersManage, Permissions.RolesManage);
            var controller = BuildController(db, tenantId, principal, userManager);

            var result = await controller.DeleteUser("manager-2");

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            var body = Assert.IsType<ApiResponse<object>>(bad.Value);
            Assert.False(body.Success);

            var reloaded = await userManager.FindByIdAsync("manager-2");
            Assert.True(reloaded!.IsActive);
        }
    }
}
