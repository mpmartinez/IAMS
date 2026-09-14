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
/// Covers POST /api/users/{id}/send-password-reset: an administrator mails a reset link and the
/// target's current password stops working until they use it.
///
/// The authorisation boundary here is the same one UpdateUser enforces, and the tests below pin
/// each half of it: tenant isolation, and the rule that only a SuperAdmin may reset a
/// SuperAdmin. The ordering test matters just as much - the endpoint must not lock an account
/// it could not deliver a link for, or it would create the exact lockout it exists to resolve.
///
/// UserManager here is a real Identity store over the test AppDbContext, matching
/// UsersControllerRoleAssignmentTests rather than mocking it.
/// </summary>
public class UsersControllerPasswordResetTests
{
    private static UserManager<ApplicationUser> CreateUserManager(AppDbContext db)
    {
        var store = new UserStore<ApplicationUser, ApplicationRole, AppDbContext>(db);
        var manager = new UserManager<ApplicationUser>(
            store,
            optionsAccessor: Options.Create(new IdentityOptions()),
            passwordHasher: new PasswordHasher<ApplicationUser>(),
            userValidators: [],
            passwordValidators: [],
            keyNormalizer: new UpperInvariantLookupNormalizer(),
            errors: new IdentityErrorDescriber(),
            services: null!,
            logger: NullLogger<UserManager<ApplicationUser>>.Instance);

        manager.RegisterTokenProvider(TokenOptions.DefaultProvider, new StubTokenProvider());
        return manager;
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

    private class StubSubscriptionService : ISubscriptionService
    {
        public Task<bool> CanCreateAssetAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<string?> ReserveAssetCapacityAsync(Data.AppDbContext db, Guid tenantId, int count, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
        AppDbContext db,
        Guid tenantId,
        UserManager<ApplicationUser> userManager,
        FakeEmailService email,
        bool isSuperAdmin = false,
        string actingUserId = "admin-1") =>
        new(
            userManager,
            new FakeTenantProvider(tenantId, isSuperAdmin),
            new StubSubscriptionService(),
            CreateTokenService(db, userManager),
            new PermissionResolver(db),
            email,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:WebUrl"] = "https://assetdesk.test",
            }).Build(),
            db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, actingUserId)], "test"))
                }
            }
        };

    private static async Task SeedAssignableRoleAsync(AppDbContext db, Guid tenantId, string name)
    {
        db.Roles.Add(new ApplicationRole
        {
            Id = $"role-{name}-{tenantId:N}",
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ApplicationUser> SeedTargetAsync(
        AppDbContext db, UserManager<ApplicationUser> userManager, Guid tenantId, string id)
    {
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{id}@test.local",
            Email = $"{id}@test.local",
            NormalizedUserName = $"{id}@TEST.LOCAL",
            NormalizedEmail = $"{id}@TEST.LOCAL",
            FullName = "Target User",
            TenantId = tenantId,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Creating a user mails an invite and leaves the account locked until it is used. An
    /// administrator never chooses the password - CreateUserDto has no field for one.
    /// </summary>
    [Fact]
    public async Task CreateUser_MailsAnInvite_AndLeavesTheAccountLocked()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        await SeedAssignableRoleAsync(db, tenantId, Roles.Staff);
        var email = new FakeEmailService();
        var controller = BuildController(db, tenantId, userManager, email, isSuperAdmin: true);

        var result = await controller.CreateUser(new CreateUserDto
        {
            Email = "newcomer@test.local",
            FullName = "New Comer",
            Role = Roles.Staff
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<UserDto>>(ok.Value);
        Assert.Contains("sent to newcomer@test.local", payload.Message);

        var sent = Assert.Single(email.PasswordResets);
        Assert.Equal("newcomer@test.local", sent.To);
        Assert.Contains("/reset-password?email=", sent.ResetUrl);

        var created = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "newcomer@test.local");
        Assert.True(created.MustChangePassword);
        Assert.True(created.IsActive);
    }

    /// A failed invite must not discard the account - the row carries a resend button, and
    /// making the administrator retype everything over an SMTP blip would be worse.
    [Fact]
    public async Task CreateUser_WhenInviteMailFails_StillCreatesTheUser_AndSaysSo()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        await SeedAssignableRoleAsync(db, tenantId, Roles.Staff);
        var email = new FakeEmailService { ShouldSucceed = false };
        var controller = BuildController(db, tenantId, userManager, email, isSuperAdmin: true);

        var result = await controller.CreateUser(new CreateUserDto
        {
            Email = "unreachable@test.local",
            FullName = "Un Reachable",
            Role = Roles.Staff
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ApiResponse<UserDto>>(ok.Value);
        Assert.Contains("could not be sent", payload.Message);

        var created = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "unreachable@test.local");
        Assert.True(created.MustChangePassword);
    }

    /// The generated password is never shown to anyone, but Identity still validates it on
    /// creation - so it has to satisfy the configured requirements every time, not usually.
    [Fact]
    public void GeneratedPassword_AlwaysSatisfiesTheConfiguredRequirements()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Generate();

            Assert.Equal(24, password.Length);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public void GeneratedPasswords_AreNotRepeated()
    {
        var generated = Enumerable.Range(0, 100).Select(_ => PasswordGenerator.Generate()).ToList();
        Assert.Equal(generated.Count, generated.Distinct().Count());
    }

    [Fact]
    public async Task SendPasswordReset_MailsLink_AndLocksThePassword()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, tenantId, "target-1");
        var email = new FakeEmailService();
        var controller = BuildController(db, tenantId, userManager, email);

        var result = await controller.SendPasswordReset(target.Id);

        Assert.IsType<OkObjectResult>(result.Result);

        var sent = Assert.Single(email.PasswordResets);
        Assert.Equal(target.Email, sent.To);
        Assert.StartsWith("https://assetdesk.test/reset-password?email=", sent.ResetUrl);
        Assert.Contains("token=", sent.ResetUrl);

        var reloaded = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.True(reloaded.MustChangePassword);
    }

    [Fact]
    public async Task SendPasswordReset_WritesAnAuditRow_NamingTheActingAdmin()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, tenantId, "target-1");
        var controller = BuildController(db, tenantId, userManager, new FakeEmailService(), actingUserId: "admin-7");

        await controller.SendPasswordReset(target.Id);

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.PasswordResetSent);
        Assert.Equal(nameof(ApplicationUser), audit.EntityType);
        Assert.Equal(target.Id, audit.EntityId);
        Assert.Equal("admin-7", audit.UserId);
        Assert.Equal(tenantId, audit.TenantId);
    }

    /// The ordering guarantee. A failed send must leave the account exactly as it was - locking
    /// it here would strand the user with a dead password and no link to recover with.
    [Fact]
    public async Task SendPasswordReset_WhenMailFails_ReportsFailure_AndLeavesPasswordUsable()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, tenantId, "target-1");
        var email = new FakeEmailService { ShouldSucceed = false };
        var controller = BuildController(db, tenantId, userManager, email);

        var result = await controller.SendPasswordReset(target.Id);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);

        var reloaded = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.False(reloaded.MustChangePassword);
        Assert.Empty(await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.PasswordResetSent).ToListAsync());
    }

    [Fact]
    public async Task SendPasswordReset_ForUserInAnotherTenant_Returns404_AndSendsNothing()
    {
        var actorTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(actorTenant, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, actorTenant);
        await TestDb.SeedTenantAsync(db, otherTenant);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, otherTenant, "target-elsewhere");
        var email = new FakeEmailService();

        // Actor is a tenant admin, not a platform SuperAdmin.
        var controller = BuildController(db, actorTenant, userManager, email, isSuperAdmin: false);

        var result = await controller.SendPasswordReset(target.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(email.PasswordResets);

        var reloaded = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.False(reloaded.MustChangePassword);
    }

    [Fact]
    public async Task SendPasswordReset_ForSuperAdmin_ByNonSuperAdmin_IsForbidden()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, tenantId, "target-super");

        db.Roles.Add(new ApplicationRole
        {
            Id = "role-super",
            Name = Roles.SuperAdmin,
            NormalizedName = Roles.SuperAdmin.ToUpperInvariant(),
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = target.Id, RoleId = "role-super" });
        await db.SaveChangesAsync();

        var email = new FakeEmailService();
        var controller = BuildController(db, tenantId, userManager, email, isSuperAdmin: false);

        var result = await controller.SendPasswordReset(target.Id);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(email.PasswordResets);

        var reloaded = await db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        Assert.False(reloaded.MustChangePassword);
    }

    [Fact]
    public async Task SendPasswordReset_ForDeactivatedUser_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var target = await SeedTargetAsync(db, userManager, tenantId, "target-inactive");
        target.IsActive = false;
        await db.SaveChangesAsync();

        var email = new FakeEmailService();
        var controller = BuildController(db, tenantId, userManager, email);

        var result = await controller.SendPasswordReset(target.Id);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(email.PasswordResets);
    }

    [Fact]
    public async Task SendPasswordReset_ForUnknownUser_Returns404()
    {
        var tenantId = Guid.NewGuid();
        var (db, connection) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: true));
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, tenantId);

        var userManager = CreateUserManager(db);
        var email = new FakeEmailService();
        var controller = BuildController(db, tenantId, userManager, email);

        var result = await controller.SendPasswordReset("no-such-user");

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(email.PasswordResets);
    }
}
