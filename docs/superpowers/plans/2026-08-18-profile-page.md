# Profile Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every signed-in user a `/profile` page that shows their account and lets them edit their own full name and department.

**Architecture:** A new `PUT /api/auth/me` endpoint on the existing `AuthController` resolves the caller from their token, so it can only ever write to the caller's own record. A new Blazor page at `/profile` reads `GET /api/auth/me` on load, saves through the new endpoint, and pushes the updated `UserDto` into local storage so `AuthStateProvider` re-renders the sidebar name without a reload. The sidebar user card becomes the link to the page, and the change-password key icon moves onto it.

**Tech Stack:** .NET 10, ASP.NET Core Web API, ASP.NET Core Identity, Blazor WebAssembly, Tailwind CSS, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-18-profile-page-design.md`

## Global Constraints

- All API endpoints return the `ApiResponse<T>` wrapper (`ApiResponse<T>.Ok(data, message)` / `ApiResponse<T>.Fail(message)`).
- Self-scoped endpoints resolve the user from `User.FindFirstValue(ClaimTypes.NameIdentifier)` — never from a route or body parameter.
- `FullName`: required, trimmed, 1–100 characters. `Department`: optional, trimmed, ≤100 characters, empty/whitespace stored as `null`.
- Blazor components support dark mode via Tailwind `dark:` prefix classes. Every colour class needs its `dark:` counterpart.
- The test project has no mocking library. Tests construct controllers directly, passing `null!` for dependencies the tested action never touches.
- There is no Blazor component test project. Tasks 2–5 are verified by `dotnet build` plus the manual check in Task 6.

## File Structure

| File | Responsibility |
|---|---|
| `src/AssetDesk.Shared/DTOs/AuthDto.cs` (modify) | Add `UpdateProfileDto` — the two-field self-service contract |
| `src/AssetDesk.Api/Controllers/AuthController.cs` (modify) | Add `PUT me` action |
| `tests/AssetDesk.Api.Tests/ProfileSelfServiceTests.cs` (create) | Cover the endpoint's happy path, validation, and the escalation boundary |
| `src/AssetDesk.Web/Services/ApiClient.cs` (modify) | `GetMyProfileAsync`, `UpdateProfileAsync`, `LogoutAllAsync` |
| `src/AssetDesk.Web/Services/AuthService.cs` (modify) | `UpdateCachedUserAsync` — refresh stored user + notify auth state |
| `src/AssetDesk.Web/Components/UI/RoleBadge.razor` (create) | One place that maps a role name to its badge colours |
| `src/AssetDesk.Web/Pages/Profile.razor` (create) | The page: identity, personal details, account, security |
| `src/AssetDesk.Web/Layout/MainLayout.razor` (modify) | User card links to `/profile`; key icon and its dialog removed |

---

### Task 1: `PUT /api/auth/me` endpoint

**Files:**
- Modify: `src/AssetDesk.Shared/DTOs/AuthDto.cs` (append at end)
- Modify: `src/AssetDesk.Api/Controllers/AuthController.cs` (insert after `GetCurrentUser`, which ends at line 79)
- Test: `tests/AssetDesk.Api.Tests/ProfileSelfServiceTests.cs` (create)

**Interfaces:**
- Consumes: `ApplicationUser` (has `FullName`, `Department`, `IsActive`, `UpdatedAt`), `UserDto`, `AuthController.MapToDto(ApplicationUser, string role, string? tenantName)` (private static, line 247).
- Produces: `UpdateProfileDto { string FullName; string? Department }` and `PUT api/auth/me` returning `ApiResponse<UserDto>`. Task 2 calls both.

- [ ] **Step 1: Add the DTO**

Append to `src/AssetDesk.Shared/DTOs/AuthDto.cs`:

```csharp
// Deliberately narrower than UpdateUserDto: Role, Email, IsActive and TenantId are absent from
// this contract, so a crafted payload has nothing to bind to and a user cannot escalate
// themselves through the self-service endpoint. Do not widen it to share UpdateUserDto.
public record UpdateProfileDto
{
    public required string FullName { get; init; }
    public string? Department { get; init; }
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/ProfileSelfServiceTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/AssetDesk.Api.Tests --filter FullyQualifiedName~ProfileSelfServiceTests
```

Expected: compile error — `'AuthController' does not contain a definition for 'UpdateCurrentUser'`.

- [ ] **Step 4: Implement the endpoint**

In `src/AssetDesk.Api/Controllers/AuthController.cs`, insert directly after `GetCurrentUser` (after line 79, before the `ChangePassword` attributes):

```csharp
    /// <summary>
    /// Update the signed-in user's own profile. Takes no id - the account is resolved from the
    /// caller's token, so this can never be aimed at someone else's record. UpdateProfileDto
    /// carries only FullName and Department; role, email and active status stay administrator-only.
    /// </summary>
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpPut("me")]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateCurrentUser(UpdateProfileDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);

        if (user is null)
            return NotFound();

        var fullName = dto.FullName?.Trim();
        if (string.IsNullOrEmpty(fullName))
            return BadRequest(ApiResponse<UserDto>.Fail("Full name is required"));

        if (fullName.Length > 100)
            return BadRequest(ApiResponse<UserDto>.Fail("Full name must be 100 characters or fewer"));

        var department = dto.Department?.Trim();
        if (department?.Length > 100)
            return BadRequest(ApiResponse<UserDto>.Fail("Department must be 100 characters or fewer"));

        user.FullName = fullName;
        user.Department = string.IsNullOrEmpty(department) ? null : department;
        user.UpdatedAt = DateTime.UtcNow;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<UserDto>.Fail(errors));
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId);

        var roles = await userManager.GetRolesAsync(user);
        return Ok(ApiResponse<UserDto>.Ok(
            MapToDto(user, roles.FirstOrDefault() ?? "Staff", tenant?.Name),
            "Profile updated"));
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/AssetDesk.Api.Tests --filter FullyQualifiedName~ProfileSelfServiceTests
```

Expected: PASS, 8 tests (the `[Theory]` contributes 2).

- [ ] **Step 6: Run the full test suite for regressions**

```bash
dotnet test
```

Expected: PASS, no failures.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Shared/DTOs/AuthDto.cs src/AssetDesk.Api/Controllers/AuthController.cs tests/AssetDesk.Api.Tests/ProfileSelfServiceTests.cs
git commit -m "feat(api): let a user update their own name and department"
```

