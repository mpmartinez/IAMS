# Permission-Based RBAC with Custom Roles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hard-coded role checks with per-tenant permission grants, so each tenant can retune what its built-in roles allow and define custom roles of its own.

**Architecture:** A static permission catalog in code is the single source of truth for what permissions exist; a `RolePermission` table owns who holds them, keyed by `(RoleId, TenantId)`. Login resolves the user's role to a permission set and stamps one `permission` claim per grant into the JWT. API policies and the Blazor UI both read those claims, so server enforcement and UI gating cannot disagree.

**Tech Stack:** .NET 10, ASP.NET Core Identity, EF Core 10 + Npgsql, Blazor WebAssembly, xUnit, Tailwind.

**Spec:** `docs/superpowers/specs/2026-08-17-rbac-roles-design.md`

## Global Constraints

- Permission keys use the `iams:resource:action` convention **already present in the repo**. Never introduce a second format.
- The claim type is the literal string `permission`, matching what `PermissionView.razor:33` already reads.
- `RolePermission` gets **no global query filter**. Every read filters `TenantId` explicitly. See "Query-filter trap" below — getting this wrong empties every user's permissions at login and the failure is silent.
- Default grants must reproduce today's access exactly. The per-role tables in Task 1 are the contract; do not adjust them to taste.
- SuperAdmin keeps its existing bypass. It is never permission-resolved and its grants are never editable.
- Database is **PostgreSQL** (`Program.cs:29` uses `UseNpgsql`), despite what `CLAUDE.md` says. Tests use SQLite in-memory via `tests/IAMS.Api.Tests/TestDb.cs`.
- Run all tests with: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`

### Query-filter trap

`TokenService` runs during login, when the HTTP context is still unauthenticated. `TenantProvider.GetCurrentTenantId()` returns null and `IsSuperAdmin()` returns false at that moment. EF Core extracts the provider call into a query parameter and evaluates it eagerly, so a `_tenantProvider == null` guard inside a filter does **not** short-circuit the way C# `||` would — this is documented at `tests/IAMS.Api.Tests/TestDb.cs:21-26`. A filtered `RolePermission` would match zero rows at login and hand every user an empty permission set, with no error anywhere.

`RolePermission` therefore does not implement `ITenantEntity` and gets no `HasQueryFilter` call.

## File Structure

**Create:**
- `src/IAMS.Api/Authorization/Permissions.cs` — catalog and per-role defaults
- `src/IAMS.Api/Authorization/PermissionRequirement.cs` — requirement + handler + registration helper
- `src/IAMS.Api/Entities/ApplicationRole.cs` — Identity role with TenantId/IsBuiltIn/Description
- `src/IAMS.Api/Entities/RolePermission.cs` — the grant row
- `src/IAMS.Api/Services/PermissionResolver.cs` — role names + tenant to permission keys
- `src/IAMS.Api/Controllers/RolesController.cs`
- `src/IAMS.Shared/DTOs/RoleDto.cs`
- `src/IAMS.Web/Pages/Admin/Roles.razor`
- `tests/IAMS.Api.Tests/PermissionCatalogTests.cs`
- `tests/IAMS.Api.Tests/PermissionResolverTests.cs`
- `tests/IAMS.Api.Tests/RolePermissionSeedTests.cs`

**Modify:**
- `src/IAMS.Api/Data/AppDbContext.cs` — role type parameter, `RolePermissions` DbSet, entity config
- `src/IAMS.Api/Data/SeedData.cs` — seed `ApplicationRole` metadata and backfill grants per tenant
- `src/IAMS.Api/Program.cs` — Identity generic args, policy redefinitions, DI
- `src/IAMS.Api/Services/TokenService.cs` — emit permission claims
- `src/IAMS.Api/Controllers/{Assets,Tickets,TicketAttachments,Users,Attachments,WarrantyAlerts,Notifications,Tenants}Controller.cs`
- `src/IAMS.Web/Services/AuthStateProvider.cs` — copy permission claims out of the JWT
- `src/IAMS.Web/Services/ApiClient.cs` — roles/permissions endpoints
- `src/IAMS.Web/Shared/PermissionView.razor` — drop the Admin fallback
- `src/IAMS.Web/Layout/MainLayout.razor` — role lists to permission gates
- `src/IAMS.Web/Pages/Users.razor` — role dropdown from the API

---

### Task 1: Permission catalog

**Files:**
- Create: `src/IAMS.Api/Authorization/Permissions.cs`
- Test: `tests/IAMS.Api.Tests/PermissionCatalogTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `IAMS.Api.Authorization.Permissions` with `const string ClaimType = "permission"`, one `const string` per key, `PermissionDescriptor[] All`, `string[] Keys`, and `IReadOnlyList<string> DefaultsFor(string roleName)`. `PermissionDescriptor` is `record(string Key, string Group, string Label, string Description)`.

The defaults below are derived from the policies in `Program.cs:113-139` and the role attributes on controllers as they exist today. They reproduce current access exactly.

- [ ] **Step 1: Write the failing test**

Create `tests/IAMS.Api.Tests/PermissionCatalogTests.cs`:

```csharp
using IAMS.Api.Authorization;
using IAMS.Api.Entities;

namespace IAMS.Api.Tests;

public class PermissionCatalogTests
{
    [Fact]
    public void ClaimType_MatchesWhatPermissionViewReads()
    {
        // PermissionView.razor calls user.HasClaim("permission", key).
        Assert.Equal("permission", Permissions.ClaimType);
    }

    [Fact]
    public void EveryKey_UsesTheIamsConvention()
    {
        foreach (var key in Permissions.Keys)
        {
            var parts = key.Split(':');
            Assert.Equal(3, parts.Length);
            Assert.Equal("iams", parts[0]);
            Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        }
    }

    [Fact]
    public void Keys_AreUnique()
    {
        Assert.Equal(Permissions.Keys.Length, Permissions.Keys.Distinct().Count());
    }

    [Fact]
    public void Admin_HoldsEveryPermission()
    {
        Assert.Equal(
            Permissions.Keys.OrderBy(k => k),
            Permissions.DefaultsFor(Roles.Admin).OrderBy(k => k));
    }

    [Theory]
    [InlineData(Roles.Staff, 13)]
    [InlineData(Roles.Auditor, 3)]
    [InlineData(Roles.Management, 1)]
    [InlineData(Roles.Employee, 1)]
    public void BuiltInRoles_HaveTheExpectedGrantCount(string role, int expected)
    {
        Assert.Equal(expected, Permissions.DefaultsFor(role).Count);
    }

    [Fact]
    public void EveryRole_CanFileTickets()
    {
        // CanFileTickets today lists every authenticated role including Employee.
        foreach (var role in Roles.All)
            Assert.Contains(Permissions.TicketsFile, Permissions.DefaultsFor(role));
    }

    [Fact]
    public void Auditor_KeepsReportsAndAssignmentReads_ButNoAssetWrites()
    {
        var auditor = Permissions.DefaultsFor(Roles.Auditor);
        Assert.Contains(Permissions.ReportsView, auditor);
        Assert.Contains(Permissions.AssignmentsView, auditor);
        Assert.DoesNotContain(Permissions.AssetsCreate, auditor);
        Assert.DoesNotContain(Permissions.AssetsDelete, auditor);
    }

    [Fact]
    public void Staff_CanImportButNotDelete()
    {
        var staff = Permissions.DefaultsFor(Roles.Staff);
        Assert.Contains(Permissions.AssetsImport, staff);
        Assert.Contains(Permissions.AssetsCreate, staff);
        Assert.DoesNotContain(Permissions.AssetsDelete, staff);
    }

    [Fact]
    public void UnknownRole_GetsNothing()
    {
        Assert.Empty(Permissions.DefaultsFor("NoSuchRole"));
    }

    [Fact]
    public void EveryDescriptor_HasAGroupAndLabel()
    {
        Assert.All(Permissions.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Group));
            Assert.False(string.IsNullOrWhiteSpace(d.Label));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
        });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter PermissionCatalogTests`

Expected: FAIL to compile — `The type or namespace name 'Authorization' does not exist in the namespace 'IAMS.Api'`.

- [ ] **Step 3: Write the catalog**

Create `src/IAMS.Api/Authorization/Permissions.cs`:

