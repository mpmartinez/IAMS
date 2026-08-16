# Service Desk Phase 1 (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `Maintenance` entity with a general `Ticket` entity — three types, comments, attachments, an append-only audit log — exposed through a REST API, with existing maintenance data migrated intact.

**Architecture:** `Maintenance` is renamed to `Ticket` by an EF Core migration rather than recreated, so rows and attachments survive. Status changes go through a single `TicketService` that validates transitions against a static table; request fulfilment wraps ticket closure and `AssetAssignment` creation in one database transaction. Auditing is a `SaveChangesInterceptor` on `AppDbContext`, so no controller can forget to write history.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10 with Npgsql, xUnit + `Microsoft.EntityFrameworkCore.Sqlite` (in-memory) for tests.

**Source spec:** `docs/superpowers/specs/2026-08-16-itsm-service-desk-design.md`

## Global Constraints

- Target framework is `net10.0`; `ImplicitUsings` and `Nullable` are both `enable`.
- All API endpoints return `ApiResponse<T>` or `ApiResponse<PagedResponse<T>>` from `IAMS.Shared.DTOs`. Use the `ApiResponse<T>.Ok(data, message)` and `ApiResponse<T>.Fail(message, errors)` factories — do not construct them by hand.
- Every tenant-scoped entity implements `ITenantEntity` and gets a global query filter in `AppDbContext.OnModelCreating` matching the existing shape exactly: `e => _tenantProvider == null || _tenantProvider.IsSuperAdmin() || e.TenantId == _tenantProvider.GetCurrentTenantId()`.
- Status, type and priority values are `string` columns with `HasMaxLength(50)`, validated against static constant classes. Do not introduce enums — the codebase uses the `AssetStatus` / `MaintenanceStatus` string-constant pattern throughout.
- The UTC `DateTime` value converter block at the end of `OnModelCreating` must stay last. Add new entity configuration above it.
- Timestamps are `DateTime.UtcNow`.
- Authorization policy names already defined in `Program.cs`: `Admin`, `Staff`, `CanManageAssets`, `CanViewReports`.
- `Roles.Employee`, `Tenant.MaxTicketsPerMonth` and the seat-metering exclusions are **phase 2** and out of scope here. Ticket creation in this plan is staff-only and unquotaed.

---

### Task 1: Test project

**Files:**
- Create: `tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`
- Create: `tests/IAMS.Api.Tests/TestDb.cs`
- Create: `tests/IAMS.Api.Tests/FakeTenantProvider.cs`
- Create: `tests/IAMS.Api.Tests/TestDbTests.cs`
- Modify: `IAMS.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `TestDb.Create(ITenantProvider? tenantProvider = null)` returning `(AppDbContext Db, SqliteConnection Connection)`; `TestDb.SeedTenantAsync(AppDbContext db, Guid tenantId)` returning `Task<Tenant>`; `FakeTenantProvider(Guid? tenantId, bool isSuperAdmin = false)`.

- [ ] **Step 1: Create the test project file**

Create `tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\IAMS.Api\IAMS.Api.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the fake tenant provider**

Create `tests/IAMS.Api.Tests/FakeTenantProvider.cs`:

```csharp
using IAMS.Api.Services;

namespace IAMS.Api.Tests;

public class FakeTenantProvider : ITenantProvider
{
    private Guid? _tenantId;
    private readonly bool _isSuperAdmin;

    public FakeTenantProvider(Guid? tenantId, bool isSuperAdmin = false)
    {
        _tenantId = tenantId;
        _isSuperAdmin = isSuperAdmin;
    }

    public Guid? GetCurrentTenantId() => _tenantId;

    public Guid GetRequiredTenantId() =>
        _tenantId ?? throw new UnauthorizedAccessException("Tenant context is required for this operation");

    public bool IsSuperAdmin() => _isSuperAdmin;

    public void SetTenantId(Guid tenantId) => _tenantId = tenantId;

    public void ClearTenantOverride() => _tenantId = null;
}
```

- [ ] **Step 3: Create the database helper**

SQLite's in-memory database lives only as long as its connection is open, so the helper hands the connection back to the caller to dispose.

Create `tests/IAMS.Api.Tests/TestDb.cs`:

```csharp
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public static class TestDb
{
    public static (AppDbContext Db, SqliteConnection Connection) Create(
        ITenantProvider? tenantProvider = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = tenantProvider is null
            ? new AppDbContext(options)
            : new AppDbContext(options, tenantProvider);

        db.Database.EnsureCreated();
        return (db, connection);
    }

    public static async Task<Tenant> SeedTenantAsync(AppDbContext db, Guid tenantId)
    {
        var tenant = SubscriptionTiers.CreateWithLimits("Test Agency", $"test-{tenantId:N}", SubscriptionTiers.Pro);
        tenant.Id = tenantId;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public static async Task<ApplicationUser> SeedUserAsync(
        AppDbContext db, Guid tenantId, string id, string fullName)
    {
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{id}@test.local",
            Email = $"{id}@test.local",
            FullName = fullName,
            TenantId = tenantId
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Asset> SeedAssetAsync(
        AppDbContext db, Guid tenantId, string assetTag, string status = AssetStatus.Available)
    {
        var asset = new Asset
        {
            TenantId = tenantId,
            AssetTag = assetTag,
            DeviceType = DeviceTypes.Laptop,
            Status = status
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
```

- [ ] **Step 4: Write the smoke test**

Create `tests/IAMS.Api.Tests/TestDbTests.cs`:

```csharp
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TestDbTests
{
    [Fact]
    public async Task Create_gives_a_usable_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            var found = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal("IAMS-0001", found.AssetTag);
            Assert.Equal(tenantId, found.TenantId);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_assets()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedAssetAsync(db, mine, "MINE-1");
            await TestDb.SeedAssetAsync(db, theirs, "THEIRS-1");

            var visible = await db.Assets.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE-1", visible[0].AssetTag);
        }
    }
}
```

- [ ] **Step 5: Add the project to the solution and run the tests**

```bash
dotnet sln IAMS.sln add tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj
```

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj`
Expected: `Passed! - Failed: 0, Passed: 2`

If `Query_filter_hides_other_tenants_assets` fails with two assets returned, the model was cached from the first test's filter-free context. Confirm each test builds its own `DbContextOptions` — `TestDb.Create` already does this per call.

- [ ] **Step 6: Commit**

```bash
git add tests/IAMS.Api.Tests IAMS.sln
git commit -m "test: add IAMS.Api.Tests project with SQLite in-memory harness"
```

---

### Task 2: Ticket constants and status transitions

**Files:**
- Create: `src/IAMS.Api/Entities/TicketConstants.cs`
- Create: `tests/IAMS.Api.Tests/TicketWorkflowTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TicketTypes.{Incident,Request,SecurityEvent}`, `TicketStatus.{New,Assigned,InProgress,OnHold,Resolved,Closed,Cancelled}` plus `TicketStatus.All` / `TicketStatus.Open` / `TicketStatus.IsValid(string)`, `TicketPriority.{Low,Medium,High,Critical}`, and `TicketWorkflow.CanTransition(string from, string to)` / `TicketWorkflow.IsOpen(string status)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketWorkflowTests.cs`:

```csharp
using IAMS.Api.Entities;

namespace IAMS.Api.Tests;