---

### Task 2: Client plumbing

**Files:**
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs` (append the three methods after `GetUserListAsync`, which ends around line 220)
- Modify: `src/AssetDesk.Web/Services/AuthService.cs` (add method after `ChangePasswordAsync`, which ends at line 193)

**Interfaces:**
- Consumes: `UpdateProfileDto` and `PUT api/auth/me` from Task 1; existing `POST api/auth/logout-all`; `ApiClient.GetAuthenticatedClient()` and `SafeGetAsync<T>` (private, already in the file).
- Produces, all called by Task 4:
  - `ApiClient.GetMyProfileAsync() → Task<UserDto?>`
  - `ApiClient.UpdateProfileAsync(UpdateProfileDto) → Task<(bool Success, UserDto? User, string? Error)>`
  - `ApiClient.LogoutAllAsync() → Task<(bool Success, string? Error)>`
  - `AuthService.UpdateCachedUserAsync(UserDto) → Task`

- [ ] **Step 1: Add the ApiClient methods**

In `src/AssetDesk.Web/Services/ApiClient.cs`, add after `GetUserListAsync`:

```csharp
    // Reads the profile from the server rather than the copy AuthService cached at login, so a
    // role or department an administrator changed since then shows correctly on /profile.
    public async Task<UserDto?> GetMyProfileAsync()
    {
        var response = await SafeGetAsync<ApiResponse<UserDto>>("api/auth/me");
        return response?.Data;
    }

    public async Task<(bool Success, UserDto? User, string? Error)> UpdateProfileAsync(UpdateProfileDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync("api/auth/me", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, null, error?.Message ?? "Failed to update profile");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();
        return (true, result?.Data, null);
    }

    public async Task<(bool Success, string? Error)> LogoutAllAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/auth/logout-all", new { });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to sign out other sessions");
        }

        return (true, null);
    }