```csharp
using IAMS.Api.Entities;

namespace IAMS.Api.Authorization;

/// <param name="Key">Stable identifier stored in RolePermission and emitted as a claim.</param>
/// <param name="Group">Heading the permission sits under in the /admin/roles matrix.</param>
public sealed record PermissionDescriptor(string Key, string Group, string Label, string Description);

/// <summary>
/// The set of permissions that exist. Deliberately code-owned: a permission is only real if some
/// policy checks it, so a database-backed catalog would drift from the policies the way the Users
/// role dropdown drifted from Roles.TenantAssignable. The database owns the grants, not the catalog.
/// </summary>
public static class Permissions
{
    /// Matches the claim type PermissionView.razor already reads. Do not rename.
    public const string ClaimType = "permission";

    public const string AssetsView = "iams:assets:view";
    public const string AssetsCreate = "iams:assets:create";
    public const string AssetsEdit = "iams:assets:edit";
    public const string AssetsDelete = "iams:assets:delete";
    public const string AssetsImport = "iams:assets:import";
    public const string AssetsDebug = "iams:assets:debug";

    public const string AssignmentsView = "iams:assignments:view";
    public const string AssignmentsAssign = "iams:assignments:assign";
    public const string AssignmentsReturn = "iams:assignments:return";

    public const string TicketsFile = "iams:tickets:file";
    public const string TicketsQueue = "iams:tickets:queue";
    public const string TicketsManage = "iams:tickets:manage";

    public const string ReportsView = "iams:reports:view";

    public const string UsersView = "iams:users:view";
    public const string UsersManage = "iams:users:manage";
    public const string UsersRead = "iams:users:read";

    public const string RolesView = "iams:roles:view";
    public const string RolesManage = "iams:roles:manage";

    public const string AttachmentsManage = "iams:attachments:manage";

    public const string WarrantyManage = "iams:warranty:manage";
    public const string WarrantyDelete = "iams:warranty:delete";

    public const string NotificationsTest = "iams:notifications:test";

    public static readonly PermissionDescriptor[] All =
    [
        new(AssetsView, "Assets", "View assets", "See the asset list and individual asset records."),
        new(AssetsCreate, "Assets", "Create assets", "Add a new asset."),
        new(AssetsEdit, "Assets", "Edit assets", "Change details on an existing asset."),
        new(AssetsDelete, "Assets", "Delete assets", "Permanently remove an asset."),
        new(AssetsImport, "Assets", "Bulk import", "Upload a spreadsheet to create many assets at once."),
        new(AssetsDebug, "Assets", "List all asset tags", "Enumerate every asset tag in the tenant."),

        new(AssignmentsView, "Assignments", "View assignments", "See who holds which asset, and the assignment history."),
        new(AssignmentsAssign, "Assignments", "Assign assets", "Hand an asset to a user."),
        new(AssignmentsReturn, "Assignments", "Return assets", "Record an asset coming back."),

        new(TicketsFile, "Tickets", "File tickets", "Raise a ticket and follow your own."),
        new(TicketsQueue, "Tickets", "View the queue", "See every ticket in the tenant, not just your own."),
        new(TicketsManage, "Tickets", "Work the queue", "Assign, progress, and resolve other people's tickets."),

        new(ReportsView, "Reports", "View reports", "Open the inventory, warranty, and value reports."),

        new(UsersView, "Users", "View users", "Browse the full user list with roles and status."),
        new(UsersManage, "Users", "Manage users", "Create, edit, deactivate users and set their role."),
        new(UsersRead, "Users", "Look up users", "Read the names-only list used to pick an assignee."),

        new(RolesView, "Roles", "View roles", "See the roles in this organisation and what they grant."),
        new(RolesManage, "Roles", "Manage roles", "Create custom roles and change what any role grants."),

        new(AttachmentsManage, "Attachments", "Manage attachments", "Upload and delete files on assets."),

        new(WarrantyManage, "Warranty", "Manage alerts", "Acknowledge and update warranty alerts."),
        new(WarrantyDelete, "Warranty", "Delete alerts", "Remove a warranty alert."),

        new(NotificationsTest, "Notifications", "Send test notification", "Push a test notification, for diagnosing delivery."),
    ];

    public static readonly string[] Keys = All.Select(p => p.Key).ToArray();

    public static bool IsValid(string key) => Keys.Contains(key);

    /// <summary>
    /// The grants a built-in role starts with in every tenant. These reproduce the access the
    /// hard-coded policies gave before this feature existed; see the plan for the derivation.
    /// </summary>
    public static IReadOnlyList<string> DefaultsFor(string roleName) => roleName switch
    {
        // SuperAdmin bypasses authorisation entirely. It holds everything so the matrix shows
        // the truth rather than an empty grid.
        Roles.SuperAdmin => Keys,

        Roles.Admin => Keys,

        Roles.Staff =>
        [
            AssetsView, AssetsCreate, AssetsEdit, AssetsImport,
            AssignmentsView, AssignmentsAssign, AssignmentsReturn,
            TicketsFile, TicketsQueue, TicketsManage,
            UsersRead, AttachmentsManage, WarrantyManage,
        ],

        Roles.Auditor => [AssignmentsView, TicketsFile, ReportsView],

        Roles.Management => [TicketsFile],

        Roles.Employee => [TicketsFile],

        _ => [],
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter PermissionCatalogTests`

Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/IAMS.Api/Authorization/Permissions.cs tests/IAMS.Api.Tests/PermissionCatalogTests.cs && git commit -m "feat(auth): add the permission catalog and per-role defaults"
```

---

### Task 2: Role and grant entities, Identity swap, migration

**Files:**
- Create: `src/IAMS.Api/Entities/ApplicationRole.cs`, `src/IAMS.Api/Entities/RolePermission.cs`
- Modify: `src/IAMS.Api/Data/AppDbContext.cs:9`, `:35`, `:470`
- Modify: `src/IAMS.Api/Program.cs:54`, `:225`
- Modify: `src/IAMS.Api/Data/SeedData.cs:15`, `:18-24`
- Modify: `src/IAMS.Api/Controllers/TenantsController.cs` (RoleManager generic arg)

**Interfaces:**
- Consumes: `Permissions` (Task 1)
- Produces: `ApplicationRole { string Id, string? Name, Guid? TenantId, bool IsBuiltIn, string? Description }`; `RolePermission { Guid Id, string RoleId, Guid TenantId, string Permission, ApplicationRole? Role }`; `AppDbContext.RolePermissions` and inherited `AppDbContext.Roles` typed as `DbSet<ApplicationRole>`.

- [ ] **Step 1: Write the entities**

Create `src/IAMS.Api/Entities/ApplicationRole.cs`:

```csharp
using Microsoft.AspNetCore.Identity;

namespace IAMS.Api.Entities;

public class ApplicationRole : IdentityRole
{
    /// Null for the built-in roles, which are shared by every tenant. Set for custom roles,
    /// which only their own tenant may see or edit.
    public Guid? TenantId { get; set; }

    public bool IsBuiltIn { get; set; }

    public string? Description { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }
}
```

Create `src/IAMS.Api/Entities/RolePermission.cs`:

```csharp
namespace IAMS.Api.Entities;

/// <summary>
/// One permission granted to one role within one tenant.
///
/// Deliberately NOT an ITenantEntity and deliberately without a global query filter. TokenService
/// resolves permissions during login, before the request is authenticated, so ITenantProvider
/// reports no tenant at that moment. EF evaluates the filter's provider call eagerly into a query
/// parameter, so the null guard used by the other filters does not short-circuit, and a filtered
/// read would silently return zero rows and hand every user an empty permission set.
/// Every read of this table filters TenantId explicitly instead.
/// </summary>
public class RolePermission
{
    public Guid Id { get; set; }

    public string RoleId { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public string Permission { get; set; } = string.Empty;

    public ApplicationRole? Role { get; set; }
    public Tenant? Tenant { get; set; }
}
```

- [ ] **Step 2: Swap the Identity role type in AppDbContext**

In `src/IAMS.Api/Data/AppDbContext.cs`, change line 9 from:

```csharp
public class AppDbContext : IdentityDbContext<ApplicationUser>
```

to:

```csharp
public class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
```

Add after line 35 (`public DbSet<LookupValue> LookupValues => Set<LookupValue>();`):

```csharp
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
```

Add this entity configuration immediately before the `LookupValue` block (currently line 467), so it stays ahead of the trailing UTC converter loop that must remain last:

```csharp
        // Configure ApplicationRole. Built-in roles have a null TenantId and are shared; custom
        // roles belong to exactly one tenant.
        modelBuilder.Entity<ApplicationRole>(entity =>
        {
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasIndex(e => e.TenantId);
        });

        // Configure RolePermission. No query filter by design - see the class comment.
        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.RoleId).HasMaxLength(450).IsRequired();
            entity.Property(e => e.Permission).HasMaxLength(100).IsRequired();

            entity.HasIndex(e => new { e.RoleId, e.TenantId, e.Permission }).IsUnique();
            entity.HasIndex(e => e.TenantId);

            entity.HasOne(e => e.Role)
                .WithMany()
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 3: Update every RoleManager and Identity generic argument**

In `src/IAMS.Api/Program.cs` line 54, change `AddIdentity<ApplicationUser, IdentityRole>(` to `AddIdentity<ApplicationUser, ApplicationRole>(`.

In `src/IAMS.Api/Program.cs` line 225, change `GetRequiredService<RoleManager<IdentityRole>>()` to `GetRequiredService<RoleManager<ApplicationRole>>()`.

In `src/IAMS.Api/Data/SeedData.cs` line 15, change the parameter `RoleManager<IdentityRole> roleManager` to `RoleManager<ApplicationRole> roleManager`.

In `src/IAMS.Api/Controllers/TenantsController.cs`, change the injected `RoleManager<IdentityRole>` to `RoleManager<ApplicationRole>`, and the `new IdentityRole("Admin")` call to `new ApplicationRole("Admin") { IsBuiltIn = true }`.

- [ ] **Step 4: Seed built-in role metadata**

In `src/IAMS.Api/Data/SeedData.cs`, replace the loop at lines 18-24 with:

```csharp
        // Create roles including SuperAdmin, and keep their built-in metadata current.
        foreach (var role in Roles.All)
        {
            var existing = await roleManager.FindByNameAsync(role);
            if (existing is null)
            {
                await roleManager.CreateAsync(new ApplicationRole(role)
                {
                    IsBuiltIn = true,
                    TenantId = null,
                    Description = Roles.DescriptionFor(role)
                });
            }
            else if (!existing.IsBuiltIn || existing.Description is null)
            {
                existing.IsBuiltIn = true;
                existing.TenantId = null;
                existing.Description = Roles.DescriptionFor(role);
                await roleManager.UpdateAsync(existing);
            }
        }
```

Add to `src/IAMS.Api/Entities/Roles.cs`, inside the `Roles` class:

```csharp
    public static string DescriptionFor(string role) => role switch
    {
        SuperAdmin => "Platform operator. Bypasses tenant isolation and every permission check.",
        Admin => "Full control of this organisation, including users and roles.",
        Management => "Files and follows tickets. No asset or queue management.",
        Staff => "Runs the asset estate and works the ticket queue.",
        Auditor => "Read-only oversight: reports and assignment history.",
        Employee => "Files and follows their own tickets.",
        _ => ""
    };
```

- [ ] **Step 5: Create the migration**

```bash
cd "src/IAMS.Api" && dotnet ef migrations add AddRolePermissions
```

Expected: creates `src/IAMS.Api/Migrations/<timestamp>_AddRolePermissions.cs` adding `TenantId`, `IsBuiltIn`, `Description` to `AspNetRoles` and creating the `RolePermissions` table.

- [ ] **Step 6: Verify the solution builds**

Run: `dotnet build`

Expected: Build succeeded. If `RoleManager<IdentityRole>` errors remain, fix each to `RoleManager<ApplicationRole>` — Step 3 lists every known site, but the compiler is authoritative.

- [ ] **Step 7: Commit**

```bash
git add -A src/IAMS.Api && git commit -m "feat(auth): add ApplicationRole and RolePermission with migration"
```

---

### Task 3: Backfill grants per tenant

**Files:**
- Modify: `src/IAMS.Api/Data/SeedData.cs`
- Test: `tests/IAMS.Api.Tests/RolePermissionSeedTests.cs`

**Interfaces:**
- Consumes: `Permissions.DefaultsFor` (Task 1), `RolePermission` (Task 2)
- Produces: `static Task SeedData.EnsureRolePermissionsAsync(AppDbContext db, Guid tenantId)` — idempotent; provisions default grants for any built-in role that has none in that tenant, and touches nothing else. It reads roles from `db.Roles` directly, so it needs no `RoleManager`.

Idempotent-and-additive is the required behaviour: it must not delete grants, or every restart would wipe a tenant's customisations.

- [ ] **Step 1: Write the failing test**

Create `tests/IAMS.Api.Tests/RolePermissionSeedTests.cs`:

```csharp
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class RolePermissionSeedTests
{
    private static async Task<ApplicationRole> SeedRoleAsync(AppDbContext db, string name)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = true
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    [Fact]
    public async Task Backfill_GivesEachBuiltInRoleItsDefaults()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var granted = await db.RolePermissions
            .Where(rp => rp.RoleId == staffId && rp.TenantId == tenantId)
            .Select(rp => rp.Permission)
            .ToListAsync();

        Assert.Equal(
            Permissions.DefaultsFor(Roles.Staff).OrderBy(k => k),
            granted.OrderBy(k => k));
    }

    [Fact]
    public async Task Backfill_IsIdempotent()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterFirst = await db.RolePermissions.CountAsync();
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);
        var afterSecond = await db.RolePermissions.CountAsync();

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task Backfill_DoesNotRestoreARevokedGrant()
    {
        // A tenant that unticks a box must not have it reappear on the next restart.
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);
        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var revoked = await db.RolePermissions.FirstAsync(rp =>
            rp.RoleId == staffId && rp.Permission == Permissions.AssetsCreate);
        db.RolePermissions.Remove(revoked);
        await db.SaveChangesAsync();

        await SeedData.EnsureRolePermissionsAsync(db, tenantId);

        Assert.False(await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == staffId
            && rp.TenantId == tenantId
            && rp.Permission == Permissions.AssetsCreate));
    }

    [Fact]
    public async Task Backfill_KeepsTenantsIndependent()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantA);
        await TestDb.SeedTenantAsync(db, tenantB);
        foreach (var r in Roles.All) await SeedRoleAsync(db, r);

        await SeedData.EnsureRolePermissionsAsync(db, tenantA);
        await SeedData.EnsureRolePermissionsAsync(db, tenantB);

        var staffId = (await db.Roles.FirstAsync(r => r.Name == Roles.Staff)).Id;
        var toRevoke = await db.RolePermissions.FirstAsync(rp =>
            rp.RoleId == staffId && rp.TenantId == tenantA && rp.Permission == Permissions.AssetsEdit);
        db.RolePermissions.Remove(toRevoke);
        await db.SaveChangesAsync();

        Assert.True(await db.RolePermissions.AnyAsync(rp =>
            rp.RoleId == staffId && rp.TenantId == tenantB && rp.Permission == Permissions.AssetsEdit));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter RolePermissionSeedTests`

Expected: FAIL to compile — `'SeedData' does not contain a definition for 'EnsureRolePermissionsAsync'`.

- [ ] **Step 3: Implement the backfill**

Add to `src/IAMS.Api/Data/SeedData.cs`:

```csharp
    /// <summary>
    /// Ensures every built-in role holds its default grants in this tenant.
    ///
    /// Additive and idempotent on purpose: it inserts what is missing and never deletes. A tenant
    /// that unticks a permission must keep it unticked across restarts, so "missing row" is a
    /// legitimate customisation, not drift to repair.
    /// </summary>
    public static async Task EnsureRolePermissionsAsync(AppDbContext db, Guid tenantId)
    {
        var builtInRoles = await db.Roles
            .Where(r => r.IsBuiltIn && r.Name != null)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync();

        if (builtInRoles.Count == 0) return;

        var roleIds = builtInRoles.Select(r => r.Id).ToList();

        // One round trip for everything already granted in this tenant.
        var existing = (await db.RolePermissions
                .Where(rp => rp.TenantId == tenantId && roleIds.Contains(rp.RoleId))
                .Select(rp => new { rp.RoleId, rp.Permission })
                .ToListAsync())
            .Select(x => (x.RoleId, x.Permission))
            .ToHashSet();

        // A tenant that has any grant for a role has already been provisioned; leaving it alone
        // is what stops a revoked permission from reappearing.
        var provisioned = existing.Select(e => e.RoleId).ToHashSet();

        var toAdd = new List<RolePermission>();
        foreach (var role in builtInRoles)
        {
            if (provisioned.Contains(role.Id)) continue;

            foreach (var permission in Permissions.DefaultsFor(role.Name!))
            {
                toAdd.Add(new RolePermission
                {
                    Id = Guid.NewGuid(),
                    RoleId = role.Id,
                    TenantId = tenantId,
                    Permission = permission
                });
            }
        }

        if (toAdd.Count == 0) return;

        db.RolePermissions.AddRange(toAdd);
        await db.SaveChangesAsync();
    }
```

Add `using IAMS.Api.Authorization;` to the top of `SeedData.cs`.

Then call it for every tenant at the end of `SeedData.Initialize`, before the method returns:

```csharp
        // Backfill grants for every tenant, including ones created before this feature existed.
        var tenantIds = await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync();
        foreach (var id in tenantIds)
            await EnsureRolePermissionsAsync(db, id);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter RolePermissionSeedTests`

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add -A src/IAMS.Api tests && git commit -m "feat(auth): backfill built-in role grants per tenant"
```

---

### Task 4: Permission resolver

**Files:**
- Create: `src/IAMS.Api/Services/PermissionResolver.cs`
- Modify: `src/IAMS.Api/Program.cs` (DI registration near line 154)
- Test: `tests/IAMS.Api.Tests/PermissionResolverTests.cs`

**Interfaces:**
- Consumes: `RolePermission`, `ApplicationRole` (Task 2)
- Produces: `IPermissionResolver` with `Task<IReadOnlyList<string>> GetPermissionsAsync(IEnumerable<string> roleNames, Guid tenantId, CancellationToken ct = default)`

It takes a set of role names rather than one because the seeded super admin holds both `SuperAdmin` and `Admin` (`SeedData.cs:70-71`). The product rule is one role per user; the resolver unions anyway so a multi-role account resolves correctly instead of silently losing grants.

- [ ] **Step 1: Write the failing test**

Create `tests/IAMS.Api.Tests/PermissionResolverTests.cs`:

```csharp
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;

namespace IAMS.Api.Tests;

public class PermissionResolverTests
{
    private static async Task<ApplicationRole> AddRoleAsync(AppDbContext db, string name, Guid? tenantId = null)
    {
        var role = new ApplicationRole(name)
        {
            Id = $"role-{name}-{tenantId?.ToString("N")[..4] ?? "global"}",
            NormalizedName = name.ToUpperInvariant(),
            IsBuiltIn = tenantId is null,
            TenantId = tenantId
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task GrantAsync(AppDbContext db, string roleId, Guid tenantId, params string[] permissions)
    {
        foreach (var p in permissions)
        {
            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(), RoleId = roleId, TenantId = tenantId, Permission = p
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolves_GrantsForTheRoleInThatTenant()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var role = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, role.Id, tenantId, Permissions.AssetsView, Permissions.AssetsEdit);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Staff], tenantId);

        Assert.Equal(
            new[] { Permissions.AssetsEdit, Permissions.AssetsView },
            result.OrderBy(x => x));
    }