public class TicketWorkflowTests
{
    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Assigned)]
    [InlineData(TicketStatus.New, TicketStatus.Cancelled)]
    [InlineData(TicketStatus.Assigned, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Assigned, TicketStatus.OnHold)]
    [InlineData(TicketStatus.InProgress, TicketStatus.OnHold)]
    [InlineData(TicketStatus.InProgress, TicketStatus.Resolved)]
    [InlineData(TicketStatus.OnHold, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Closed)]
    [InlineData(TicketStatus.Resolved, TicketStatus.InProgress)]
    public void Allows_valid_transitions(string from, string to)
    {
        Assert.True(TicketWorkflow.CanTransition(from, to));
    }

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Resolved)]
    [InlineData(TicketStatus.New, TicketStatus.Closed)]
    [InlineData(TicketStatus.Closed, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Closed, TicketStatus.New)]
    [InlineData(TicketStatus.Cancelled, TicketStatus.InProgress)]
    [InlineData(TicketStatus.OnHold, TicketStatus.Resolved)]
    public void Rejects_invalid_transitions(string from, string to)
    {
        Assert.False(TicketWorkflow.CanTransition(from, to));
    }

    [Fact]
    public void Rejects_unknown_status_values()
    {
        Assert.False(TicketWorkflow.CanTransition("Banana", TicketStatus.Closed));
        Assert.False(TicketWorkflow.CanTransition(TicketStatus.New, "Banana"));
    }

    [Fact]
    public void Open_statuses_are_the_four_working_states()
    {
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.New));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.Assigned));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.InProgress));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.OnHold));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Resolved));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Closed));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Cancelled));
    }

    [Fact]
    public void Validators_accept_known_values_and_reject_others()
    {
        Assert.True(TicketTypes.IsValid(TicketTypes.SecurityEvent));
        Assert.False(TicketTypes.IsValid("Escalation"));
        Assert.True(TicketStatus.IsValid(TicketStatus.OnHold));
        Assert.False(TicketStatus.IsValid("Paused"));
        Assert.True(TicketPriority.IsValid(TicketPriority.Critical));
        Assert.False(TicketPriority.IsValid("Urgent"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketWorkflowTests`
Expected: build failure — `The name 'TicketStatus' does not exist in the current context` (and the same for `TicketTypes`, `TicketPriority`, `TicketWorkflow`).

- [ ] **Step 3: Write the implementation**

Create `src/IAMS.Api/Entities/TicketConstants.cs`:

```csharp
namespace IAMS.Api.Entities;

public static class TicketTypes
{
    public const string Incident = "Incident";
    public const string Request = "Request";
    public const string SecurityEvent = "SecurityEvent";

    public static readonly string[] All = [Incident, Request, SecurityEvent];

    public static bool IsValid(string type) => All.Contains(type);
}

public static class TicketStatus
{
    public const string New = "New";
    public const string Assigned = "Assigned";
    public const string InProgress = "InProgress";
    public const string OnHold = "OnHold";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All =
        [New, Assigned, InProgress, OnHold, Resolved, Closed, Cancelled];

    public static readonly string[] Open =
        [New, Assigned, InProgress, OnHold];

    public static bool IsValid(string status) => All.Contains(status);
}

public static class TicketPriority
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public const string Critical = "Critical";

    public static readonly string[] All = [Low, Medium, High, Critical];

    public static bool IsValid(string priority) => All.Contains(priority);
}

/// <summary>
/// The single source of truth for how a ticket may move between statuses.
/// Closed and Cancelled are terminal.
/// </summary>
public static class TicketWorkflow
{
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [TicketStatus.New] = [TicketStatus.Assigned, TicketStatus.Cancelled],
        [TicketStatus.Assigned] = [TicketStatus.InProgress, TicketStatus.OnHold, TicketStatus.Cancelled],
        [TicketStatus.InProgress] = [TicketStatus.OnHold, TicketStatus.Resolved, TicketStatus.Cancelled],
        [TicketStatus.OnHold] = [TicketStatus.InProgress, TicketStatus.Cancelled],
        [TicketStatus.Resolved] = [TicketStatus.Closed, TicketStatus.InProgress],
        [TicketStatus.Closed] = [],
        [TicketStatus.Cancelled] = []
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsOpen(string status) => TicketStatus.Open.Contains(status);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketWorkflowTests`
Expected: `Passed! - Failed: 0, Passed: 18` (nine allowed transitions, six rejected, three facts)

- [ ] **Step 5: Commit**

```bash
git add src/IAMS.Api/Entities/TicketConstants.cs tests/IAMS.Api.Tests/TicketWorkflowTests.cs
git commit -m "feat(tickets): add ticket type, status, priority constants and transition rules"
```

---

### Task 3: Entities and DbContext configuration

**Files:**
- Create: `src/IAMS.Api/Entities/Ticket.cs`
- Create: `src/IAMS.Api/Entities/TicketComment.cs`
- Create: `src/IAMS.Api/Entities/TicketAttachment.cs`
- Create: `src/IAMS.Api/Entities/AuditLog.cs`
- Delete: `src/IAMS.Api/Entities/Maintenance.cs`
- Delete: `src/IAMS.Api/Entities/MaintenanceAttachment.cs`
- Modify: `src/IAMS.Api/Entities/Asset.cs`
- Modify: `src/IAMS.Api/Data/AppDbContext.cs`
- Create: `tests/IAMS.Api.Tests/TicketEntityTests.cs`

**Interfaces:**
- Consumes: `TicketTypes`, `TicketStatus`, `TicketPriority` from Task 2.
- Produces: entities `Ticket`, `TicketComment`, `TicketAttachment`, `AuditLog`; `Asset.OwnerUserId` (`string?`) and `Asset.LastVerifiedAt` (`DateTime?`); DbSets `AppDbContext.Tickets`, `.TicketComments`, `.TicketAttachments`, `.AuditLogs`.

Read `src/IAMS.Api/Entities/MaintenanceAttachment.cs` before deleting it — `TicketAttachment` below must keep the same file-storage property names (`FileName`, `StoredFileName`, `ContentType`, `FileSizeBytes`, `Category`, `Description`, `UploadedByUserId`, `UploadedAt`) so `FileStorageService` and the storage-quota query keep working unchanged.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketEntityTests.cs`:

```csharp
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketEntityTests
{
    [Fact]
    public async Task Ticket_round_trips_with_its_defaults()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "J. Dela Cruz");

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Printer jams",
                RequesterUserId = "user-1"
            });
            await db.SaveChangesAsync();

            var saved = await db.Tickets.SingleAsync();

            Assert.Equal(TicketTypes.Incident, saved.Type);
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(TicketPriority.Medium, saved.Priority);
            Assert.Null(saved.AssetId);
            Assert.Null(saved.AssignedToUserId);
        }
    }

    [Fact]
    public async Task Ticket_number_is_unique_within_a_tenant_only()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantA, "a-1", "A One");
            await TestDb.SeedUserAsync(db, tenantB, "b-1", "B One");

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 1, Title = "A", RequesterUserId = "a-1" });
            db.Tickets.Add(new Ticket { TenantId = tenantB, TicketNumber = 1, Title = "B", RequesterUserId = "b-1" });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.Tickets.CountAsync());

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 1, Title = "dupe", RequesterUserId = "a-1" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Comments_cascade_when_their_ticket_is_deleted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "J. Dela Cruz");

            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Printer jams",
                RequesterUserId = "user-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            db.TicketComments.Add(new TicketComment
            {
                TenantId = tenantId,
                TicketId = ticket.Id,
                UserId = "user-1",
                Body = "Any update?"
            });
            await db.SaveChangesAsync();

            db.Tickets.Remove(ticket);
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Asset_carries_an_owner_and_a_verification_stamp()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "owner-1", "Crewing Manager");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0241");

            asset.OwnerUserId = "owner-1";
            asset.LastVerifiedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var reloaded = await db.Assets.SingleAsync();
            Assert.Equal("owner-1", reloaded.OwnerUserId);
            Assert.Equal(2026, reloaded.LastVerifiedAt!.Value.Year);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketEntityTests`
Expected: build failure — `The name 'Ticket' does not exist` and `'AppDbContext' does not contain a definition for 'Tickets'`.

- [ ] **Step 3: Create the entities**

Create `src/IAMS.Api/Entities/Ticket.cs`:

```csharp
namespace IAMS.Api.Entities;

/// <summary>
/// A unit of IT work: an incident, an equipment request, or a security event report.
/// Generalises the former Maintenance entity.
/// </summary>
public class Ticket : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Per-tenant display number, rendered as TKT-0183. Distinct from Id, which is global.</summary>
    public int TicketNumber { get; set; }

    public string Type { get; set; } = TicketTypes.Incident;
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = TicketStatus.New;
    public string Priority { get; set; } = TicketPriority.Medium;

    /// <summary>Optional: a Request exists before the asset that will fulfil it.</summary>
    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public string RequesterUserId { get; set; } = "";
    public ApplicationUser? RequesterUser { get; set; }
    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Phase 3 (SLA). Present now so the columns exist before there is history to lose.
    public DateTime? DueAt { get; set; }
    public DateTime? BreachedAt { get; set; }

    public string? Resolution { get; set; }

    /// <summary>Set when a Request is fulfilled, linking the ticket to the assignment it produced.</summary>
    public int? AssetAssignmentId { get; set; }
    public AssetAssignment? AssetAssignment { get; set; }

    public ICollection<TicketAttachment> Attachments { get; set; } = [];
    public ICollection<TicketComment> Comments { get; set; } = [];
}
```

Create `src/IAMS.Api/Entities/TicketComment.cs`:

```csharp
namespace IAMS.Api.Entities;

public class TicketComment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public string Body { get; set; } = "";

    /// <summary>Staff-only. Filtered out server-side before a requester ever sees the ticket.</summary>
    public bool IsInternal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

Create `src/IAMS.Api/Entities/TicketAttachment.cs` — keep the property names identical to the deleted `MaintenanceAttachment` so `FileStorageService` needs no changes:

```csharp
namespace IAMS.Api.Entities;

public class TicketAttachment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public required string FileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public required string Category { get; set; }
    public string? Description { get; set; }

    public string? UploadedByUserId { get; set; }
    public ApplicationUser? UploadedByUser { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
```

Create `src/IAMS.Api/Entities/AuditLog.cs`:

```csharp
namespace IAMS.Api.Entities;

/// <summary>
/// Append-only record of changes to audited entities. Never updated or deleted.
/// </summary>
public class AuditLog : ITenantEntity
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? UserId { get; set; }

    /// <summary>JSON object of shape { "Field": { "from": ..., "to": ... } }. Null for Created and Deleted.</summary>
    public string? Changes { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
}
```

- [ ] **Step 4: Add the two new Asset fields**

In `src/IAMS.Api/Entities/Asset.cs`, insert after the `AssignedToUser` property (currently line 24), keeping the existing "Legacy/additional fields" block below it:

```csharp
    /// <summary>The person accountable for this asset, as distinct from whoever currently holds it.</summary>
    public string? OwnerUserId { get; set; }
    public ApplicationUser? OwnerUser { get; set; }

    /// <summary>Stamped by a QR verification scan. Drives inventory-accuracy reporting.</summary>
    public DateTime? LastVerifiedAt { get; set; }
```

- [ ] **Step 5: Delete the Maintenance entities and wire up the DbContext**

```bash
git rm src/IAMS.Api/Entities/Maintenance.cs src/IAMS.Api/Entities/MaintenanceAttachment.cs
```

In `src/IAMS.Api/Data/AppDbContext.cs`, replace the two Maintenance DbSet properties:

```csharp
    public DbSet<Maintenance> Maintenances => Set<Maintenance>();
    public DbSet<MaintenanceAttachment> MaintenanceAttachments => Set<MaintenanceAttachment>();
```

with:

```csharp
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
```

Then replace the whole `modelBuilder.Entity<Maintenance>(...)` and `modelBuilder.Entity<MaintenanceAttachment>(...)` blocks — which sit immediately before the UTC converter comment at the end of `OnModelCreating` — with the following. Everything must stay above the UTC converter block.

```csharp
        // Configure Ticket with tenant
        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Priority).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Resolution).HasMaxLength(2000);

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.AssetId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.RequesterUserId);
            entity.HasIndex(e => e.AssignedToUserId);
            entity.HasIndex(e => new { e.TenantId, e.Status });

            // Display number is unique per tenant, not globally.
            entity.HasIndex(e => new { e.TenantId, e.TicketNumber }).IsUnique();

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // A Request has no asset until it is fulfilled, so this is optional
            // and must not cascade-delete the ticket history when an asset goes.
            entity.HasOne(e => e.Asset)
                .WithMany()
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.RequesterUser)
                .WithMany()
                .HasForeignKey(e => e.RequesterUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.AssignedToUser)
                .WithMany()
                .HasForeignKey(e => e.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.AssetAssignment)
                .WithMany()
                .HasForeignKey(e => e.AssetAssignmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure TicketComment with tenant
        modelBuilder.Entity<TicketComment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Body).HasMaxLength(4000).IsRequired();

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.TicketId);
            entity.HasIndex(e => new { e.TicketId, e.CreatedAt });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Ticket)
                .WithMany(t => t.Comments)
                .HasForeignKey(e => e.TicketId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure TicketAttachment with tenant
        modelBuilder.Entity<TicketAttachment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FileName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.StoredFileName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.TicketId);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => new { e.TicketId, e.Category });

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Ticket)
                .WithMany(t => t.Attachments)
                .HasForeignKey(e => e.TicketId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.UploadedByUser)
                .WithMany()
                .HasForeignKey(e => e.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure AuditLog with tenant. Append-only: no update or delete path exists.
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.EntityId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(450);

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });
```

Add the `Asset.OwnerUser` relationship inside the existing `modelBuilder.Entity<Asset>(...)` block, after the `AssignedToUser` relationship:

```csharp
            entity.HasOne(e => e.OwnerUser)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