```

- [ ] **Step 2: Add the AuthService method**

In `src/AssetDesk.Web/Services/AuthService.cs`, add after `ChangePasswordAsync`:

```csharp
    /// <summary>
    /// Overwrite the cached user after a profile edit. AuthStateProvider builds its
    /// ClaimTypes.Name claim from this stored UserDto, so notifying afterwards is what makes the
    /// sidebar pick up a new name immediately - without it the old name persists until the next
    /// token refresh or page reload. The token itself is untouched: nothing editable here is
    /// something the API authorises against.
    /// </summary>
    public async Task UpdateCachedUserAsync(UserDto user)
    {
        await localStorage.SetItemAsync(UserKey, user);
        ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();
    }
```

- [ ] **Step 3: Build to verify it compiles**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/AssetDesk.Web/Services/ApiClient.cs src/AssetDesk.Web/Services/AuthService.cs
git commit -m "feat(web): add profile read, update and sign-out-everywhere client calls"
```

---

### Task 3: `RoleBadge` component

**Files:**
- Create: `src/AssetDesk.Web/Components/UI/RoleBadge.razor`
- Modify: `src/AssetDesk.Web/Layout/MainLayout.razor:103-114` (the `<p class="text-xs text-slate-500 truncate">` block holding the inline role map)

**Interfaces:**
- Produces: `<RoleBadge Role="@someString" />`, and optional `Size` (`"sm"` default, `"md"`). Task 4 uses `Size="md"`.

- [ ] **Step 1: Create the component**

Create `src/AssetDesk.Web/Components/UI/RoleBadge.razor`:

```razor
@* The single place a role name becomes a colour. MainLayout's sidebar and the profile page both
   render a role badge; keeping the map here stops the two from drifting when a role is added. *@

<span class="@BadgeClasses">@Role</span>

@code {
    [Parameter, EditorRequired] public string Role { get; set; } = "";

    /// "sm" for the sidebar's tight footer, "md" for page headers.
    [Parameter] public string Size { get; set; } = "sm";

    private string BadgeClasses =>
        $"inline-flex items-center font-medium rounded {SizeClasses} {ColorClasses}";

    private string SizeClasses => Size == "md"
        ? "px-2.5 py-1 text-xs"
        : "px-1.5 py-0.5 text-[10px]";

    private string ColorClasses => Role switch
    {
        "SuperAdmin" => "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400",
        "Admin" => "bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-400",
        "Auditor" => "bg-teal-100 text-teal-700 dark:bg-teal-900/30 dark:text-teal-400",
        _ => "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400"
    };
}
```

- [ ] **Step 2: Use it in MainLayout**

In `src/AssetDesk.Web/Layout/MainLayout.razor`, replace lines 103–114 — the whole `<p class="text-xs text-slate-500 truncate">` element including its `@{ ... }` block and the `<span>` — with:

```razor
                                <p class="text-xs text-slate-500 truncate">
                                    <RoleBadge Role="@(userContext.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Staff")" />
                                </p>
```

`_Imports.razor:19` already carries `@using AssetDesk.Web.Components.UI`, so no using directive is needed.