    [Fact]
    public async Task DoesNotLeakAnotherTenantsGrants()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, mine);
        await TestDb.SeedTenantAsync(db, theirs);
        var role = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, role.Id, mine, Permissions.AssetsView);
        await GrantAsync(db, role.Id, theirs, Permissions.AssetsDelete);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Staff], mine);

        Assert.Equal([Permissions.AssetsView], result);
    }

    [Fact]
    public async Task UnionsAcrossRoles_AndDeduplicates()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var admin = await AddRoleAsync(db, Roles.Admin);
        var staff = await AddRoleAsync(db, Roles.Staff);
        await GrantAsync(db, admin.Id, tenantId, Permissions.AssetsView, Permissions.AssetsDelete);
        await GrantAsync(db, staff.Id, tenantId, Permissions.AssetsView);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Admin, Roles.Staff], tenantId);

        Assert.Equal(
            new[] { Permissions.AssetsDelete, Permissions.AssetsView },
            result.OrderBy(x => x));
    }

    [Fact]
    public async Task ReturnsEmpty_ForNoRoles()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;

        var result = await new PermissionResolver(db).GetPermissionsAsync([], Guid.NewGuid());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReturnsEmpty_ForARoleWithNoGrants()
    {
        var (db, conn) = TestDb.Create();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        await AddRoleAsync(db, Roles.Employee);

        var result = await new PermissionResolver(db).GetPermissionsAsync([Roles.Employee], tenantId);

        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter PermissionResolverTests`

Expected: FAIL to compile — `The type or namespace name 'PermissionResolver' could not be found`.

- [ ] **Step 3: Implement the resolver**

Create `src/IAMS.Api/Services/PermissionResolver.cs`:

```csharp
using IAMS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public interface IPermissionResolver
{
    Task<IReadOnlyList<string>> GetPermissionsAsync(
        IEnumerable<string> roleNames, Guid tenantId, CancellationToken ct = default);
}

public class PermissionResolver(AppDbContext db) : IPermissionResolver
{
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        IEnumerable<string> roleNames, Guid tenantId, CancellationToken ct = default)
    {
        var names = roleNames as IList<string> ?? roleNames.ToList();
        if (names.Count == 0) return [];

        // TenantId is filtered explicitly here. RolePermission has no global query filter - see
        // the comment on the entity for why a filter would return nothing during login.
        return await db.RolePermissions
            .Where(rp => rp.TenantId == tenantId)
            .Join(db.Roles.Where(r => r.Name != null && names.Contains(r.Name)),
                rp => rp.RoleId,
                r => r.Id,
                (rp, r) => rp.Permission)
            .Distinct()
            .ToListAsync(ct);
    }
}
```

Register it in `src/IAMS.Api/Program.cs` alongside the other scoped services (after line 154):

```csharp
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter PermissionResolverTests`

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add -A src/IAMS.Api tests && git commit -m "feat(auth): resolve a role's permissions within a tenant"
```

---

### Task 5: Authorization requirement, handler, and policy rewrite

**Files:**
- Create: `src/IAMS.Api/Authorization/PermissionRequirement.cs`
- Modify: `src/IAMS.Api/Program.cs:112-139`
- Modify: 8 controllers (exact sites listed in Step 3)

**Interfaces:**
- Consumes: `Permissions` (Task 1)
- Produces: `PermissionRequirement(string permission)`, `PermissionAuthorizationHandler`, and the extension `AuthorizationBuilder.RequirePermission(string policyName, string permission)`

- [ ] **Step 1: Write the requirement and handler**

Create `src/IAMS.Api/Authorization/PermissionRequirement.cs`:

```csharp
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Authorization;

namespace IAMS.Api.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // SuperAdmin keeps the bypass it has always had: it short-circuits tenant isolation, so
        // gating it on per-tenant grants would be incoherent.
        if (context.User.IsInRole(Roles.SuperAdmin) ||
            context.User.HasClaim(Permissions.ClaimType, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationBuilderExtensions
{
    public static AuthorizationBuilder RequirePermission(
        this AuthorizationBuilder builder, string policyName, string permission) =>
        builder.AddPolicy(policyName, policy =>
            policy.AddRequirements(new PermissionRequirement(permission)));
}
```

- [ ] **Step 2: Rewrite the policy block**

In `src/IAMS.Api/Program.cs`, replace lines 112-139 entirely with:

```csharp
// Authorization policies.
//
// Every policy below resolves to a permission the tenant can retune at /admin/roles. The two
// SuperAdmin policies stay role-based: they guard platform-level endpoints (tenants, shared
// reference data) that no tenant may grant itself.
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthorizationBuilder()
    .RequirePermission("CanCreateAssets", Permissions.AssetsCreate)
    .RequirePermission("CanEditAssets", Permissions.AssetsEdit)
    .RequirePermission("CanDeleteAssets", Permissions.AssetsDelete)
    .RequirePermission("CanManageAssets", Permissions.AssetsEdit)
    .RequirePermission("CanImportAssets", Permissions.AssetsImport)
    .RequirePermission("CanViewAssets", Permissions.AssetsView)
    .RequirePermission("Admin", Permissions.AssetsDebug)
    .RequirePermission("CanViewReports", Permissions.ReportsView)
    .RequirePermission("CanAssignAssets", Permissions.AssignmentsAssign)
    .RequirePermission("CanReturnAssets", Permissions.AssignmentsReturn)
    .RequirePermission("CanViewAssignments", Permissions.AssignmentsView)
    .RequirePermission("CanFileTickets", Permissions.TicketsFile)
    .RequirePermission("CanViewTicketQueue", Permissions.TicketsQueue)
    .RequirePermission("CanManageTicketQueue", Permissions.TicketsManage)
    .RequirePermission("CanViewUsers", Permissions.UsersView)
    .RequirePermission("CanManageUsers", Permissions.UsersManage)
    .RequirePermission("CanViewUsersList", Permissions.UsersRead)
    .RequirePermission("CanViewRoles", Permissions.RolesView)
    .RequirePermission("CanManageRoles", Permissions.RolesManage)
    .RequirePermission("CanManageAttachments", Permissions.AttachmentsManage)
    .RequirePermission("CanManageWarrantyAlerts", Permissions.WarrantyManage)
    .RequirePermission("CanDeleteWarrantyAlerts", Permissions.WarrantyDelete)
    .RequirePermission("CanSendTestNotifications", Permissions.NotificationsTest)
    // Platform-level: not tenant-tunable.
    .AddPolicy("SuperAdmin", policy => policy.RequireRole(Roles.SuperAdmin))
    .AddPolicy("CanManageTenants", policy => policy.RequireRole(Roles.SuperAdmin));
```

Add `using IAMS.Api.Authorization;` and `using Microsoft.AspNetCore.Authorization;` to the top of `Program.cs` — the latter is needed for the `IAuthorizationHandler` registration.

Note what this removes: the `Staff` and `Auditor` policies (`Staff` was overloaded across assets and tickets and is replaced by `CanViewAssets` / `CanViewTicketQueue` / `CanManageTicketQueue`; `Auditor` had no call sites), and `TenantAdmin` / `CanManageOrgSettings`, which also had no call sites.

- [ ] **Step 3: Repoint every controller attribute**

Apply each of these exactly. The left column is the current attribute argument, the right is the replacement.

`src/IAMS.Api/Controllers/AssetsController.cs`
- line 23: `Policy = "Staff"` → `Policy = "CanViewAssets"`
- line 72: `Policy = "Staff"` → `Policy = "CanViewAssets"`
- line 243: `Policy = "CanCreateAssets"` → `Policy = "CanImportAssets"`

`src/IAMS.Api/Controllers/TicketsController.cs`
- line 42: `Policy = "Staff"` → `Policy = "CanViewTicketQueue"`
- line 72: `Policy = "Staff"` → `Policy = "CanViewTicketQueue"`
- line 159: `Policy = "Staff"` → `Policy = "CanManageTicketQueue"`
- line 171: `Policy = "Staff"` → `Policy = "CanManageTicketQueue"`
- line 183: `Policy = "Staff"` → `Policy = "CanManageTicketQueue"`

`src/IAMS.Api/Controllers/TicketAttachmentsController.cs`
- line 173: `Policy = "Staff"` → `Policy = "CanManageTicketQueue"`

`src/IAMS.Api/Controllers/UsersController.cs`
- line 23: `Roles = "Admin"` → `Policy = "CanViewUsers"`
- line 73: `Roles = "Admin"` → `Policy = "CanManageUsers"`
- line 129: `Roles = "Admin"` → `Policy = "CanViewUsers"`
- line 148: `Roles = "Admin"` → `Policy = "CanManageUsers"`
- line 215: `Roles = "Admin"` → `Policy = "CanManageUsers"`