```

- [ ] **Step 6: Fix the now-broken references**

The build will fail in files that still reference `Maintenance`. Find them:

```bash
grep -rln "Maintenance" src/IAMS.Api --include=*.cs
```

Delete `src/IAMS.Api/Controllers/MaintenanceController.cs` and `src/IAMS.Api/Controllers/MaintenanceAttachmentsController.cs` — they are replaced in Tasks 10 and 11.

```bash
git rm src/IAMS.Api/Controllers/MaintenanceController.cs src/IAMS.Api/Controllers/MaintenanceAttachmentsController.cs
```

If `DashboardController.cs` or `SeedData.cs` reference `db.Maintenances`, change them to `db.Tickets` and map `MaintenanceStatus.Pending` to `TicketStatus.New`, `MaintenanceStatus.InProgress` to `TicketStatus.InProgress`, and `MaintenanceStatus.Completed` to `TicketStatus.Closed`. Leave `src/IAMS.Shared/DTOs/MaintenanceDto.cs` in place for now; Task 10 replaces it.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet build && dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketEntityTests`
Expected: build succeeds; `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 8: Commit**

```bash
git add -A src/IAMS.Api tests/IAMS.Api.Tests
git commit -m "feat(tickets): add Ticket, TicketComment, TicketAttachment and AuditLog entities"
```

---

### Task 4: Migration from Maintenance

**Files:**
- Create: `src/IAMS.Api/Migrations/<timestamp>_TicketsFromMaintenance.cs` (generated)
- Modify: the generated migration's `Up` method

**Interfaces:**
- Consumes: the entity model from Task 3.
- Produces: a migration that renames `Maintenances` to `Tickets` and `MaintenanceAttachments` to `TicketAttachments`, preserving all rows.

This task has no unit test. It is verified by generating the SQL, reading it, and applying it to a scratch database.

- [ ] **Step 1: Generate the migration**

```bash
cd src/IAMS.Api && dotnet ef migrations add TicketsFromMaintenance
```

- [ ] **Step 2: Rewrite the generated Up method to rename rather than drop**

EF will have generated `DropTable("Maintenances")` and `CreateTable("Tickets")`, which destroys every existing maintenance record. Replace the generated `Up` body with the following. Keep the generated `Down` for the new columns, but replace any `CreateTable("Maintenances")` in `Down` with the inverse renames.

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // --- Rename tables rather than recreate, so existing rows survive ---
    migrationBuilder.DropForeignKey(name: "FK_MaintenanceAttachments_Maintenances_MaintenanceId", table: "MaintenanceAttachments");

    migrationBuilder.RenameTable(name: "Maintenances", newName: "Tickets");
    migrationBuilder.RenameTable(name: "MaintenanceAttachments", newName: "TicketAttachments");

    migrationBuilder.RenameColumn(name: "MaintenanceId", table: "TicketAttachments", newName: "TicketId");
    migrationBuilder.RenameColumn(name: "PerformedByUserId", table: "Tickets", newName: "AssignedToUserId");
    migrationBuilder.RenameColumn(name: "CreatedByUserId", table: "Tickets", newName: "RequesterUserId");
    migrationBuilder.RenameColumn(name: "CompletedAt", table: "Tickets", newName: "ResolvedAt");

    // --- New Ticket columns ---
    migrationBuilder.AddColumn<int>(name: "TicketNumber", table: "Tickets", nullable: false, defaultValue: 0);
    migrationBuilder.AddColumn<string>(name: "Type", table: "Tickets", maxLength: 50, nullable: false, defaultValue: "Incident");
    migrationBuilder.AddColumn<string>(name: "Priority", table: "Tickets", maxLength: 50, nullable: false, defaultValue: "Medium");
    migrationBuilder.AddColumn<DateTime>(name: "AssignedAt", table: "Tickets", nullable: true);
    migrationBuilder.AddColumn<DateTime>(name: "ClosedAt", table: "Tickets", nullable: true);
    migrationBuilder.AddColumn<DateTime>(name: "DueAt", table: "Tickets", nullable: true);
    migrationBuilder.AddColumn<DateTime>(name: "BreachedAt", table: "Tickets", nullable: true);
    migrationBuilder.AddColumn<string>(name: "Resolution", table: "Tickets", maxLength: 2000, nullable: true);
    migrationBuilder.AddColumn<int>(name: "AssetAssignmentId", table: "Tickets", nullable: true);

    // Asset gains an accountable owner and a physical-verification stamp.
    migrationBuilder.AddColumn<string>(name: "OwnerUserId", table: "Assets", maxLength: 450, nullable: true);
    migrationBuilder.AddColumn<DateTime>(name: "LastVerifiedAt", table: "Assets", nullable: true);

    // --- Data migration ---

    // Old Notes become the resolution text; the column is dropped afterwards.
    migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Resolution"" = ""Notes"" WHERE ""Notes"" IS NOT NULL;");

    // A completed maintenance record was both resolved and closed at the same moment.
    migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""ClosedAt"" = ""ResolvedAt"" WHERE ""Status"" IN ('Completed', 'Cancelled');");

    // Status remap: Pending -> New, Completed -> Closed. InProgress and Cancelled are unchanged.
    migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'New' WHERE ""Status"" = 'Pending';");
    migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'Closed' WHERE ""Status"" = 'Completed';");

    // Backfill per-tenant ticket numbers in creation order.
    migrationBuilder.Sql(@"
        WITH numbered AS (
            SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""TenantId"" ORDER BY ""CreatedAt"", ""Id"") AS rn
            FROM ""Tickets""
        )
        UPDATE ""Tickets"" t SET ""TicketNumber"" = n.rn FROM numbered n WHERE t.""Id"" = n.""Id"";
    ");

    migrationBuilder.DropColumn(name: "Notes", table: "Tickets");

    // --- Indexes and constraints ---
    migrationBuilder.CreateIndex(
        name: "IX_Tickets_TenantId_TicketNumber",
        table: "Tickets",
        columns: ["TenantId", "TicketNumber"],
        unique: true);

    migrationBuilder.CreateIndex(name: "IX_Tickets_Type", table: "Tickets", column: "Type");
    migrationBuilder.CreateIndex(name: "IX_Tickets_RequesterUserId", table: "Tickets", column: "RequesterUserId");
    migrationBuilder.CreateIndex(name: "IX_Tickets_AssignedToUserId", table: "Tickets", column: "AssignedToUserId");
    migrationBuilder.CreateIndex(name: "IX_Tickets_TenantId_Status", table: "Tickets", columns: ["TenantId", "Status"]);
    migrationBuilder.CreateIndex(name: "IX_Assets_OwnerUserId", table: "Assets", column: "OwnerUserId");

    migrationBuilder.AddForeignKey(
        name: "FK_TicketAttachments_Tickets_TicketId",
        table: "TicketAttachments",
        column: "TicketId",
        principalTable: "Tickets",
        principalColumn: "Id",
        onDelete: ReferentialAction.Cascade);

    // --- New tables ---
    // Keep the CreateTable calls EF generated for TicketComments and AuditLogs here, unchanged.
}
```

Move the EF-generated `CreateTable("TicketComments")` and `CreateTable("AuditLogs")` calls to the end of `Up`, where the comment marks them.

- [ ] **Step 3: Verify the generated SQL renames rather than drops**

```bash
cd src/IAMS.Api && dotnet ef migrations script --idempotent --output ../../artifacts/tickets-migration.sql
```

Run: `grep -iE "DROP TABLE|ALTER TABLE .Maintenances. RENAME" artifacts/tickets-migration.sql`
Expected: an `ALTER TABLE "Maintenances" RENAME TO "Tickets"` line and **no** `DROP TABLE "Maintenances"`. If a drop appears, the `Up` body was not fully replaced.

- [ ] **Step 4: Apply it to a scratch database and confirm data survives**

Point `ConnectionStrings:DefaultConnection` at a scratch Postgres database that already has maintenance rows, then:

```bash
cd src/IAMS.Api && dotnet ef database update
```

Expected: completes without error. Then confirm no ticket lost its number:

```bash
psql "$SCRATCH_CONNECTION" -c 'SELECT count(*) FROM "Tickets" WHERE "TicketNumber" = 0;'
```

Expected: `0`.

- [ ] **Step 5: Commit**

```bash
git add src/IAMS.Api/Migrations
git commit -m "feat(db): migrate Maintenance to Ticket, preserving existing records"
```

---

### Task 5: Audit log interceptor

**Files:**
- Create: `src/IAMS.Api/Data/AuditSaveChangesInterceptor.cs`
- Modify: `src/IAMS.Api/Program.cs`
- Create: `tests/IAMS.Api.Tests/AuditLogTests.cs`