- [ ] **Step 3: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/AssetDesk.Web/Components/UI/RoleBadge.razor src/AssetDesk.Web/Layout/MainLayout.razor
git commit -m "refactor(web): extract the role badge into one component"
```

---

### Task 4: The profile page

**Files:**
- Create: `src/AssetDesk.Web/Pages/Profile.razor`

**Interfaces:**
- Consumes: `ApiClient.GetMyProfileAsync`, `ApiClient.UpdateProfileAsync`, `ApiClient.LogoutAllAsync`, `AuthService.UpdateCachedUserAsync` (Task 2); `<RoleBadge Role Size>` (Task 3); the existing `<ChangePasswordDialog @bind-IsOpen>` and `SnackbarService.Success/Error`.
- Produces: the route `/profile`, which Task 5 links to.

- [ ] **Step 1: Create the page**

Create `src/AssetDesk.Web/Pages/Profile.razor`:

```razor
@page "/profile"
@using AssetDesk.Web.Components
@using AssetDesk.Web.Components.UI
@attribute [Authorize]
@inject ApiClient Api
@inject AuthService Auth
@inject SnackbarService Snackbar

<PageTitle>My Profile - AssetDesk</PageTitle>

<div class="max-w-3xl space-y-6">
    <div>
        <h1 class="text-2xl font-bold text-slate-900 dark:text-white">My Profile</h1>
        <p class="text-sm text-slate-500 dark:text-slate-400">Your account details and security settings</p>
    </div>

    @if (_loading)
    {
        @for (int i = 0; i < 3; i++)
        {
            <div class="bg-white dark:bg-slate-800 rounded-2xl p-6 border border-slate-200 dark:border-slate-700">
                <div class="flex items-center gap-4">
                    <div class="w-14 h-14 rounded-full skeleton"></div>
                    <div class="flex-1 space-y-2">
                        <div class="h-4 w-1/3 rounded skeleton"></div>
                        <div class="h-3 w-1/2 rounded skeleton"></div>
                    </div>
                </div>
            </div>
        }
    }
    else if (_user is null)
    {
        <div class="bg-white dark:bg-slate-800 rounded-2xl p-12 border border-slate-200 dark:border-slate-700 text-center">
            <svg class="w-12 h-12 mx-auto text-slate-300 dark:text-slate-600 mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p class="text-slate-500 dark:text-slate-400 mb-4">We couldn't load your profile.</p>
            <button @onclick="LoadProfile" class="px-4 py-2.5 rounded-xl bg-primary-600 text-white font-medium hover:bg-primary-700 transition-colors">
                Try again
            </button>
        </div>
    }
    else
    {
        <!-- Identity -->
        <div class="bg-white dark:bg-slate-800 rounded-2xl p-6 border border-slate-200 dark:border-slate-700">
            <div class="flex flex-col sm:flex-row sm:items-center gap-4">
                <div class="w-16 h-16 rounded-full bg-gradient-to-br from-primary-500 to-primary-700 flex items-center justify-center text-white text-xl font-semibold shrink-0">
                    @Initials
                </div>
                <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-2 flex-wrap">
                        <h2 class="text-xl font-semibold text-slate-900 dark:text-white truncate">@_user.FullName</h2>
                        <RoleBadge Role="@_user.Role" Size="md" />
                    </div>
                    <p class="text-sm text-slate-500 dark:text-slate-400 truncate">@_user.Email</p>
                    @if (!string.IsNullOrEmpty(_user.TenantName))
                    {
                        <p class="text-sm text-slate-400 dark:text-slate-500 truncate">@_user.TenantName</p>
                    }
                </div>
            </div>
        </div>

        <!-- Personal details -->
        <div class="bg-white dark:bg-slate-800 rounded-2xl p-6 border border-slate-200 dark:border-slate-700">
            <div class="flex items-center justify-between mb-4">
                <h3 class="font-semibold text-slate-900 dark:text-white">Personal details</h3>
                @if (!_editing)
                {
                    <button @onclick="StartEditing" class="inline-flex items-center gap-2 px-3 py-1.5 text-sm font-medium text-slate-600 dark:text-slate-300 border border-slate-200 dark:border-slate-600 rounded-xl hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors">
                        <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                        </svg>
                        Edit
                    </button>
                }
            </div>

            @if (_editing)
            {
                @if (!string.IsNullOrEmpty(_saveError))
                {
                    <div class="mb-4 p-3 rounded-lg bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800">
                        <p class="text-sm text-red-700 dark:text-red-400">@_saveError</p>
                    </div>
                }

                <div class="space-y-4">
                    <div>
                        <label for="profile-name" class="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Full name</label>
                        <input id="profile-name" type="text" @bind="_editName" @bind:event="oninput" disabled="@_saving" maxlength="100"
                               class="w-full px-4 py-2.5 rounded-xl border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-700 text-slate-900 dark:text-white focus:ring-2 focus:ring-primary-500 focus:border-primary-500 disabled:opacity-50 transition-colors"
                               placeholder="Your full name" />
                    </div>
                    <div>
                        <label for="profile-department" class="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Department</label>
                        <input id="profile-department" type="text" @bind="_editDepartment" @bind:event="oninput" disabled="@_saving" maxlength="100"
                               class="w-full px-4 py-2.5 rounded-xl border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-700 text-slate-900 dark:text-white focus:ring-2 focus:ring-primary-500 focus:border-primary-500 disabled:opacity-50 transition-colors"
                               placeholder="e.g. Finance" />
                        <p class="text-xs text-slate-500 mt-1">Leave blank if you'd rather not say.</p>
                    </div>
                    <div class="flex justify-end gap-3 pt-2">
                        <button type="button" @onclick="CancelEditing" disabled="@_saving"
                                class="px-4 py-2.5 rounded-xl text-slate-700 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-700 font-medium disabled:opacity-50 transition-colors">
                            Cancel
                        </button>
                        <button type="button" @onclick="SaveProfile" disabled="@_saving"
                                class="px-4 py-2.5 rounded-xl bg-primary-600 text-white font-medium hover:bg-primary-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center gap-2">
                            @if (_saving)
                            {
                                <svg class="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                                    <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
                                    <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                                </svg>
                            }
                            Save changes
                        </button>
                    </div>
                </div>
            }
            else
            {
                <dl class="divide-y divide-slate-100 dark:divide-slate-700">
                    <div class="flex justify-between gap-4 py-3">
                        <dt class="text-sm text-slate-500 dark:text-slate-400">Full name</dt>
                        <dd class="text-sm font-medium text-slate-900 dark:text-white text-right">@_user.FullName</dd>
                    </div>
                    <div class="flex justify-between gap-4 py-3">
                        <dt class="text-sm text-slate-500 dark:text-slate-400">Department</dt>
                        <dd class="text-sm font-medium text-slate-900 dark:text-white text-right">
                            @(string.IsNullOrEmpty(_user.Department) ? "Not set" : _user.Department)
                        </dd>
                    </div>
                </dl>
            }
        </div>

        <!-- Account -->
        <div class="bg-white dark:bg-slate-800 rounded-2xl p-6 border border-slate-200 dark:border-slate-700">
            <h3 class="font-semibold text-slate-900 dark:text-white mb-1">Account</h3>
            <p class="text-sm text-slate-500 dark:text-slate-400 mb-4">An administrator manages these. Contact your IT team if something looks wrong.</p>
            <dl class="divide-y divide-slate-100 dark:divide-slate-700">
                <div class="flex justify-between gap-4 py-3">
                    <dt class="text-sm text-slate-500 dark:text-slate-400">Email</dt>
                    <dd class="text-sm font-medium text-slate-900 dark:text-white text-right break-all">@_user.Email</dd>
                </div>
                <div class="flex justify-between gap-4 py-3">
                    <dt class="text-sm text-slate-500 dark:text-slate-400">Role</dt>
                    <dd class="text-right"><RoleBadge Role="@_user.Role" /></dd>
                </div>
                <div class="flex justify-between gap-4 py-3">
                    <dt class="text-sm text-slate-500 dark:text-slate-400">Member since</dt>
                    <dd class="text-sm font-medium text-slate-900 dark:text-white text-right">@_user.CreatedAt.ToLocalTime().ToString("d MMMM yyyy")</dd>
                </div>
                <div class="flex justify-between gap-4 py-3">
                    <dt class="text-sm text-slate-500 dark:text-slate-400">Status</dt>
                    <dd class="text-right">
                        <span class="@(_user.IsActive
                            ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                            : "bg-slate-100 text-slate-700 dark:bg-slate-700 dark:text-slate-400") inline-flex px-2.5 py-1 rounded text-xs font-medium">
                            @(_user.IsActive ? "Active" : "Inactive")
                        </span>
                    </dd>
                </div>
            </dl>
        </div>

        <!-- Security -->
        <div class="bg-white dark:bg-slate-800 rounded-2xl p-6 border border-slate-200 dark:border-slate-700">
            <h3 class="font-semibold text-slate-900 dark:text-white mb-4">Security</h3>
            <div class="divide-y divide-slate-100 dark:divide-slate-700">
                <div class="flex flex-wrap items-center justify-between gap-3 pb-4">
                    <div>
                        <p class="text-sm font-medium text-slate-900 dark:text-white">Password</p>
                        <p class="text-sm text-slate-500 dark:text-slate-400">Change the password you use to sign in.</p>
                    </div>
                    <button @onclick="() => _showChangePassword = true"
                            class="px-4 py-2 text-sm font-medium text-slate-600 dark:text-slate-300 border border-slate-200 dark:border-slate-600 rounded-xl hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors">
                        Change password
                    </button>
                </div>
                <div class="flex flex-wrap items-center justify-between gap-3 pt-4">
                    <div>
                        <p class="text-sm font-medium text-slate-900 dark:text-white">Other sessions</p>
                        <p class="text-sm text-slate-500 dark:text-slate-400">Sign out everywhere else you're signed in. This device stays signed in.</p>
                    </div>
                    @if (_confirmingLogoutAll)
                    {
                        <div class="flex items-center gap-2">
                            <button @onclick="() => _confirmingLogoutAll = false" disabled="@_loggingOutAll"
                                    class="px-4 py-2 text-sm font-medium text-slate-600 dark:text-slate-300 rounded-xl hover:bg-slate-100 dark:hover:bg-slate-700 disabled:opacity-50 transition-colors">
                                Cancel
                            </button>
                            <button @onclick="SignOutEverywhere" disabled="@_loggingOutAll"
                                    class="px-4 py-2 text-sm font-medium text-white bg-red-600 rounded-xl hover:bg-red-700 disabled:opacity-50 transition-colors">
                                @(_loggingOutAll ? "Signing out..." : "Confirm")
                            </button>
                        </div>
                    }
                    else
                    {
                        <button @onclick="() => _confirmingLogoutAll = true"
                                class="px-4 py-2 text-sm font-medium text-red-600 dark:text-red-400 border border-red-200 dark:border-red-800 rounded-xl hover:bg-red-50 dark:hover:bg-red-900/20 transition-colors">
                            Sign out everywhere
                        </button>
                    }
                </div>
            </div>
        </div>
    }