`src/IAMS.Api/Controllers/AttachmentsController.cs`
- line 75: `Roles = "Admin,Staff"` → `Policy = "CanManageAttachments"`
- line 172: `Roles = "Admin,Staff"` → `Policy = "CanManageAttachments"`

`src/IAMS.Api/Controllers/WarrantyAlertsController.cs`
- line 100: `Roles = "Admin,Staff"` → `Policy = "CanManageWarrantyAlerts"`
- line 130: `Roles = "Admin,Staff"` → `Policy = "CanManageWarrantyAlerts"`
- line 158: `Roles = "Admin"` → `Policy = "CanDeleteWarrantyAlerts"`

`src/IAMS.Api/Controllers/NotificationsController.cs`
- line 144: `Roles = "Admin"` → `Policy = "CanSendTestNotifications"`

Worked example — `UsersController.cs:23` goes from:

```csharp
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "Admin")]
```

to:

```csharp
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewUsers")]
```

- [ ] **Step 4: Verify no orphaned policy references remain**

Run: `git grep -n 'Policy = "Staff"\|Policy = "Auditor"\|Policy = "TenantAdmin"\|Policy = "CanManageOrgSettings"' -- src/`

Expected: no output. Any hit is a controller pointing at a policy that no longer exists, which throws at request time rather than build time.

- [ ] **Step 5: Build**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add -A src/IAMS.Api && git commit -m "feat(auth): enforce policies via permissions instead of role lists"
```

---

### Task 6: Emit permission claims, and revoke tokens on role change

**Files:**
- Modify: `src/IAMS.Api/Services/TokenService.cs:13-63`
- Modify: `src/IAMS.Api/Controllers/UsersController.cs:196-208`

**Interfaces:**
- Consumes: `IPermissionResolver` (Task 4), `Permissions.ClaimType` (Task 1)
- Produces: JWTs carrying one `permission` claim per grant

- [ ] **Step 1: Inject the resolver and emit claims**

In `src/IAMS.Api/Services/TokenService.cs`, add `IPermissionResolver permissionResolver` to the primary constructor at line 13-16, so it reads:

```csharp
public class TokenService(
    IConfiguration configuration,
    UserManager<ApplicationUser> userManager,
    IPermissionResolver permissionResolver,
    AppDbContext db)
```

Add `using IAMS.Api.Authorization;` at the top.

Then insert this immediately after the SuperAdmin role block (currently lines 46-50), before `var expireMinutes`:

```csharp
        // One claim per granted permission. The Blazor client reads these same claims, so the UI
        // cannot offer an action the API will reject. Resolved against the user's own tenant -
        // note the resolver filters TenantId explicitly, because ITenantProvider reports nothing
        // during login.
        var permissions = await permissionResolver.GetPermissionsAsync(roles, user.TenantId);
        foreach (var permission in permissions)
        {
            claims.Add(new Claim(Permissions.ClaimType, permission));
        }
```

- [ ] **Step 2: Revoke refresh tokens when a user's role changes**

In `src/IAMS.Api/Controllers/UsersController.cs`, the role-change block at lines 196-208 currently ends after re-adding roles on failure. Replace the whole block with:

```csharp
        // Update role if changed
        if (dto.Role is not null && !existingRoles.Contains(dto.Role))
        {
            await userManager.RemoveFromRolesAsync(user, existingRoles);
            var roleResult = await userManager.AddToRoleAsync(user, dto.Role);
            if (!roleResult.Succeeded)
            {
                // Put the old roles back rather than leaving the account with none.
                await userManager.AddToRolesAsync(user, existingRoles);
                return BadRequest(ApiResponse<UserDto>.Fail(
                    string.Join(", ", roleResult.Errors.Select(e => e.Description))));
            }

            // Their live token still carries the old role's permissions. This affects one person,
            // so force a refresh rather than wait out the token lifetime. Editing a role's
            // permissions is handled differently - see the spec.
            await tokenService.RevokeAllUserTokensAsync(user.Id);
        }
```

Add `TokenService tokenService` to the `UsersController` primary constructor parameter list at lines 16-20.

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`

Expected: all tests pass. If `TokenService` is constructed anywhere in tests, add the new parameter there.

- [ ] **Step 4: Commit**

```bash
git add -A src/IAMS.Api && git commit -m "feat(auth): put permission claims in the token, revoke on role change"
```

---

### Task 7: Roles API

**Files:**
- Create: `src/IAMS.Shared/DTOs/RoleDto.cs`, `src/IAMS.Api/Controllers/RolesController.cs`
- Modify: `src/IAMS.Api/Controllers/TenantsController.cs` (call the backfill for new tenants)
- Test: `tests/IAMS.Api.Tests/RolesApiTests.cs`

**Interfaces:**
- Consumes: `Permissions`, `IPermissionResolver`, `SeedData.EnsureRolePermissionsAsync`
- Produces: the DTOs below and `public static IReadOnlyList<string> RolesController.GrantableKeys(ClaimsPrincipal actor, bool isSuperAdmin)`, used by the escalation guard and unit-tested directly

- [ ] **Step 1: Write the DTOs**

Create `src/IAMS.Shared/DTOs/RoleDto.cs`:

```csharp
namespace IAMS.Shared.DTOs;

public record RoleDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsBuiltIn { get; init; }
    public int UserCount { get; init; }
    public required List<string> Permissions { get; init; }
}

public record AssignableRoleDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record CreateRoleDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> Permissions { get; init; } = [];
}

public record UpdateRoleDto
{
    public string? Description { get; init; }
    public required List<string> Permissions { get; init; }
}

public record PermissionDto
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
}

public record PermissionGroupDto
{
    public required string Group { get; init; }
    public required List<PermissionDto> Permissions { get; init; }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/IAMS.Api.Tests/RolesApiTests.cs`:

```csharp
using IAMS.Api.Authorization;
using IAMS.Api.Controllers;
using IAMS.Api.Data;
using IAMS.Api.Entities;

namespace IAMS.Api.Tests;

public class RolesApiTests
{
    [Fact]
    public void GrantableKeys_AreLimitedToWhatTheActorHolds()
    {
        var actor = TestPrincipals.With(Permissions.RolesManage, Permissions.AssetsView);

        var grantable = RolesController.GrantableKeys(actor, isSuperAdmin: false);

        Assert.Contains(Permissions.AssetsView, grantable);
        Assert.DoesNotContain(Permissions.AssetsDelete, grantable);
    }

    [Fact]
    public void GrantableKeys_AreUnlimitedForSuperAdmin()
    {
        var actor = TestPrincipals.With();

        var grantable = RolesController.GrantableKeys(actor, isSuperAdmin: true);

        Assert.Equal(Permissions.Keys.OrderBy(k => k), grantable.OrderBy(k => k));
    }

    [Fact]
    public void GrantableKeys_IgnoreUnknownClaims()
    {
        var actor = TestPrincipals.With("iams:not:real", Permissions.AssetsView);

        var grantable = RolesController.GrantableKeys(actor, isSuperAdmin: false);

        Assert.Equal([Permissions.AssetsView], grantable);
    }
}

internal static class TestPrincipals
{
    public static System.Security.Claims.ClaimsPrincipal With(params string[] permissions)
    {
        var claims = permissions
            .Select(p => new System.Security.Claims.Claim(Permissions.ClaimType, p))
            .ToList();
        return new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "test"));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter RolesApiTests`

Expected: FAIL to compile — `The type or namespace name 'RolesController' could not be found`.

- [ ] **Step 4: Write the controller**

Create `src/IAMS.Api/Controllers/RolesController.cs`:

```csharp
using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class RolesController(
    AppDbContext db,
    RoleManager<ApplicationRole> roleManager,
    UserManager<ApplicationUser> userManager,
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>
    /// The permissions this actor may hand to a role. Without this cap anyone holding
    /// iams:roles:manage could mint a role with every permission and assign it to themselves,
    /// which would make every other permission decorative.
    /// </summary>
    public static IReadOnlyList<string> GrantableKeys(ClaimsPrincipal actor, bool isSuperAdmin)
    {
        if (isSuperAdmin) return Permissions.Keys;

        return actor.FindAll(Permissions.ClaimType)
            .Select(c => c.Value)
            .Where(Permissions.IsValid)
            .Distinct()
            .ToList();
    }

    [HttpGet("~/api/permissions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewRoles")]
    public ActionResult<ApiResponse<List<PermissionGroupDto>>> GetPermissions()
    {
        var groups = Permissions.All
            .GroupBy(p => p.Group)
            .Select(g => new PermissionGroupDto
            {
                Group = g.Key,
                Permissions = g.Select(p => new PermissionDto
                {
                    Key = p.Key, Group = p.Group, Label = p.Label, Description = p.Description
                }).ToList()
            })
            .ToList();

        return Ok(ApiResponse<List<PermissionGroupDto>>.Ok(groups));
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewRoles")]
    public async Task<ActionResult<ApiResponse<List<RoleDto>>>> GetRoles()
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var roles = await VisibleRoles(tenantId).ToListAsync();
        var roleIds = roles.Select(r => r.Id).ToList();

        var grants = await db.RolePermissions
            .Where(rp => rp.TenantId == tenantId && roleIds.Contains(rp.RoleId))
            .Select(rp => new { rp.RoleId, rp.Permission })
            .ToListAsync();

        var result = new List<RoleDto>();
        foreach (var role in roles)
        {
            var usersInRole = await userManager.GetUsersInRoleAsync(role.Name!);
            result.Add(new RoleDto
            {
                Id = role.Id,
                Name = role.Name!,
                Description = role.Description,
                IsBuiltIn = role.IsBuiltIn,
                UserCount = usersInRole.Count(u => u.TenantId == tenantId),
                Permissions = grants.Where(g => g.RoleId == role.Id).Select(g => g.Permission).ToList()
            });
        }

        return Ok(ApiResponse<List<RoleDto>>.Ok(result.OrderByDescending(r => r.IsBuiltIn).ThenBy(r => r.Name).ToList()));
    }

    [HttpGet("assignable")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageUsers")]
    public async Task<ActionResult<ApiResponse<List<AssignableRoleDto>>>> GetAssignable()
    {
        var tenantId = tenantProvider.GetRequiredTenantId();
        var isSuperAdmin = tenantProvider.IsSuperAdmin();

        var roles = await VisibleRoles(tenantId)
            // SuperAdmin short-circuits tenant isolation, so only an existing SuperAdmin may hand
            // it out - same rule as Roles.TenantAssignable.
            .Where(r => isSuperAdmin || r.Name != Roles.SuperAdmin)
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name)
            .Select(r => new AssignableRoleDto { Name = r.Name!, Description = r.Description })
            .ToListAsync();

        return Ok(ApiResponse<List<AssignableRoleDto>>.Ok(roles));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> CreateRole(CreateRoleDto dto)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();

        var name = dto.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(ApiResponse<RoleDto>.Fail("Name is required."));

        if (await roleManager.FindByNameAsync(name) is not null)
            return BadRequest(ApiResponse<RoleDto>.Fail($"A role named \"{name}\" already exists."));

        var rejected = Validate(dto.Permissions, out var accepted);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        var role = new ApplicationRole(name)
        {
            TenantId = tenantId,
            IsBuiltIn = false,
            Description = dto.Description?.Trim()
        };

        var created = await roleManager.CreateAsync(role);
        if (!created.Succeeded)
            return BadRequest(ApiResponse<RoleDto>.Fail(
                string.Join(", ", created.Errors.Select(e => e.Description))));

        await ReplaceGrants(role.Id, tenantId, accepted);

        return Ok(ApiResponse<RoleDto>.Ok(new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            IsBuiltIn = false,
            UserCount = 0,
            Permissions = accepted
        }));
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> UpdateRole(string id, UpdateRoleDto dto)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();

        var role = await VisibleRoles(tenantId).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        // SuperAdmin bypasses every check, so letting anyone edit its grants would present a
        // control that does nothing while implying it does something.
        if (role.Name == Roles.SuperAdmin)
            return BadRequest(ApiResponse<RoleDto>.Fail("The SuperAdmin role cannot be edited."));

        var rejected = Validate(dto.Permissions, out var accepted);
        if (rejected is not null) return BadRequest(ApiResponse<RoleDto>.Fail(rejected));

        if (!role.IsBuiltIn && dto.Description is not null)
        {
            role.Description = dto.Description.Trim();
            await roleManager.UpdateAsync(role);
        }

        await ReplaceGrants(role.Id, tenantId, accepted);

        var usersInRole = await userManager.GetUsersInRoleAsync(role.Name!);

        return Ok(ApiResponse<RoleDto>.Ok(new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            IsBuiltIn = role.IsBuiltIn,
            UserCount = usersInRole.Count(u => u.TenantId == tenantId),
            Permissions = accepted
        }));
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageRoles")]
    public async Task<IActionResult> DeleteRole(string id)
    {
        var tenantId = tenantProvider.GetRequiredTenantId();

        var role = await VisibleRoles(tenantId).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return NotFound();

        if (role.IsBuiltIn)
            return BadRequest(ApiResponse<object>.Fail("Built-in roles cannot be deleted."));

        var holders = (await userManager.GetUsersInRoleAsync(role.Name!))
            .Count(u => u.TenantId == tenantId);
        if (holders > 0)
            return Conflict(ApiResponse<object>.Fail(
                $"{holders} user{(holders == 1 ? "" : "s")} still hold this role. Move them to another role first."));

        var grants = await db.RolePermissions
            .Where(rp => rp.RoleId == role.Id && rp.TenantId == tenantId)
            .ToListAsync();
        db.RolePermissions.RemoveRange(grants);
        await db.SaveChangesAsync();

        await roleManager.DeleteAsync(role);
        return NoContent();
    }

    /// Built-in roles plus this tenant's own. Another tenant's custom role is simply not there,
    /// so every lookup by id 404s rather than leaking its existence.
    private IQueryable<ApplicationRole> VisibleRoles(Guid tenantId) =>
        db.Roles.Where(r => r.TenantId == null || r.TenantId == tenantId);

    /// Returns an error message, or null and the accepted key list.
    private string? Validate(List<string> requested, out List<string> accepted)
    {
        accepted = requested.Distinct().ToList();

        var unknown = accepted.Where(p => !Permissions.IsValid(p)).ToList();
        if (unknown.Count > 0)
            return $"Unknown permission{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown)}";

        var grantable = GrantableKeys(User, tenantProvider.IsSuperAdmin());
        var overreach = accepted.Where(p => !grantable.Contains(p)).ToList();
        if (overreach.Count > 0)
            return $"You cannot grant a permission you do not hold yourself: {string.Join(", ", overreach)}";

        return null;
    }

    private async Task ReplaceGrants(string roleId, Guid tenantId, List<string> permissions)
    {
        var existing = await db.RolePermissions
            .Where(rp => rp.RoleId == roleId && rp.TenantId == tenantId)
            .ToListAsync();

        db.RolePermissions.RemoveRange(existing);
        db.RolePermissions.AddRange(permissions.Select(p => new RolePermission
        {
            Id = Guid.NewGuid(), RoleId = roleId, TenantId = tenantId, Permission = p
        }));

        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 5: Provision grants for new tenants**

In `src/IAMS.Api/Controllers/TenantsController.cs`, immediately after the tenant admin user is created and added to the Admin role, add:

```csharp
        // Give the new tenant its own copy of every built-in role's default grants, so its admin
        // can start editing them straight away.
        await SeedData.EnsureRolePermissionsAsync(db, tenant.Id);
```

Add `using IAMS.Api.Data;` if not already present.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter RolesApiTests`

Expected: PASS, 3 tests.

- [ ] **Step 7: Run the full suite and build**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A src tests && git commit -m "feat(roles): add the roles API with escalation guards"
```

---

### Task 8: Client reads permission claims

**Files:**
- Modify: `src/IAMS.Web/Services/AuthStateProvider.cs:43-72`
- Modify: `src/IAMS.Web/Shared/PermissionView.razor:25-41`
- Modify: `src/IAMS.Web/Services/ApiClient.cs`

**Interfaces:**
- Consumes: the `permission` claims minted in Task 6, the endpoints from Task 7
- Produces: `ApiClient.GetRolesAsync()`, `.GetAssignableRolesAsync()`, `.GetPermissionGroupsAsync()`, `.CreateRoleAsync(CreateRoleDto)`, `.UpdateRoleAsync(string, UpdateRoleDto)`, `.DeleteRoleAsync(string)`

- [ ] **Step 1: Copy permission claims into the identity**

In `src/IAMS.Web/Services/AuthStateProvider.cs`, insert immediately after the `claims.AddRange(roleClaims...)` line (currently line 72):

```csharp
            // Permission claims come from the token for the same reason roles do: the token is
            // what the API authorises against. UserDto carries no permissions at all.
            claims.AddRange(jwtToken.Claims
                .Where(c => c.Type == "permission")
                .Select(c => c.Value)
                .Distinct()
                .Select(p => new Claim("permission", p)));
```

- [ ] **Step 2: Remove the Admin fallback from PermissionView**

Replace the `CheckPermission` method in `src/IAMS.Web/Shared/PermissionView.razor` (lines 25-41) with:

```csharp
    private async Task CheckPermission()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            _hasPermission = false;
            return;
        }

        // SuperAdmin bypasses authorisation on the API, so it must bypass here too or the UI
        // would hide controls that would in fact work.
        _hasPermission = user.IsInRole("SuperAdmin") || user.HasClaim("permission", Permission);
    }