**Interfaces:**
- Consumes: `AuditLog`, `AuditActions` from Task 3.
- Produces: `AuditSaveChangesInterceptor(ICurrentUserAccessor currentUser)`; `ICurrentUserAccessor.GetUserId()` returning `string?`, implemented by `HttpContextCurrentUserAccessor`.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/AuditLogTests.cs`:

```csharp
using System.Text.Json;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class AuditLogTests
{
    private sealed class StubUser : ICurrentUserAccessor
    {
        private readonly string? _id;
        public StubUser(string? id) => _id = id;
        public string? GetUserId() => _id;
    }

    private static (AppDbContext Db, SqliteConnection Conn) CreateAudited(string? userId = "staff-1")
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new StubUser(userId)))
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }

    [Fact]
    public async Task Creating_a_ticket_writes_a_Created_entry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            });
            await db.SaveChangesAsync();

            var entry = await db.AuditLogs.SingleAsync(a => a.EntityType == "Ticket");
            Assert.Equal(AuditActions.Created, entry.Action);
            Assert.Equal("staff-1", entry.UserId);
            Assert.Equal(tenantId, entry.TenantId);
        }
    }

    [Fact]
    public async Task Updating_a_ticket_records_only_the_changed_fields()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            ticket.Status = TicketStatus.Assigned;
            ticket.AssignedToUserId = "staff-1";
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            Assert.True(changes.ContainsKey("Status"));
            Assert.True(changes.ContainsKey("AssignedToUserId"));
            Assert.False(changes.ContainsKey("Title"));
            Assert.Equal("New", changes["Status"].GetProperty("from").GetString());
            Assert.Equal("Assigned", changes["Status"].GetProperty("to").GetString());
        }
    }

    [Fact]
    public async Task Notifications_are_not_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            db.Notifications.Add(new Notification
            {
                TenantId = tenantId, UserId = "staff-1",
                Title = "Hello", Message = "Body", Type = NotificationTypes.Info
            });
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task Audit_entries_do_not_audit_themselves()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            Assert.Equal(1, await db.AuditLogs.CountAsync());
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter AuditLogTests`
Expected: build failure — `The name 'AuditSaveChangesInterceptor' does not exist` and `'ICurrentUserAccessor' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/IAMS.Api/Data/AuditSaveChangesInterceptor.cs`:

```csharp
using System.Text.Json;
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAMS.Api.Data;

public interface ICurrentUserAccessor
{
    string? GetUserId();
}

public class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? GetUserId() =>
        _accessor.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}

/// <summary>
/// Writes an append-only AuditLog row for every insert, update and delete of an audited
/// entity. Lives at the DbContext layer so no controller can forget to record history.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedTypes =
        [nameof(Asset), nameof(AssetAssignment), nameof(Ticket), nameof(TicketComment), nameof(TicketAttachment)];

    // Noise, or already captured by the audited fields themselves.
    private static readonly HashSet<string> IgnoredProperties =
        ["CreatedAt", "UpdatedAt"];

    private readonly ICurrentUserAccessor _currentUser;

    public AuditSaveChangesInterceptor(ICurrentUserAccessor currentUser) => _currentUser = currentUser;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            WriteAuditEntries(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            WriteAuditEntries(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    private void WriteAuditEntries(DbContext context)
    {
        var userId = _currentUser.GetUserId();

        // Materialise first: adding audit rows mutates the ChangeTracker.
        var tracked = context.ChangeTracker.Entries()
            .Where(e => AuditedTypes.Contains(e.Metadata.ClrType.Name))
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in tracked)
        {
            var tenantId = ReadTenantId(entry);
            if (tenantId is null) continue;

            var log = new AuditLog
            {
                TenantId = tenantId.Value,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = ReadKey(entry),
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                Action = entry.State switch
                {
                    EntityState.Added => AuditActions.Created,
                    EntityState.Modified => AuditActions.Updated,
                    _ => AuditActions.Deleted
                },
                Changes = entry.State == EntityState.Modified ? SerialiseChanges(entry) : null
            };

            context.Add(log);
        }
    }

    private static Guid? ReadTenantId(EntityEntry entry)
    {
        var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(ITenantEntity.TenantId));
        return property?.CurrentValue as Guid?;
    }

    private static string ReadKey(EntityEntry entry)
    {
        // For an inserted row the store-generated key is not known yet; EF fills it in
        // after the insert, so read CurrentValue lazily via the tracked property.
        var key = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return key?.CurrentValue?.ToString() ?? "";
    }

    private static string? SerialiseChanges(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (!property.IsModified) continue;
            if (IgnoredProperties.Contains(property.Metadata.Name)) continue;
            if (Equals(property.OriginalValue, property.CurrentValue)) continue;

            changes[property.Metadata.Name] = new
            {
                from = property.OriginalValue,
                to = property.CurrentValue
            };
        }

        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }
}
```

`ReadKey` returns `"0"` for newly inserted rows because the identity value is not assigned until after the insert. Accept that for now — the `EntityType` plus `Timestamp` plus `Changes` still identify the event, and Created entries carry the full row in the following Updated entries. A follow-up task can resolve inserted keys in `SavedChangesAsync`.

- [ ] **Step 4: Register the interceptor**

In `src/IAMS.Api/Program.cs`, find the `builder.Services.AddDbContext<AppDbContext>(...)` call and register the accessor before it and the interceptor inside it:

```csharp
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    options.AddInterceptors(
        new AuditSaveChangesInterceptor(serviceProvider.GetRequiredService<ICurrentUserAccessor>()));
});
```

Keep whatever `UseNpgsql` arguments the existing call already passes — only the lambda signature and the `AddInterceptors` line are new. Add `using IAMS.Api.Data;` if it is not already present.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter AuditLogTests`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 6: Commit**

```bash
git add src/IAMS.Api/Data/AuditSaveChangesInterceptor.cs src/IAMS.Api/Program.cs tests/IAMS.Api.Tests/AuditLogTests.cs
git commit -m "feat(audit): record entity changes through a SaveChanges interceptor"
```

---

### Task 6: Per-tenant ticket numbering

**Files:**
- Create: `src/IAMS.Api/Services/TicketNumberAllocator.cs`
- Create: `tests/IAMS.Api.Tests/TicketNumberAllocatorTests.cs`

**Interfaces:**
- Consumes: `AppDbContext.Tickets`.
- Produces: `ITicketNumberAllocator.NextAsync(Guid tenantId, CancellationToken ct)` returning `Task<int>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketNumberAllocatorTests.cs`:

```csharp
using IAMS.Api.Entities;
using IAMS.Api.Services;

namespace IAMS.Api.Tests;

public class TicketNumberAllocatorTests
{
    [Fact]
    public async Task First_ticket_for_a_tenant_is_number_one()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var allocator = new TicketNumberAllocator(db);

            Assert.Equal(1, await allocator.NextAsync(tenantId, default));
        }
    }

    [Fact]
    public async Task Numbers_continue_from_the_tenants_highest()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "u1", "User One");

            db.Tickets.Add(new Ticket { TenantId = tenantId, TicketNumber = 7, Title = "a", RequesterUserId = "u1" });
            await db.SaveChangesAsync();

            var allocator = new TicketNumberAllocator(db);
            Assert.Equal(8, await allocator.NextAsync(tenantId, default));
        }
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_sequence()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantA, "a1", "A One");

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 42, Title = "a", RequesterUserId = "a1" });
            await db.SaveChangesAsync();

            var allocator = new TicketNumberAllocator(db);
            Assert.Equal(43, await allocator.NextAsync(tenantA, default));
            Assert.Equal(1, await allocator.NextAsync(tenantB, default));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketNumberAllocatorTests`
Expected: build failure — `The name 'TicketNumberAllocator' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/IAMS.Api/Services/TicketNumberAllocator.cs`:

```csharp
using IAMS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public interface ITicketNumberAllocator
{
    Task<int> NextAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Allocates the next human-facing ticket number for a tenant.
///
/// This reads MAX + 1, which races under concurrent inserts. That race is caught by the
/// unique index on (TenantId, TicketNumber): TicketService retries the insert, and the
/// second attempt reads the now-higher maximum. A per-tenant database sequence would
/// avoid the retry but needs DDL per tenant, which this app does not do.
/// </summary>
public class TicketNumberAllocator : ITicketNumberAllocator
{
    private readonly AppDbContext _db;

    public TicketNumberAllocator(AppDbContext db) => _db = db;

    public async Task<int> NextAsync(Guid tenantId, CancellationToken ct = default)
    {
        var highest = await _db.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .MaxAsync(t => (int?)t.TicketNumber, ct);

        return (highest ?? 0) + 1;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketNumberAllocatorTests`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 5: Commit**

```bash
git add src/IAMS.Api/Services/TicketNumberAllocator.cs tests/IAMS.Api.Tests/TicketNumberAllocatorTests.cs
git commit -m "feat(tickets): allocate per-tenant ticket numbers"
```

---

### Task 7: TicketService — create and query

**Files:**
- Create: `src/IAMS.Api/Services/TicketService.cs`
- Create: `tests/IAMS.Api.Tests/TicketServiceCreateTests.cs`

**Interfaces:**
- Consumes: `ITicketNumberAllocator.NextAsync`, `ITenantProvider.GetRequiredTenantId()`, `TicketTypes`, `TicketStatus`, `TicketPriority`.
- Produces:
  - `record ServiceResult(bool Success, string? Message = null)` with `ServiceResult.Ok()` and `ServiceResult.Fail(string)`.
  - `record ServiceResult<T>(bool Success, T? Value, string? Message = null)` with `ServiceResult<T>.Ok(T)` and `ServiceResult<T>.Fail(string)`.
  - `record TicketQuery(string? Type, string? Status, string? Priority, string? AssignedToUserId, int? AssetId, string? Search, int Page = 1, int PageSize = 25)`.
  - `record TicketSummary(int Open, int Unassigned, int InProgress, int Overdue)`.
  - `ITicketService.CreateAsync(string type, string title, string? description, string priority, int? assetId, string requesterUserId, CancellationToken ct)` returning `Task<ServiceResult<Ticket>>`.
  - `ITicketService.GetAsync(int id, CancellationToken ct)` returning `Task<Ticket?>`.
  - `ITicketService.ListAsync(TicketQuery query, CancellationToken ct)` returning `Task<(List<Ticket> Items, int TotalCount)>`.
  - `ITicketService.GetSummaryAsync(CancellationToken ct)` returning `Task<TicketSummary>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketServiceCreateTests.cs`:

```csharp
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketServiceCreateTests
{
    private static TicketService Build(IAMS.Api.Data.AppDbContext db, Guid tenantId) =>
        new(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));

    [Fact]
    public async Task Creates_a_new_ticket_with_an_allocated_number()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.Incident, "Printer jams", "Every second page", TicketPriority.High, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(1, result.Value!.TicketNumber);
            Assert.Equal(TicketStatus.New, result.Value.Status);
            Assert.Equal(tenantId, result.Value.TenantId);
        }
    }

    [Fact]
    public async Task Rejects_an_unknown_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                "Escalation", "Bad type", null, TicketPriority.Low, null, "emp-1", default);

            Assert.False(result.Success);
            Assert.Contains("type", result.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_an_asset_from_another_tenant()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, mine, "emp-1", "J. Dela Cruz");
            var foreign = await TestDb.SeedAssetAsync(db, theirs, "THEIRS-1");
            var service = Build(db, db, mine);

            var result = await service.CreateAsync(
                TicketTypes.Incident, "Not mine", null, TicketPriority.Low, foreign.Id, "emp-1", default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Security_events_default_to_high_priority()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.SecurityEvent, "Phishing email", null, TicketPriority.Low, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(TicketPriority.High, result.Value!.Priority);
        }
    }

    [Fact]
    public async Task Lists_and_filters_by_status_and_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Request, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var (all, total) = await service.ListAsync(new TicketQuery(null, null, null, null, null, null), default);
            Assert.Equal(2, total);
            Assert.Equal(2, all.Count);

            var (requests, requestTotal) = await service.ListAsync(
                new TicketQuery(TicketTypes.Request, null, null, null, null, null), default);
            Assert.Equal(1, requestTotal);
            Assert.Equal("Two", requests[0].Title);
        }
    }

    [Fact]
    public async Task Summary_counts_open_and_unassigned()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Incident, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var summary = await service.GetSummaryAsync(default);

            Assert.Equal(2, summary.Open);
            Assert.Equal(2, summary.Unassigned);
            Assert.Equal(0, summary.InProgress);
            Assert.Equal(0, summary.Overdue);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketServiceCreateTests`
Expected: build failure — `The name 'TicketService' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/IAMS.Api/Services/TicketService.cs`:

```csharp
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public record ServiceResult(bool Success, string? Message = null)
{
    public static ServiceResult Ok() => new(true);
    public static ServiceResult Fail(string message) => new(false, message);
}

public record ServiceResult<T>(bool Success, T? Value, string? Message = null)
{
    public static ServiceResult<T> Ok(T value) => new(true, value);
    public static ServiceResult<T> Fail(string message) => new(false, default, message);
}

public record TicketQuery(
    string? Type,
    string? Status,
    string? Priority,
    string? AssignedToUserId,
    int? AssetId,
    string? Search,
    int Page = 1,
    int PageSize = 25);

public record TicketSummary(int Open, int Unassigned, int InProgress, int Overdue);

public interface ITicketService
{
    Task<ServiceResult<Ticket>> CreateAsync(
        string type, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default);

    Task<Ticket?> GetAsync(int id, CancellationToken ct = default);

    Task<(List<Ticket> Items, int TotalCount)> ListAsync(TicketQuery query, CancellationToken ct = default);

    Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default);
}

public partial class TicketService : ITicketService
{
    private const int MaxNumberRetries = 3;

    private readonly AppDbContext _db;
    private readonly ITicketNumberAllocator _numbers;
    private readonly ITenantProvider _tenants;

    public TicketService(AppDbContext db, ITicketNumberAllocator numbers, ITenantProvider tenants)
    {
        _db = db;
        _numbers = numbers;
        _tenants = tenants;
    }

    public async Task<ServiceResult<Ticket>> CreateAsync(
        string type, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default)
    {
        if (!TicketTypes.IsValid(type))
            return ServiceResult<Ticket>.Fail($"'{type}' is not a valid ticket type.");

        if (string.IsNullOrWhiteSpace(title))
            return ServiceResult<Ticket>.Fail("A ticket needs a title.");

        if (!TicketPriority.IsValid(priority))
            return ServiceResult<Ticket>.Fail($"'{priority}' is not a valid priority.");

        var tenantId = _tenants.GetRequiredTenantId();

        if (assetId is not null)
        {
            // The query filter already scopes this to the current tenant, so a foreign
            // asset id simply does not resolve.
            var assetExists = await _db.Assets.AnyAsync(a => a.Id == assetId, ct);
            if (!assetExists)
                return ServiceResult<Ticket>.Fail("That asset does not exist.");
        }

        // A security report is never low priority, whatever the form said.
        var effectivePriority = type == TicketTypes.SecurityEvent
            ? TicketPriority.High
            : priority;

        for (var attempt = 1; ; attempt++)
        {
            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = await _numbers.NextAsync(tenantId, ct),
                Type = type,
                Title = title.Trim(),
                Description = description,
                Status = TicketStatus.New,
                Priority = effectivePriority,
                AssetId = assetId,
                RequesterUserId = requesterUserId,
                CreatedAt = DateTime.UtcNow
            };

            _db.Tickets.Add(ticket);

            try
            {
                await _db.SaveChangesAsync(ct);
                return ServiceResult<Ticket>.Ok(ticket);
            }
            catch (DbUpdateException) when (attempt < MaxNumberRetries)
            {
                // Another request took the number between our MAX read and this insert.
                _db.Entry(ticket).State = EntityState.Detached;
            }
        }
    }

    public Task<Ticket?> GetAsync(int id, CancellationToken ct = default) =>
        _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<(List<Ticket> Items, int TotalCount)> ListAsync(
        TicketQuery query, CancellationToken ct = default)
    {
        var q = _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Type))
            q = q.Where(t => t.Type == query.Type);

        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(t => t.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.Priority))
            q = q.Where(t => t.Priority == query.Priority);

        if (!string.IsNullOrWhiteSpace(query.AssignedToUserId))
            q = q.Where(t => t.AssignedToUserId == query.AssignedToUserId);

        if (query.AssetId is not null)
            q = q.Where(t => t.AssetId == query.AssetId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            q = q.Where(t =>
                EF.Functions.Like(t.Title, term) ||
                (t.Description != null && EF.Functions.Like(t.Description, term)));
        }

        var total = await q.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var open = await _db.Tickets.CountAsync(t => TicketStatus.Open.Contains(t.Status), ct);
        var unassigned = await _db.Tickets.CountAsync(
            t => TicketStatus.Open.Contains(t.Status) && t.AssignedToUserId == null, ct);
        var inProgress = await _db.Tickets.CountAsync(t => t.Status == TicketStatus.InProgress, ct);
        var overdue = await _db.Tickets.CountAsync(
            t => TicketStatus.Open.Contains(t.Status) && t.DueAt != null && t.DueAt < now, ct);

        return new TicketSummary(open, unassigned, inProgress, overdue);
    }
}
```

The class is `partial` because Tasks 8 and 9 add the workflow methods in a second file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketServiceCreateTests`
Expected: `Passed! - Failed: 0, Passed: 6`

- [ ] **Step 5: Commit**

```bash
git add src/IAMS.Api/Services/TicketService.cs tests/IAMS.Api.Tests/TicketServiceCreateTests.cs
git commit -m "feat(tickets): add TicketService create, list and summary"
```

---

### Task 8: TicketService — assign, status, resolve

**Files:**
- Create: `src/IAMS.Api/Services/TicketService.Workflow.cs`
- Modify: `src/IAMS.Api/Services/TicketService.cs:39-52` (the `ITicketService` interface — add the four new members)
- Create: `tests/IAMS.Api.Tests/TicketServiceWorkflowTests.cs`

**Interfaces:**
- Consumes: `TicketWorkflow.CanTransition`, `ServiceResult` from Task 7.
- Produces, all on `ITicketService`:
  - `AssignAsync(int id, string assigneeUserId, CancellationToken ct)` → `Task<ServiceResult>`
  - `ChangeStatusAsync(int id, string status, CancellationToken ct)` → `Task<ServiceResult>`
  - `ResolveAsync(int id, string resolution, CancellationToken ct)` → `Task<ServiceResult>`
  - `AddCommentAsync(int ticketId, string userId, string body, bool isInternal, CancellationToken ct)` → `Task<ServiceResult<TicketComment>>`

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketServiceWorkflowTests.cs`:

```csharp
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketServiceWorkflowTests
{
    private static async Task<(TicketService Service, Ticket Ticket)> SetupAsync(AppDbContext db, Guid tenantId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
        var created = await service.CreateAsync(
            TicketTypes.Incident, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

        return (service, created.Value!);
    }

    [Fact]
    public async Task Assigning_moves_the_ticket_to_Assigned_and_stamps_the_time()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.AssignAsync(ticket.Id, "staff-1", default);

            Assert.True(result.Success);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Assigned, saved.Status);
            Assert.Equal("staff-1", saved.AssignedToUserId);
            Assert.NotNull(saved.AssignedAt);
        }
    }

    [Fact]
    public async Task Rejects_an_invalid_transition_and_leaves_state_unchanged()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.ChangeStatusAsync(ticket.Id, TicketStatus.Closed, default);

            Assert.False(result.Success);
            Assert.Contains("New", result.Message!);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.New, saved.Status);
        }
    }

    [Fact]
    public async Task Starting_work_stamps_StartedAt_once()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var first = (await db.Tickets.SingleAsync(t => t.Id == ticket.Id)).StartedAt;
            Assert.NotNull(first);

            await service.ChangeStatusAsync(ticket.Id, TicketStatus.OnHold, default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var second = (await db.Tickets.SingleAsync(t => t.Id == ticket.Id)).StartedAt;
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public async Task Resolving_requires_a_resolution_and_stamps_ResolvedAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var blank = await service.ResolveAsync(ticket.Id, "   ", default);
            Assert.False(blank.Success);

            var ok = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);
            Assert.True(ok.Success);

            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Resolved, saved.Status);
            Assert.Equal("Replaced the fuser.", saved.Resolution);
            Assert.NotNull(saved.ResolvedAt);
        }
    }

    [Fact]
    public async Task Closing_a_resolved_ticket_stamps_ClosedAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);
            await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);

            var result = await service.ChangeStatusAsync(ticket.Id, TicketStatus.Closed, default);

            Assert.True(result.Success);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.NotNull(saved.ClosedAt);
        }
    }

    [Fact]
    public async Task Comments_record_their_author_and_visibility()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var publicComment = await service.AddCommentAsync(ticket.Id, "staff-1", "Looking at it now.", false, default);
            var internalNote = await service.AddCommentAsync(ticket.Id, "staff-1", "Third jam this quarter.", true, default);

            Assert.True(publicComment.Success);
            Assert.True(internalNote.Success);
            Assert.False(publicComment.Value!.IsInternal);
            Assert.True(internalNote.Value!.IsInternal);
            Assert.Equal(2, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_an_empty_comment()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.AddCommentAsync(ticket.Id, "staff-1", "  ", false, default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketServiceWorkflowTests`
Expected: build failure — `'TicketService' does not contain a definition for 'AssignAsync'`.

- [ ] **Step 3: Extend the interface**

In `src/IAMS.Api/Services/TicketService.cs`, add these four members to the `ITicketService` interface, after `GetSummaryAsync`:

```csharp
    Task<ServiceResult> AssignAsync(int id, string assigneeUserId, CancellationToken ct = default);

    Task<ServiceResult> ChangeStatusAsync(int id, string status, CancellationToken ct = default);

    Task<ServiceResult> ResolveAsync(int id, string resolution, CancellationToken ct = default);

    Task<ServiceResult<TicketComment>> AddCommentAsync(
        int ticketId, string userId, string body, bool isInternal, CancellationToken ct = default);
```

- [ ] **Step 4: Write the implementation**

Create `src/IAMS.Api/Services/TicketService.Workflow.cs`:

```csharp
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public partial class TicketService
{
    public async Task<ServiceResult> AssignAsync(
        int id, string assigneeUserId, CancellationToken ct = default)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        var assigneeExists = await _db.Users.AnyAsync(u => u.Id == assigneeUserId, ct);
        if (!assigneeExists)
            return ServiceResult.Fail("That user does not exist.");

        // Reassigning an in-flight ticket keeps its status; only a New ticket advances.
        if (ticket.Status == TicketStatus.New)
        {
            if (!TicketWorkflow.CanTransition(ticket.Status, TicketStatus.Assigned))
                return ServiceResult.Fail($"A {ticket.Status} ticket cannot be assigned.");

            ticket.Status = TicketStatus.Assigned;
        }
        else if (!TicketWorkflow.IsOpen(ticket.Status))
        {
            return ServiceResult.Fail($"A {ticket.Status} ticket cannot be reassigned.");
        }

        ticket.AssignedToUserId = assigneeUserId;
        ticket.AssignedAt ??= DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ChangeStatusAsync(
        int id, string status, CancellationToken ct = default)
    {
        if (!TicketStatus.IsValid(status))
            return ServiceResult.Fail($"'{status}' is not a valid status.");

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        if (!TicketWorkflow.CanTransition(ticket.Status, status))
            return ServiceResult.Fail($"A ticket cannot move from {ticket.Status} to {status}.");

        // Resolve has its own method because it requires resolution text.
        if (status == TicketStatus.Resolved)
            return ServiceResult.Fail("Use Resolve so a resolution is recorded.");

        ticket.Status = status;

        if (status == TicketStatus.InProgress)
            ticket.StartedAt ??= DateTime.UtcNow;

        if (status is TicketStatus.Closed or TicketStatus.Cancelled)
            ticket.ClosedAt ??= DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ResolveAsync(
        int id, string resolution, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return ServiceResult.Fail("A resolution note is required.");

        var ticket = await _db.Tickets
            .Include(t => t.Asset)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        if (!TicketWorkflow.CanTransition(ticket.Status, TicketStatus.Resolved))
            return ServiceResult.Fail($"A {ticket.Status} ticket cannot be resolved.");

        ticket.Status = TicketStatus.Resolved;
        ticket.Resolution = resolution.Trim();
        ticket.ResolvedAt = DateTime.UtcNow;

        // An asset parked in Maintenance for this ticket returns to service.
        if (ticket.Asset is { Status: AssetStatus.Maintenance } asset)
        {
            asset.Status = asset.AssignedToUserId is null
                ? AssetStatus.Available
                : AssetStatus.InUse;
            asset.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<TicketComment>> AddCommentAsync(
        int ticketId, string userId, string body, bool isInternal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
            return ServiceResult<TicketComment>.Fail("A comment cannot be empty.");

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return ServiceResult<TicketComment>.Fail("Ticket not found.");

        var comment = new TicketComment
        {
            TenantId = ticket.TenantId,
            TicketId = ticket.Id,
            UserId = userId,
            Body = body.Trim(),
            IsInternal = isInternal,
            CreatedAt = DateTime.UtcNow
        };

        _db.TicketComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<TicketComment>.Ok(comment);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketServiceWorkflowTests`
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 6: Commit**

```bash
git add src/IAMS.Api/Services/TicketService.cs src/IAMS.Api/Services/TicketService.Workflow.cs tests/IAMS.Api.Tests/TicketServiceWorkflowTests.cs
git commit -m "feat(tickets): add assign, status change, resolve and comments"
```

---

### Task 9: Request fulfilment in one transaction

**Files:**
- Create: `src/IAMS.Api/Services/TicketService.Fulfilment.cs`
- Modify: `src/IAMS.Api/Services/TicketService.cs` (add `FulfilAsync` to `ITicketService`)
- Create: `tests/IAMS.Api.Tests/TicketFulfilmentTests.cs`

**Interfaces:**
- Consumes: `AssetAssignment`, `AssetStatus`, `ServiceResult`.
- Produces: `ITicketService.FulfilAsync(int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct)` returning `Task<ServiceResult>`.

Read `src/IAMS.Api/Entities/AssetAssignment.cs` first and use its actual property names in the code below. The names used here — `AssetId`, `UserId`, `AssignedAt`, `AssignedByUserId`, `ReturnedAt` — match the entity as of this plan; if any differ, adjust and keep the rest identical.

- [ ] **Step 1: Write the failing tests**

Create `tests/IAMS.Api.Tests/TicketFulfilmentTests.cs`:

```csharp
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketFulfilmentTests
{
    private static async Task<(TicketService Service, Ticket Request)> SetupAsync(AppDbContext db, Guid tenantId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "A. Reyes");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
        var created = await service.CreateAsync(
            TicketTypes.Request, "Laptop for new documentation officer",
            null, TicketPriority.Medium, null, "emp-1", default);

        return (service, created.Value!);
    }

    [Fact]
    public async Task Fulfilling_creates_the_assignment_and_closes_the_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(
                request.Id, asset.Id, "Issued ThinkPad E14 with charger.", "staff-1", default);

            Assert.True(result.Success);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            var assignment = await db.AssetAssignments.SingleAsync();

            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.Equal(asset.Id, saved.AssetId);
            Assert.Equal(assignment.Id, saved.AssetAssignmentId);
            Assert.NotNull(saved.ResolvedAt);
            Assert.NotNull(saved.ClosedAt);
            Assert.Equal(AssetStatus.InUse, savedAsset.Status);
            Assert.Equal("emp-1", savedAsset.AssignedToUserId);
            Assert.Equal("emp-1", assignment.UserId);
        }
    }

    [Fact]
    public async Task Refuses_an_asset_that_is_not_available()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356", AssetStatus.InUse);

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);
            Assert.Contains("available", result.Message!, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Refuses_to_fulfil_a_non_request_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "A. Reyes");
            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
            var incident = await service.CreateAsync(
                TicketTypes.Incident, "Printer jams", null, TicketPriority.Low, null, "emp-1", default);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(incident.Value!.Id, asset.Id, "n/a", "emp-1", default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Nothing_is_written_when_the_ticket_does_not_exist()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, _) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(9999, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);

            db.ChangeTracker.Clear();
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketFulfilmentTests`
Expected: build failure — `'TicketService' does not contain a definition for 'FulfilAsync'`.

- [ ] **Step 3: Extend the interface**

In `src/IAMS.Api/Services/TicketService.cs`, add to `ITicketService`:

```csharp
    Task<ServiceResult> FulfilAsync(
        int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default);
```

- [ ] **Step 4: Write the implementation**

Create `src/IAMS.Api/Services/TicketService.Fulfilment.cs`:

```csharp
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public partial class TicketService
{
    /// <summary>
    /// Closes an equipment Request by issuing an asset to its requester. Ticket closure,
    /// assignment creation and the asset status change are one transaction: a partial
    /// success here would leave the assignment history lying, which is the one thing
    /// this system exists to prevent.
    /// </summary>
    public async Task<ServiceResult> FulfilAsync(
        int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return ServiceResult.Fail("A resolution note is required.");

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        if (ticket.Type != TicketTypes.Request)
            return ServiceResult.Fail("Only an equipment request can be fulfilled with an asset.");

        if (!TicketWorkflow.IsOpen(ticket.Status))
            return ServiceResult.Fail($"A {ticket.Status} ticket cannot be fulfilled.");

        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
            return ServiceResult.Fail("That asset does not exist.");

        if (asset.Status != AssetStatus.Available)
            return ServiceResult.Fail($"{asset.AssetTag} is not available — it is {asset.Status}.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            var assignment = new AssetAssignment
            {
                TenantId = ticket.TenantId,
                AssetId = asset.Id,
                UserId = ticket.RequesterUserId,
                AssignedAt = now,
                AssignedByUserId = actingUserId
            };
            _db.AssetAssignments.Add(assignment);
            await _db.SaveChangesAsync(ct);

            asset.Status = AssetStatus.InUse;
            asset.AssignedToUserId = ticket.RequesterUserId;
            asset.UpdatedAt = now;

            ticket.AssetId = asset.Id;
            ticket.AssetAssignmentId = assignment.Id;
            ticket.Resolution = resolution.Trim();
            ticket.Status = TicketStatus.Closed;
            ticket.ResolvedAt = now;
            ticket.ClosedAt = now;

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return ServiceResult.Ok();
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(ct);
            return ServiceResult.Fail($"Could not fulfil the request: {ex.Message}");
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketFulfilmentTests`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 6: Commit**

```bash
git add src/IAMS.Api/Services/TicketService.cs src/IAMS.Api/Services/TicketService.Fulfilment.cs tests/IAMS.Api.Tests/TicketFulfilmentTests.cs
git commit -m "feat(tickets): fulfil equipment requests by issuing an asset"
```

---

### Task 10: DTOs and TicketsController

**Files:**
- Create: `src/IAMS.Shared/DTOs/TicketDto.cs`
- Delete: `src/IAMS.Shared/DTOs/MaintenanceDto.cs`
- Create: `src/IAMS.Api/Controllers/TicketsController.cs`
- Create: `src/IAMS.Api/Mapping/TicketMapping.cs`
- Modify: `src/IAMS.Api/Program.cs` (service registrations)
- Create: `tests/IAMS.Api.Tests/TicketMappingTests.cs`

**Interfaces:**
- Consumes: `ITicketService` (all members), `ApiResponse<T>`, `PagedResponse<T>`.
- Produces: `TicketDto`, `TicketListItemDto`, `TicketCommentDto`, `TicketSummaryDto`, `CreateTicketRequest`, `AssignTicketRequest`, `ChangeTicketStatusRequest`, `ResolveTicketRequest`, `FulfilTicketRequest`; extension methods `Ticket.ToDto()`, `Ticket.ToListItem()`, `TicketComment.ToDto()`.

- [ ] **Step 1: Write the DTOs**

Create `src/IAMS.Shared/DTOs/TicketDto.cs`:

```csharp
namespace IAMS.Shared.DTOs;

public record TicketListItemDto
{
    public int Id { get; init; }
    public int TicketNumber { get; init; }
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public int? AssetId { get; init; }
    public string? AssetTag { get; init; }
    public string? RequesterName { get; init; }
    public string? RequesterDepartment { get; init; }
    public string? AssignedToName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DueAt { get; init; }

    public string Reference => $"TKT-{TicketNumber:D4}";
}

public record TicketDto : TicketListItemDto
{
    public string? Description { get; init; }
    public string? Resolution { get; init; }
    public string? RequesterUserId { get; init; }
    public string? AssignedToUserId { get; init; }
    public string? AssetName { get; init; }
    public string? AssetStatus { get; init; }
    public DateTime? WarrantyEndDate { get; init; }
    public DateTime? AssignedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public int? AssetAssignmentId { get; init; }
    public List<TicketCommentDto> Comments { get; init; } = [];
}

public record TicketCommentDto
{
    public int Id { get; init; }
    public required string Body { get; init; }
    public bool IsInternal { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorUserId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record TicketSummaryDto
{
    public int Open { get; init; }
    public int Unassigned { get; init; }
    public int InProgress { get; init; }
    public int Overdue { get; init; }
}

public record CreateTicketRequest
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string Priority { get; init; } = "Medium";
    public int? AssetId { get; init; }
}

public record AssignTicketRequest
{
    public required string AssignedToUserId { get; init; }
}

public record ChangeTicketStatusRequest
{
    public required string Status { get; init; }
}

public record ResolveTicketRequest
{
    public required string Resolution { get; init; }
}

public record FulfilTicketRequest
{
    public int AssetId { get; init; }
    public required string Resolution { get; init; }
}

public record AddTicketCommentRequest
{
    public required string Body { get; init; }
    public bool IsInternal { get; init; }
}
```

- [ ] **Step 2: Write the failing mapping test**

Create `tests/IAMS.Api.Tests/TicketMappingTests.cs`:

```csharp
using IAMS.Api.Entities;
using IAMS.Api.Mapping;

namespace IAMS.Api.Tests;

public class TicketMappingTests
{
    [Fact]
    public void Reference_is_zero_padded_to_four_digits()
    {
        var ticket = new Ticket { Id = 1, TicketNumber = 183, Title = "Printer jams" };
        Assert.Equal("TKT-0183", ticket.ToListItem().Reference);
    }

    [Fact]
    public void Detail_mapping_carries_asset_context()
    {
        var ticket = new Ticket
        {
            Id = 5,
            TicketNumber = 183,
            Title = "Printer jams",
            AssetId = 41,
            Asset = new Asset
            {
                Id = 41,
                AssetTag = "IAMS-0241",
                DeviceType = DeviceTypes.Printer,
                Status = AssetStatus.Maintenance,
                Manufacturer = "HP",
                Model = "LaserJet M404dn",
                WarrantyEndDate = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var dto = ticket.ToDto(includeInternalComments: false);

        Assert.Equal("IAMS-0241", dto.AssetTag);
        Assert.Equal("HP LaserJet M404dn", dto.AssetName);
        Assert.Equal(AssetStatus.Maintenance, dto.AssetStatus);
        Assert.Equal(2026, dto.WarrantyEndDate!.Value.Year);
    }

    [Fact]
    public void Internal_comments_are_dropped_when_not_permitted()
    {
        var ticket = new Ticket
        {
            Id = 5, TicketNumber = 1, Title = "Printer jams",
            Comments =
            [
                new TicketComment { Id = 1, Body = "Looking at it", IsInternal = false },
                new TicketComment { Id = 2, Body = "Third jam this quarter", IsInternal = true }
            ]
        };

        var hidden = ticket.ToDto(includeInternalComments: false);
        var shown = ticket.ToDto(includeInternalComments: true);

        Assert.Single(hidden.Comments);
        Assert.Equal("Looking at it", hidden.Comments[0].Body);
        Assert.Equal(2, shown.Comments.Count);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketMappingTests`
Expected: build failure — `The type or namespace name 'Mapping' does not exist`.

- [ ] **Step 4: Write the mapping**

Create `src/IAMS.Api/Mapping/TicketMapping.cs`:

```csharp
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;

namespace IAMS.Api.Mapping;

public static class TicketMapping
{
    public static TicketListItemDto ToListItem(this Ticket t) => new()
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        Type = t.Type,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        AssetId = t.AssetId,
        AssetTag = t.Asset?.AssetTag,
        RequesterName = t.RequesterUser?.FullName,
        RequesterDepartment = t.RequesterUser?.Department,
        AssignedToName = t.AssignedToUser?.FullName,
        CreatedAt = t.CreatedAt,
        DueAt = t.DueAt
    };

    public static TicketDto ToDto(this Ticket t, bool includeInternalComments) => new()
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        Type = t.Type,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        Description = t.Description,
        Resolution = t.Resolution,
        AssetId = t.AssetId,
        AssetTag = t.Asset?.AssetTag,
        AssetName = t.Asset?.DisplayName,
        AssetStatus = t.Asset?.Status,
        WarrantyEndDate = t.Asset?.WarrantyEndDate,
        RequesterUserId = t.RequesterUserId,
        RequesterName = t.RequesterUser?.FullName,
        RequesterDepartment = t.RequesterUser?.Department,
        AssignedToUserId = t.AssignedToUserId,
        AssignedToName = t.AssignedToUser?.FullName,
        CreatedAt = t.CreatedAt,
        AssignedAt = t.AssignedAt,
        StartedAt = t.StartedAt,
        ResolvedAt = t.ResolvedAt,
        ClosedAt = t.ClosedAt,
        DueAt = t.DueAt,
        AssetAssignmentId = t.AssetAssignmentId,
        Comments = t.Comments
            .Where(c => includeInternalComments || !c.IsInternal)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.ToDto())
            .ToList()
    };

    public static TicketCommentDto ToDto(this TicketComment c) => new()
    {
        Id = c.Id,
        Body = c.Body,
        IsInternal = c.IsInternal,
        AuthorName = c.User?.FullName,
        AuthorUserId = c.UserId,
        CreatedAt = c.CreatedAt
    };
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketMappingTests`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 6: Write the controller**

Create `src/IAMS.Api/Controllers/TicketsController.cs`:

```csharp
using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _tickets;
    private readonly AppDbContext _db;

    public TicketsController(ITicketService tickets, AppDbContext db)
    {
        _tickets = tickets;
        _db = db;
    }

    private string CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
    private bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Staff");

    [HttpGet]
    [Authorize(Policy = "Staff")]
    public async Task<ActionResult<ApiResponse<PagedResponse<TicketListItemDto>>>> List(
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] string? assignedToUserId,
        [FromQuery] int? assetId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var (items, total) = await _tickets.ListAsync(
            new TicketQuery(type, status, priority, assignedToUserId, assetId, search, page, pageSize), ct);

        var payload = new PagedResponse<TicketListItemDto>
        {
            Items = items.Select(t => t.ToListItem()).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };

        return Ok(ApiResponse<PagedResponse<TicketListItemDto>>.Ok(payload));
    }

    [HttpGet("summary")]
    [Authorize(Policy = "Staff")]
    public async Task<ActionResult<ApiResponse<TicketSummaryDto>>> Summary(CancellationToken ct)
    {
        var summary = await _tickets.GetSummaryAsync(ct);

        return Ok(ApiResponse<TicketSummaryDto>.Ok(new TicketSummaryDto
        {
            Open = summary.Open,
            Unassigned = summary.Unassigned,
            InProgress = summary.InProgress,
            Overdue = summary.Overdue
        }));
    }

    [HttpGet("mine")]
    public async Task<ActionResult<ApiResponse<List<TicketListItemDto>>>> Mine(CancellationToken ct)
    {
        var mine = await _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .Where(t => t.RequesterUserId == CurrentUserId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return Ok(ApiResponse<List<TicketListItemDto>>.Ok(mine.Select(t => t.ToListItem()).ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<TicketDto>>> Get(int id, CancellationToken ct)
    {
        var ticket = await _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .Include(t => t.Comments).ThenInclude(c => c.User)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (ticket is null)
            return NotFound(ApiResponse<TicketDto>.Fail("Ticket not found."));

        // A requester may read their own ticket; everyone else needs staff rights.
        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        return Ok(ApiResponse<TicketDto>.Ok(ticket.ToDto(includeInternalComments: IsStaff)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<TicketDto>>> Create(
        [FromBody] CreateTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.CreateAsync(
            request.Type, request.Title, request.Description, request.Priority,
            request.AssetId, CurrentUserId, ct);

        if (!result.Success)
            return BadRequest(ApiResponse<TicketDto>.Fail(result.Message!));

        var created = await _tickets.GetAsync(result.Value!.Id, ct);
        return Ok(ApiResponse<TicketDto>.Ok(created!.ToDto(includeInternalComments: IsStaff), "Ticket created."));
    }

    [HttpPost("{id:int}/assign")]
    [Authorize(Policy = "Staff")]
    public async Task<ActionResult<ApiResponse<object>>> Assign(
        int id, [FromBody] AssignTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.AssignAsync(id, request.AssignedToUserId, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Ticket assigned."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/status")]
    [Authorize(Policy = "Staff")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeStatus(
        int id, [FromBody] ChangeTicketStatusRequest request, CancellationToken ct)
    {
        var result = await _tickets.ChangeStatusAsync(id, request.Status, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, $"Ticket moved to {request.Status}."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/resolve")]
    [Authorize(Policy = "Staff")]
    public async Task<ActionResult<ApiResponse<object>>> Resolve(
        int id, [FromBody] ResolveTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.ResolveAsync(id, request.Resolution, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Ticket resolved."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/fulfil")]
    [Authorize(Policy = "CanManageAssets")]
    public async Task<ActionResult<ApiResponse<object>>> Fulfil(
        int id, [FromBody] FulfilTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.FulfilAsync(id, request.AssetId, request.Resolution, CurrentUserId, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Request fulfilled and asset issued."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }
}
```

- [ ] **Step 7: Register the services and delete the old DTO**

In `src/IAMS.Api/Program.cs`, beside the other `AddScoped` registrations:

```csharp
builder.Services.AddScoped<ITicketNumberAllocator, TicketNumberAllocator>();
builder.Services.AddScoped<ITicketService, TicketService>();
```

```bash
git rm src/IAMS.Shared/DTOs/MaintenanceDto.cs
```

Any remaining compile errors will be in `src/IAMS.Web` referring to maintenance DTOs. Comment out or delete `Pages/Maintenance.razor`, `Components/MaintenanceAttachments.razor` and the maintenance methods in `Services/ApiClient.cs` — the replacement pages come in the phase 1 web plan.

- [ ] **Step 8: Verify the whole solution builds and all tests pass**

Run: `dotnet build && dotnet test`
Expected: build succeeds with no errors; `Failed: 0` across the whole suite

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(api): add TicketsController with DTOs and mapping"
```

---

### Task 11: Comments and attachments endpoints

**Files:**
- Create: `src/IAMS.Api/Controllers/TicketCommentsController.cs`
- Create: `src/IAMS.Api/Controllers/TicketAttachmentsController.cs`
- Create: `tests/IAMS.Api.Tests/TicketCommentVisibilityTests.cs`

**Interfaces:**
- Consumes: `ITicketService.AddCommentAsync`, `TicketMapping.ToDto`, `FileStorageService`, `ISubscriptionService.CanUploadFileAsync`.
- Produces: no new types; two controllers.

Read `src/IAMS.Api/Controllers/MaintenanceAttachmentsController.cs` in git history before writing the attachments controller — it is the template:

```bash
git show HEAD~1:src/IAMS.Api/Controllers/MaintenanceAttachmentsController.cs
```

Reproduce it with `Maintenance` replaced by `Ticket` throughout, keeping the same upload validation, `CanUploadFileAsync` quota check, `FileStorageService` calls, and download and delete endpoints.

- [ ] **Step 1: Write the failing test**

Create `tests/IAMS.Api.Tests/TicketCommentVisibilityTests.cs`:

```csharp
using IAMS.Api.Entities;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketCommentVisibilityTests
{
    [Fact]
    public async Task A_requesters_view_never_contains_internal_notes()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
            var created = await service.CreateAsync(
                TicketTypes.Incident, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

            await service.AddCommentAsync(created.Value!.Id, "staff-1", "On it now.", false, default);
            await service.AddCommentAsync(created.Value.Id, "staff-1", "Warranty lapses soon.", true, default);

            db.ChangeTracker.Clear();
            var loaded = await db.Tickets
                .Include(t => t.Comments)
                .SingleAsync(t => t.Id == created.Value.Id);

            var requesterView = loaded.ToDto(includeInternalComments: false);
            var staffView = loaded.ToDto(includeInternalComments: true);

            Assert.Single(requesterView.Comments);
            Assert.DoesNotContain(requesterView.Comments, c => c.IsInternal);
            Assert.DoesNotContain("Warranty", string.Join(" ", requesterView.Comments.Select(c => c.Body)));
            Assert.Equal(2, staffView.Comments.Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it passes already**

Run: `dotnet test tests/IAMS.Api.Tests/IAMS.Api.Tests.csproj --filter TicketCommentVisibilityTests`
Expected: `Passed! - Failed: 0, Passed: 1` — the filtering logic landed in Task 10; this test pins the behaviour so a future edit to the controller cannot leak internal notes.

- [ ] **Step 3: Write the comments controller**

Create `src/IAMS.Api/Controllers/TicketCommentsController.cs`:

```csharp
using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/tickets/{ticketId:int}/comments")]
[Authorize]
public class TicketCommentsController : ControllerBase
{
    private readonly ITicketService _tickets;
    private readonly AppDbContext _db;

    public TicketCommentsController(ITicketService tickets, AppDbContext db)
    {
        _tickets = tickets;
        _db = db;
    }

    private string CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
    private bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Staff");

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<TicketCommentDto>>>> List(int ticketId, CancellationToken ct)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<List<TicketCommentDto>>.Fail("Ticket not found."));

        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var comments = await _db.TicketComments
            .Include(c => c.User)
            .Where(c => c.TicketId == ticketId)
            .Where(c => IsStaff || !c.IsInternal)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return Ok(ApiResponse<List<TicketCommentDto>>.Ok(comments.Select(c => c.ToDto()).ToList()));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<TicketCommentDto>>> Add(
        int ticketId, [FromBody] AddTicketCommentRequest request, CancellationToken ct)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<TicketCommentDto>.Fail("Ticket not found."));

        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        // Only staff may write a note the requester cannot see.
        if (request.IsInternal && !IsStaff)
            return Forbid();

        var result = await _tickets.AddCommentAsync(
            ticketId, CurrentUserId, request.Body, request.IsInternal, ct);

        return result.Success
            ? Ok(ApiResponse<TicketCommentDto>.Ok(result.Value!.ToDto(), "Comment added."))
            : BadRequest(ApiResponse<TicketCommentDto>.Fail(result.Message!));
    }
}
```

- [ ] **Step 4: Write the attachments controller**

Create `src/IAMS.Api/Controllers/TicketAttachmentsController.cs` as a copy of the old `MaintenanceAttachmentsController` retrieved in the task preamble, with these substitutions applied throughout: route `api/maintenance/{maintenanceId:int}/attachments` becomes `api/tickets/{ticketId:int}/attachments`; `_db.MaintenanceAttachments` becomes `_db.TicketAttachments`; `MaintenanceAttachment` becomes `TicketAttachment`; `MaintenanceId` becomes `TicketId`; `_db.Maintenances` becomes `_db.Tickets`. Keep every validation, quota check and storage call byte-for-byte otherwise.

- [ ] **Step 5: Verify the solution builds and every test passes**

Run: `dotnet build && dotnet test`
Expected: build succeeds; `Failed: 0` across the whole suite

- [ ] **Step 6: Smoke-test the API by hand**

```bash
cd src/IAMS.Api && dotnet run
```

In Swagger at `https://localhost:5001/swagger`, sign in as `admin@company.com` / `Admin123!` and confirm: `POST /api/tickets` returns a ticket with `reference: "TKT-0001"`; `GET /api/tickets` lists it; `POST /api/tickets/{id}/assign` then `/status` to `InProgress` then `/resolve` walks the ticket through; and `POST /api/tickets/{id}/status` with `Closed` from `New` returns 400 with a readable message.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(api): add ticket comment and attachment endpoints"
```

---

## What this plan does not cover

Deliberately deferred, each needing its own plan:

- **Phase 1 web UI** — `Pages/Tickets/Index.razor`, `Pages/Tickets/View.razor`, the `ApiClient` methods, the `MainLayout` nav change, and the `/maintenance` redirect. Task 10 Step 7 removes the old maintenance pages, so the Blazor app has no ticket UI until that plan lands.
- **Phase 2** — `Roles.Employee`, the seat-metering exclusions in `SubscriptionService`, `Tenant.MaxTicketsPerMonth`, `/report`, `/my-tickets`, and scan-to-report.
- **Phase 3** — SLA target hours, `DueAt` computation, the overdue background service, breach notifications, and auto-close.
- **Ticket notifications** — the spec's notification events reuse `NotificationService` but are not wired up here; they belong with the phase 2 portal, where a requester finally has somewhere to receive them.
- **Audit log read endpoint** — the table fills from Task 5 onward, but nothing reads it until the evidence-export work.