</div>

<ChangePasswordDialog @bind-IsOpen="_showChangePassword" />

@code {
    private UserDto? _user;
    private bool _loading = true;
    private bool _editing;
    private bool _saving;
    private string? _saveError;
    private string _editName = "";
    private string _editDepartment = "";
    private bool _showChangePassword;
    private bool _confirmingLogoutAll;
    private bool _loggingOutAll;

    private string Initials
    {
        get
        {
            var parts = (_user?.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..1].ToUpperInvariant();
            return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
        }
    }

    protected override async Task OnInitializedAsync() => await LoadProfile();

    private async Task LoadProfile()
    {
        _loading = true;
        _user = await Api.GetMyProfileAsync();
        _loading = false;
    }

    private void StartEditing()
    {
        _editName = _user?.FullName ?? "";
        _editDepartment = _user?.Department ?? "";
        _saveError = null;
        _editing = true;
    }

    private void CancelEditing()
    {
        _editing = false;
        _saveError = null;
    }

    private async Task SaveProfile()
    {
        var name = _editName.Trim();

        // Checked here as well as on the server so the common mistake doesn't cost a round trip.
        if (string.IsNullOrEmpty(name))
        {
            _saveError = "Full name is required";
            return;
        }

        if (name.Length > 100)
        {
            _saveError = "Full name must be 100 characters or fewer";
            return;
        }

        _saving = true;
        _saveError = null;

        try
        {
            var (success, updated, error) = await Api.UpdateProfileAsync(new UpdateProfileDto
            {
                FullName = name,
                Department = _editDepartment.Trim()
            });

            if (!success || updated is null)
            {
                // Stay in edit mode so what they typed isn't thrown away.
                _saveError = error ?? "Failed to update profile";
                return;
            }

            _user = updated;
            await Auth.UpdateCachedUserAsync(updated);
            _editing = false;
            Snackbar.Success("Profile updated");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task SignOutEverywhere()
    {
        _loggingOutAll = true;
        try
        {
            var (success, error) = await Api.LogoutAllAsync();
            if (success)
            {
                Snackbar.Success("Signed out of your other sessions");
                _confirmingLogoutAll = false;
            }
            else
            {
                Snackbar.Error(error ?? "Failed to sign out other sessions");
            }
        }
        finally
        {
            _loggingOutAll = false;
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/AssetDesk.Web/Pages/Profile.razor
git commit -m "feat(web): add the profile page"
```

---

### Task 5: Sidebar entry point

**Files:**
- Modify: `src/AssetDesk.Web/Layout/MainLayout.razor` — the user menu block (lines 94–129 before Task 3's edit shortened it), plus `_showChangePassword` (line 501), `OpenChangePassword` (line 598) and the `<ChangePasswordDialog>` element (line 494)

**Interfaces:**
- Consumes: the `/profile` route from Task 4; `<RoleBadge>` from Task 3.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Make the user card a link and drop the key icon**

In `src/AssetDesk.Web/Layout/MainLayout.razor`, replace the contents of the `<Authorized Context="userContext">` block in the user menu — the single `<div class="flex items-center gap-3 px-3 py-2 rounded-xl bg-slate-50 dark:bg-slate-800/50">` element and everything inside it — with:

```razor
                        <div class="flex items-center gap-1">
                            <NavLink href="/profile" @onclick="CloseMobileMenu"
                                     class="flex items-center gap-3 flex-1 min-w-0 px-3 py-2 rounded-xl bg-slate-50 dark:bg-slate-800/50 hover:bg-slate-100 dark:hover:bg-slate-700/50 transition-colors"
                                     ActiveClass="ring-1 ring-primary-500">
                                <div class="w-9 h-9 rounded-full bg-gradient-to-br from-slate-400 to-slate-500 flex items-center justify-center text-white text-sm font-medium shrink-0">
                                    @userContext.User.Identity?.Name?.FirstOrDefault()
                                </div>
                                <div class="flex-1 min-w-0">
                                    <p class="text-sm font-medium text-slate-900 dark:text-white truncate">@userContext.User.Identity?.Name</p>
                                    <p class="text-xs text-slate-500 truncate">
                                        <RoleBadge Role="@(userContext.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Staff")" />
                                    </p>
                                </div>
                            </NavLink>
                            <button @onclick="Logout" title="Logout" class="p-2 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-200 dark:hover:bg-slate-700 dark:hover:text-slate-300 transition-all duration-200 shrink-0">
                                <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
                                </svg>
                            </button>
                        </div>
```

- [ ] **Step 2: Remove the now-unreachable dialog**

Delete the `<ChangePasswordDialog @bind-IsOpen="_showChangePassword" />` element and the comment line above it (`<!-- Change Password Dialog -->`) near the end of the markup. The page owns the dialog now.

- [ ] **Step 3: Remove the dead code behind it**

In the `@code` block, delete the field declaration:

```csharp
    private bool _showChangePassword;
```

and the method:

```csharp
    private void OpenChangePassword() => _showChangePassword = true;
```

- [ ] **Step 4: Verify nothing else referenced them**

```bash
grep -n "_showChangePassword\|OpenChangePassword" src/AssetDesk.Web/Layout/MainLayout.razor
```

Expected: no output.

- [ ] **Step 5: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Web/Layout/MainLayout.razor
git commit -m "feat(web): link the sidebar user card to the profile page"
```

---

### Task 6: Verify in the running app

**Files:** none — this task changes nothing, it confirms the previous five work together.

- [ ] **Step 1: Run the full test suite**

```bash
dotnet test
```

Expected: PASS, no failures.

- [ ] **Step 2: Start the API**

```bash
cd src/AssetDesk.Api && dotnet run
```

Expected: listening on `https://localhost:5001`.

- [ ] **Step 3: Start the web app in a second terminal**

```bash
cd src/AssetDesk.Web && dotnet run
```

Expected: listening on `https://localhost:5002`.

- [ ] **Step 4: Walk the page**

Sign in as `admin@company.com` / `Admin123!` and check each of these:

1. Clicking the sidebar user card opens `/profile`.
2. The identity card shows the name, email, `Admin` badge in purple, and tenant name.
3. Edit → change the full name → Save. A snackbar appears, the field updates, **and the sidebar name changes without a reload**.
4. Edit → clear the name → Save. The inline error reads "Full name is required" and edit mode stays open.
5. Edit → change something → Cancel. The original values return.
6. Set department to blank and save; it reads "Not set" afterwards.
7. "Change password" opens the dialog; Cancel closes it.
8. "Sign out everywhere" asks for confirmation before doing anything.
9. Toggle dark mode with the top-bar control — every card, badge and input stays legible.
10. Narrow the window to phone width — the identity card stacks, no horizontal scroll.

- [ ] **Step 5: Note anything broken**

If a check fails, fix it and commit the fix before finishing. If everything passes, this task needs no commit.

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
|---|---|
| `/profile` route, `[Authorize]`, no role gate | 4 |
| Sidebar user card becomes the entry point | 5 |
| Key icon removed, dialog moves to the page | 4, 5 |
| `MainLayout` dead code removed | 5 |
| Single column, `max-w-3xl`, four sections | 4 |
| `RoleBadge` component, `MainLayout` adopts it | 3 |
| Loads from `GET /api/auth/me`, not local storage | 2, 4 |
| `PUT /api/auth/me` + `UpdateProfileDto` | 1 |
| Server validation: name 1–100, dept ≤100, blank → null | 1 |
| `ApiClient` three methods, `AuthService.UpdateCachedUserAsync` | 2 |
| Sidebar name refreshes without reload | 2, 4, verified in 6 |
| Loading skeleton, load failure + retry, save states | 4 |
| Seven API test cases | 1 |
| Manual verification (no Blazor test project) | 6 |

No gaps.

**Type consistency:** `UpdateProfileDto { FullName, Department }` is defined in Task 1 and used with those exact names in Tasks 2 and 4. `UpdateProfileAsync` returns `(bool Success, UserDto? User, string? Error)` in Task 2 and is destructured as `(success, updated, error)` in Task 4. `LogoutAllAsync` returns `(bool Success, string? Error)` in Task 2 and is destructured as `(success, error)` in Task 4. `RoleBadge` takes `Role` and `Size` in Task 3 and is called with those in Tasks 3, 4 and 5. `UpdateCurrentUser` is the action name asserted in Task 1's tests and implemented in Task 1's step 4.

**Placeholder scan:** every code step carries complete code; no TBD, no "handle errors appropriately", no "similar to Task N".
