using System.Security.Claims;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Covers PUT /api/auth/me, the self-service profile update. The endpoint takes no id - it
/// resolves the caller from their own token - so the tests that matter are the ones proving it
/// cannot reach another account and cannot change anything an administrator controls.
///
/// UserManager here is a real Identity store over the test AppDbContext, the same non-mock
/// pattern UsersControllerRoleAssignmentTests uses.
/// </summary>
public class ProfileSelfServiceTests
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

    /// UpdateCurrentUser touches only userManager and db. SignInManager, TokenService,
    /// IEmailService and IConfiguration are primary-constructor parameters that this action
    /// never reads, and C# captures them without dereferencing - so null! is safe here and
    /// avoids building a SignInManager's seven-dependency graph for a test that cannot use it.
    private static AuthController BuildController(
        AppDbContext db, UserManager<ApplicationUser> userManager, ClaimsPrincipal principal) =>
        new(userManager, null!, null!, db, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

    private static ClaimsPrincipal PrincipalFor(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static async Task<ApplicationUser> SeedUserAsync(
        AppDbContext db, Guid tenantId, string email, string fullName, string? department = null)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = fullName,
            Department = department,
            IsActive = true,
            TenantId = tenantId,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task UpdateCurrentUser_SavesNameAndDepartment()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var user = await SeedUserAsync(db, tenantId, "sam@acme.test", "Sam Old", "Support");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(user.Id));

        var result = await controller.UpdateCurrentUser(
            new UpdateProfileDto { FullName = "Sam New", Department = "Facilities" });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<UserDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("Sam New", body.Data!.FullName);
        Assert.Equal("Facilities", body.Data.Department);

        var saved = await db.Users.FindAsync(user.Id);
        Assert.Equal("Sam New", saved!.FullName);
        Assert.Equal("Facilities", saved.Department);
        Assert.NotNull(saved.UpdatedAt);
    }

    [Fact]
    public async Task UpdateCurrentUser_LeavesOtherUsersUntouched()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var caller = await SeedUserAsync(db, tenantId, "caller@acme.test", "Caller");
        var other = await SeedUserAsync(db, tenantId, "other@acme.test", "Other Person", "Legal");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(caller.Id));

        await controller.UpdateCurrentUser(
            new UpdateProfileDto { FullName = "Caller Renamed", Department = "Ops" });

        var untouched = await db.Users.FindAsync(other.Id);
        Assert.Equal("Other Person", untouched!.FullName);
        Assert.Equal("Legal", untouched.Department);
    }

    [Fact]
    public async Task UpdateCurrentUser_DoesNotChangeRoleOrActiveFlag()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var user = await SeedUserAsync(db, tenantId, "staff@acme.test", "Staff Person");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(user.Id));

        await controller.UpdateCurrentUser(new UpdateProfileDto { FullName = "Staff Renamed" });

        var saved = await db.Users.FindAsync(user.Id);
        Assert.True(saved!.IsActive);
        Assert.Empty(await userManager.GetRolesAsync(saved));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateCurrentUser_RejectsBlankName(string name)
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var user = await SeedUserAsync(db, tenantId, "blank@acme.test", "Original Name");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(user.Id));

        var result = await controller.UpdateCurrentUser(new UpdateProfileDto { FullName = name });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var saved = await db.Users.FindAsync(user.Id);
        Assert.Equal("Original Name", saved!.FullName);
    }

    [Fact]
    public async Task UpdateCurrentUser_RejectsOverlongName()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var user = await SeedUserAsync(db, tenantId, "long@acme.test", "Original Name");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(user.Id));

        var result = await controller.UpdateCurrentUser(
            new UpdateProfileDto { FullName = new string('a', 101) });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateCurrentUser_StoresBlankDepartmentAsNull()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var user = await SeedUserAsync(db, tenantId, "dept@acme.test", "Dept Person", "Support");

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(user.Id));

        await controller.UpdateCurrentUser(
            new UpdateProfileDto { FullName = "Dept Person", Department = "   " });

        var saved = await db.Users.FindAsync(user.Id);
        Assert.Null(saved!.Department);
    }

    [Fact]
    public async Task UpdateCurrentUser_ReturnsNotFoundForUnknownUser()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await TestDb.SeedTenantAsync(db, Guid.NewGuid());

        var userManager = CreateUserManager(db);
        var controller = BuildController(db, userManager, PrincipalFor(Guid.NewGuid().ToString()));

        var result = await controller.UpdateCurrentUser(new UpdateProfileDto { FullName = "Ghost" });

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