```

The old `IsInRole("Admin") || IsInRole("Administrator")` fallback is deliberately gone. It existed because nothing emitted permission claims, so the check on line 33 never passed. Left in place now it would grant every Admin every permission regardless of what their tenant configured, defeating the feature.

- [ ] **Step 3: Add the API client methods**

Append to `src/IAMS.Web/Services/ApiClient.cs`, following the existing lookup-method style:

```csharp
    // Roles and permissions.

    public async Task<List<RoleDto>?> GetRolesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<RoleDto>>>("api/roles");
        return response?.Data;
    }

    public async Task<List<AssignableRoleDto>?> GetAssignableRolesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<AssignableRoleDto>>>("api/roles/assignable");
        return response?.Data;
    }

    public async Task<List<PermissionGroupDto>?> GetPermissionGroupsAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<PermissionGroupDto>>>("api/permissions");
        return response?.Data;
    }

    public async Task<(bool Success, RoleDto? Role, string? Error)> CreateRoleAsync(CreateRoleDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/roles", dto);

        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<RoleDto>>();
        if (!response.IsSuccessStatusCode)
            return (false, null, payload?.Message ?? "Failed to create role.");

        return (true, payload?.Data, null);
    }

    public async Task<(bool Success, string? Error)> UpdateRoleAsync(string id, UpdateRoleDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/roles/{id}", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update role.");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteRoleAsync(string id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/roles/{id}");

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to delete role.");
        }

        return (true, null);
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A src/IAMS.Web && git commit -m "feat(web): read permission claims and call the roles API"
```

---

### Task 9: Roles admin page

**Files:**
- Create: `src/IAMS.Web/Pages/Admin/Roles.razor`
- Modify: `src/IAMS.Web/Layout/MainLayout.razor` (Admin nav group, around line 59-65)

**Interfaces:**
- Consumes: `ApiClient` role methods (Task 8), `RoleDto`, `PermissionGroupDto`, `CreateRoleDto`, `UpdateRoleDto`

- [ ] **Step 1: Create the page**

Create `src/IAMS.Web/Pages/Admin/Roles.razor`:

```razor
@page "/admin/roles"
@using IAMS.Web.Components.UI
@using Microsoft.AspNetCore.Components.Authorization
@inject ApiClient Api
@inject SnackbarService Snackbar
@inject AuthenticationStateProvider AuthProvider

<PageTitle>Roles - IAMS</PageTitle>

<div class="space-y-6">
    <div class="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div>
            <h1 class="text-2xl font-bold text-slate-900 dark:text-white">Roles</h1>
            <p class="text-slate-500 dark:text-slate-400 mt-1">
                What each role in your organisation is allowed to do. Built-in roles can be retuned;
                custom roles are yours to create.
            </p>
        </div>
        <Button OnClick="OpenCreate">
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4" />
            </svg>
            New Role
        </Button>
    </div>

    <Card Class="overflow-hidden">
        @if (_roles is null)
        {
            <div class="p-4 sm:p-5 space-y-2">
                @for (int i = 0; i < 5; i++)
                {
                    <div class="h-14 rounded-xl skeleton"></div>
                }
            </div>
        }
        else if (_roles.Count == 0)
        {
            <div class="p-4 sm:p-5">
                <EmptyState Title="No roles" Description="Create a role to get started." />
            </div>
        }
        else
        {
            <div class="overflow-x-auto">
                <table class="w-full">
                    <thead>
                        <tr class="border-b border-slate-200 dark:border-slate-700">
                            <th class="text-left py-3 px-4 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Role</th>
                            <th class="text-left py-3 px-4 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Permissions</th>
                            <th class="text-left py-3 px-4 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Users</th>
                            <th class="text-right py-3 px-4 text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">&nbsp;</th>
                        </tr>
                    </thead>
                    <tbody class="divide-y divide-slate-100 dark:divide-slate-700/50">
                        @foreach (var role in _roles)
                        {
                            <tr @key="role.Id">
                                <td class="py-3 px-4">
                                    <div class="flex items-center gap-2">
                                        <span class="font-medium text-slate-900 dark:text-white text-sm">@role.Name</span>
                                        <Badge Variant="@(role.IsBuiltIn ? "secondary" : "success")">
                                            @(role.IsBuiltIn ? "Built-in" : "Custom")
                                        </Badge>
                                    </div>
                                    @if (!string.IsNullOrWhiteSpace(role.Description))
                                    {
                                        <p class="text-xs text-slate-500 dark:text-slate-400 mt-0.5">@role.Description</p>
                                    }
                                </td>
                                <td class="py-3 px-4 text-sm text-slate-600 dark:text-slate-300">
                                    @role.Permissions.Count of @_totalPermissions
                                </td>
                                <td class="py-3 px-4 text-sm text-slate-600 dark:text-slate-300">@role.UserCount</td>
                                <td class="py-3 px-4">
                                    <div class="flex items-center justify-end gap-1">
                                        <Button Variant="ghost" Size="sm" OnClick="() => OpenEdit(role)">
                                            @(role.Name == "SuperAdmin" ? "View" : "Edit")
                                        </Button>
                                        @if (!role.IsBuiltIn)
                                        {
                                            <Button Variant="ghost" Size="sm" OnClick="() => ConfirmDelete(role)">Delete</Button>
                                        }
                                    </div>
                                </td>
                            </tr>
                        }
                    </tbody>
                </table>
            </div>
        }
    </Card>
</div>

<Modal IsOpen="@_showEditor" Title="@EditorTitle" OnClose="CloseEditor">
    <ChildContent>
        <div class="space-y-4">
            <FormField LabelText="Name">
                <Input Value="@_formName" ValueChanged="v => _formName = v" Disabled="@NameLocked" MaxLength="100" Placeholder="e.g. Warehouse Lead" />
            </FormField>

            <FormField LabelText="Description">
                <Input Value="@_formDescription" ValueChanged="v => _formDescription = v" Disabled="@NameLocked" MaxLength="500" Placeholder="What this role is for" />
            </FormField>

            @if (_readOnly)
            {
                <Alert Variant="warning" Title="Not editable">
                    SuperAdmin bypasses every permission check, so its grants cannot be changed.
                </Alert>
            }

            @if (_groups is not null)
            {
                <div class="space-y-4">
                    @foreach (var group in _groups)
                    {
                        var groupKeys = group.Permissions.Select(p => p.Key).ToList();
                        var allOn = groupKeys.All(_selected.Contains);
                        <div class="rounded-xl border border-slate-200 dark:border-slate-700 p-3">
                            <div class="flex items-center justify-between mb-2">
                                <p class="text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">@group.Group</p>
                                @if (!_readOnly)
                                {
                                    <button type="button" @onclick="() => ToggleGroup(groupKeys, !allOn)"
                                            class="text-xs font-medium text-primary-600 hover:text-primary-700">
                                        @(allOn ? "Clear all" : "Select all")
                                    </button>
                                }
                            </div>
                            <div class="space-y-1.5">
                                @foreach (var permission in group.Permissions)
                                {
                                    var key = permission.Key;
                                    var blocked = _readOnly || !_grantable.Contains(key);
                                    <label class="flex items-start gap-2.5 @(blocked ? "opacity-50 cursor-not-allowed" : "cursor-pointer")">
                                        <input type="checkbox" disabled="@blocked"
                                               checked="@_selected.Contains(key)"
                                               @onchange="e => Toggle(key, (bool)(e.Value ?? false))"
                                               class="mt-0.5 rounded border-slate-300 dark:border-slate-600 text-primary-600 focus:ring-primary-500" />
                                        <span>
                                            <span class="block text-sm text-slate-900 dark:text-white">@permission.Label</span>
                                            <span class="block text-xs text-slate-500 dark:text-slate-400">@permission.Description</span>
                                        </span>
                                    </label>
                                }
                            </div>
                        </div>
                    }
                </div>
            }

            @if (!string.IsNullOrEmpty(_formError))
            {
                <div class="px-4 py-3 rounded-xl bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-900/50">
                    <p class="text-sm text-red-700 dark:text-red-400">@_formError</p>
                </div>
            }
        </div>
    </ChildContent>
    <FooterContent>
        <Button Variant="ghost" OnClick="CloseEditor">@(_readOnly ? "Close" : "Cancel")</Button>
        @if (!_readOnly)
        {
            <Button OnClick="Save" Loading="@_saving" Disabled="@string.IsNullOrWhiteSpace(_formName)">Save</Button>
        }
    </FooterContent>
</Modal>

@code {
    private List<RoleDto>? _roles;
    private List<PermissionGroupDto>? _groups;
    private HashSet<string> _grantable = [];
    private int _totalPermissions;

    private bool _showEditor;
    private bool _readOnly;
    private RoleDto? _editing;
    private string _formName = "";
    private string _formDescription = "";
    private HashSet<string> _selected = [];
    private string? _formError;
    private bool _saving;

    private string EditorTitle => _editing is null ? "New Role" : _editing.Name;
    private bool NameLocked => _editing?.IsBuiltIn == true || _readOnly;

    protected override async Task OnInitializedAsync()
    {
        _groups = await Api.GetPermissionGroupsAsync();
        _totalPermissions = _groups?.Sum(g => g.Permissions.Count) ?? 0;

        // Mirror the server's escalation guard: an admin may only grant what they hold, so
        // anything else renders disabled rather than failing on save.
        var authState = await AuthProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        _grantable = user.IsInRole("SuperAdmin")
            ? (_groups?.SelectMany(g => g.Permissions).Select(p => p.Key).ToHashSet() ?? [])
            : user.FindAll("permission").Select(c => c.Value).ToHashSet();

        await Load();
    }

    private async Task Load() => _roles = await Api.GetRolesAsync();

    private void OpenCreate()
    {
        _editing = null;
        _readOnly = false;
        _formName = "";
        _formDescription = "";
        _selected = [];
        _formError = null;
        _showEditor = true;
    }

    private void OpenEdit(RoleDto role)
    {
        _editing = role;
        _readOnly = role.Name == "SuperAdmin";
        _formName = role.Name;
        _formDescription = role.Description ?? "";
        _selected = role.Permissions.ToHashSet();
        _formError = null;
        _showEditor = true;
    }

    private void CloseEditor()
    {
        _showEditor = false;
        _editing = null;
        _formError = null;
    }

    private void Toggle(string key, bool on)
    {
        if (on) _selected.Add(key); else _selected.Remove(key);
    }

    private void ToggleGroup(List<string> keys, bool on)
    {
        foreach (var key in keys.Where(_grantable.Contains))
        {
            if (on) _selected.Add(key); else _selected.Remove(key);
        }
    }

    private async Task Save()
    {
        _formError = null;
        _saving = true;

        if (_editing is null)
        {
            var (success, _, error) = await Api.CreateRoleAsync(new CreateRoleDto
            {
                Name = _formName.Trim(),
                Description = string.IsNullOrWhiteSpace(_formDescription) ? null : _formDescription.Trim(),
                Permissions = _selected.ToList()
            });

            if (success)
            {
                Snackbar.Success("Role created");
                CloseEditor();
                await Load();
            }
            else
            {
                _formError = error;
            }
        }
        else
        {
            var (success, error) = await Api.UpdateRoleAsync(_editing.Id, new UpdateRoleDto
            {
                Description = string.IsNullOrWhiteSpace(_formDescription) ? null : _formDescription.Trim(),
                Permissions = _selected.ToList()
            });

            if (success)
            {
                Snackbar.Success("Role updated");
                CloseEditor();
                await Load();
            }
            else
            {
                _formError = error;
            }
        }

        _saving = false;
    }

    private async Task ConfirmDelete(RoleDto role)
    {
        var (success, error) = await Api.DeleteRoleAsync(role.Id);
        if (success)
        {
            Snackbar.Success($"Deleted {role.Name}");
            await Load();
        }
        else
        {
            Snackbar.Error(error ?? "Could not delete the role.");
        }
    }
}
```

- [ ] **Step 2: Add the nav entry**

In `src/IAMS.Web/Layout/MainLayout.razor`, inside the Admin nav group (currently lines 59-65), add after the Users `NavItem`:

```razor
                        <PermissionView Permission="iams:roles:view">
                            <NavItem Href="/admin/roles" Icon="users" Label="Roles" OnNavigation="CloseMobileMenu" />
                        </PermissionView>
```

`NavItem` renders icons from a fixed `switch` at `src/IAMS.Web/Layout/NavItem.razor:5` supporting exactly: `home`, `cube`, `tag`, `users`, `chart`, `qr-scan`, `shield-exclamation`, `bell`, `building`, `wrench`, `alert-triangle`, `clipboard-list`. Anything else renders no icon silently, so `users` is used above. Adding a new icon case is out of scope for this task.

- [ ] **Step 3: Build**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add -A src/IAMS.Web && git commit -m "feat(web): add the /admin/roles screen"
```

---

### Task 10: Replace role gating in the UI

**Files:**
- Modify: `src/IAMS.Web/Pages/Users.razor:174-190`, `:3`, `:312`, `:454`
- Modify: `src/IAMS.Web/Layout/MainLayout.razor` (nav entries at lines 44-58, and the mobile bar around 415-467)
- Modify: `src/IAMS.Web/Pages/Assets/*.razor`, `Reports.razor`, `WarrantyAlerts.razor` page-level `[Authorize(Roles = ...)]` attributes

**Interfaces:**
- Consumes: `ApiClient.GetAssignableRolesAsync()` (Task 8), `PermissionView` (Task 8)

- [ ] **Step 1: Feed the Users role dropdown from the API**

In `src/IAMS.Web/Pages/Users.razor`, replace the `<select>` block at lines 174-190 with:

```razor
                    <label class="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Role *</label>
                    <select @bind="_modalModel.Role" class="w-full px-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-600 bg-white dark:bg-slate-700 text-slate-900 dark:text-white transition-all duration-200 hover:border-slate-300 dark:hover:border-slate-500 focus:border-primary-500 focus:ring-2 focus:ring-primary-500/20 text-sm">
                        @if (_assignableRoles is null)
                        {
                            <option value="@_modalModel.Role">@_modalModel.Role</option>
                        }
                        else
                        {
                            @foreach (var role in _assignableRoles)
                            {
                                <option value="@role.Name">@role.Name</option>
                            }
                        }
                    </select>
```

Add to the `@code` block:

```csharp
    private List<AssignableRoleDto>? _assignableRoles;
```

and load it in `OnInitializedAsync` alongside the existing loads:

```csharp
        _assignableRoles = await Api.GetAssignableRolesAsync();
```

The hand-maintained option list and its drift comment are gone — that was the original defect this feature exists to fix.

- [ ] **Step 2: Convert page-level authorization attributes**

Replace each page's `@attribute [Authorize(Roles = "...")]` with the permission-gated equivalent. Blazor's `[Authorize]` takes a policy name, and the WASM client has no policies registered, so gate the page body with `PermissionView` and keep `[Authorize]` bare:

- `src/IAMS.Web/Pages/Users.razor:3` — change `@attribute [Authorize(Roles = "Admin,SuperAdmin")]` to `@attribute [Authorize]`, and wrap the page's root `<div class="space-y-6">` in `<PermissionView Permission="iams:users:view">`.
- `src/IAMS.Web/Pages/Admin/Roles.razor` — add `@attribute [Authorize]` and wrap its root div in `<PermissionView Permission="iams:roles:view">`.

Leave `src/IAMS.Web/Pages/Admin/Lookups.razor` and `Tenants.razor` on `[Authorize(Roles = "SuperAdmin")]` — those are platform-level and deliberately not permission-gated.

- [ ] **Step 3: Convert the nav role lists**

In `src/IAMS.Web/Layout/MainLayout.razor`, replace these `AuthorizeView` wrappers with `PermissionView`:

- line 45 `<AuthorizeView Roles="Admin,Staff,Management,Auditor,SuperAdmin">` around the Assets item → `<PermissionView Permission="iams:assets:view">`
- line 48 `<AuthorizeView Roles="Admin,Staff">` around the Tickets item → `<PermissionView Permission="iams:tickets:queue">`
- line 52 `<AuthorizeView Roles="Admin,Staff,Management,Auditor,SuperAdmin">` around Warranty Alerts → `<PermissionView Permission="iams:warranty:manage">`
- line 56 `<AuthorizeView Roles="Admin,Staff,Management,Auditor,SuperAdmin">` around Categories → `<PermissionView Permission="iams:assets:view">`
- line 60 `<AuthorizeView Roles="Admin,SuperAdmin">` around the Admin group → `<PermissionView Permission="iams:users:view">`

Apply the same substitutions to the mobile bottom bar blocks at lines 415-467, matching each entry to the permission its destination page requires.

Leave the `Employee` and `SuperAdmin` `AuthorizeView` blocks as they are: the Employee portal links are a role-shaped navigation choice rather than a permission, and the Platform group is genuinely SuperAdmin-only.

Note this is a deliberate behaviour change on two entries. Assets and Warranty Alerts were shown to Management and Auditor by the nav, but the API refused them (`Staff` policy was `Admin, Staff`). The links now match what the API actually allows.

- [ ] **Step 4: Verify no stale role gates remain**

Run: `git grep -n 'AuthorizeView Roles=' -- src/IAMS.Web`

Expected: only the `Employee` and `SuperAdmin` blocks described above. Anything else is an unconverted gate.

- [ ] **Step 5: Build**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`

Expected: all tests pass.

- [ ] **Step 7: Manual verification**

Start both projects and confirm, signed in as `staff@company.com` / `Staff123!`:

1. `/admin/roles` is not in the nav and returns nothing if visited directly.
2. Assets and Tickets are both in the nav and both load.
3. Signed in as `admin@company.com` / `Admin123!`, open `/admin/roles`, untick "Delete assets" on Staff, and save.
4. Sign back in as staff and confirm the delete control on an asset detail page is gone.

Step 4 is the end-to-end proof: a tenant retuned a built-in role and both the API and the UI honoured it.

- [ ] **Step 8: Commit**

```bash
git add -A src/IAMS.Web && git commit -m "feat(web): gate navigation and pages on permissions"
```

---

## Self-Review Notes

Checked against the spec:

- Catalog, data model, resolution, policy conversion, `Staff` retirement, import split, token claims, revocation split, roles API with all four guardrails, tenant provisioning, `/admin/roles`, Users dropdown, `PermissionView` fix, and all six test areas each map to a task above.
- The spec's `RolePermissionSeedTests` coverage of "editing one tenant's copy does not affect another" is Task 3 Step 1, `Backfill_KeepsTenantsIndependent`.
- The spec's cross-tenant isolation requirement is covered by `VisibleRoles` in Task 7 plus `DoesNotLeakAnotherTenantsGrants` in Task 4.

Deviations recorded here rather than silently:

- `IPermissionResolver` takes a set of role names, not one. The seeded super admin holds two roles (`SeedData.cs:70-71`), and a single-role signature would silently drop grants for it.
- Task 3's backfill skips any role already provisioned in a tenant rather than inserting missing keys one by one. Per-key insertion would resurrect a permission the tenant deliberately revoked on the next restart.
- Task 10 gates Blazor pages with `PermissionView` rather than `[Authorize(Policy = ...)]`, because the WASM client registers no authorization policies.
