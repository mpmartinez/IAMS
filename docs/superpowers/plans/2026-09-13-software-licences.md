# Software Licence Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A software licence register for AssetDesk - entitlements acquired, seats assigned to people or devices, computed renewal status, masked and audited licence keys, receiving Software purchase-order lines into a licence, and a compliance report.

**Architecture:** Three new tenant-scoped entities (`SoftwareLicence`, `LicenceEntitlement`, `LicenceSeatAssignment`) behind a new `LicencesController`, with seat and spend figures computed in one place (`LicenceUsageReader`) that the controller and the report both call. `GoodsReceiptService.ReceiveAsync` keeps its transaction exactly as it is and changes only what a claimed Software line produces. Renewal status is derived on read by a pure `LicenceRules` class; nothing is stored or scheduled.

**Tech Stack:** .NET 10, ASP.NET Core Web API, EF Core (Npgsql in production, SQLite in tests), Blazor WebAssembly, QuestPDF, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-13-software-licences-design.md`

## Global Constraints

- Every controller and service read of the three new entities applies an **explicit** `TenantId == tenantId` predicate. The global query filter has an `IsSuperAdmin()` bypass and is never the isolation mechanism.
- Every new controller has an isolation test whose caller is `new FakeTenantProvider(tenantA, isSuperAdmin: true)` targeting tenant B's row - built so it would pass *only* because of the explicit predicate, never because of a null-tenant guard.
- A caller with no current tenant gets `BadRequest("Select an organisation first.")` - the wording every procurement endpoint already uses.
- No list, detail, report, export or receipt response ever contains a full licence key. The full key is returned only by `POST /api/licences/{id}/key/reveal`.
- Permission keys are exactly `iams:licences:view`, `iams:licences:manage`, `iams:licences:reveal`. **Three colon-separated parts** - `PermissionCatalogTests.EveryKey_UsesTheIamsPrefix` fails any other shape.
- Policies are exactly `CanViewLicences`, `CanManageLicences`, `CanRevealLicenceKeys`.
- Licence models are exactly `PerUser` and `PerDevice`. Renewal statuses are exactly `Perpetual`, `Active`, `Due`, `Expired`. The renewal window is **90 days**, the same as `WarrantyCheckService`.
- Receipt modes are exactly `AddSeats` and `Renew`.
- Masked keys render as `****-` plus the last four characters; a key shorter than 8 characters renders as `****` only.
- Currency/rate pairs go through `CurrencyRules.Validate` - never a second copy of those rules. Peso values are `Cost * ExchangeRate`, derived, never stored.
- EF Core on SQLite cannot `Sum` or `OrderBy` a `decimal` in SQL. Every decimal aggregate is computed in memory after `ToListAsync`, as `ReportsController.BuildDepreciationSummaryAsync` does.
- Licences are deactivated, never deleted. There is no delete endpoint that removes a licence row.
- `LicenceKey` is redacted in the automatic change log: recorded as changed, with `"[redacted]"` in place of both values.
- Migrations: run `dotnet ef migrations script <previous> <new>` from `src/AssetDesk.Api` **without `--no-build`** and read the SQL by hand. `--no-build` against stale binaries silently emits a script missing the new tables and still exits 0.
- Stop any running API or Web server before `dotnet build` - a held DLL lock fails with MSB3027, which reads like a code error but is not.
- Tailwind: `src/AssetDesk.Web/wwwroot/css/app.css` is a committed, precompiled artifact. Use only class tokens that already appear in existing `.razor` files. If a task genuinely needs a new one, run `npm --prefix src/AssetDesk.Web run build:css` and bump both `?v=` in `wwwroot/index.html` and `cacheName` in `service-worker.published.js`.
- Comments explain *why*, in the codebase's existing register. Never reference "the plan", "the spec", "a review" or "a task" in code or commit messages.
- Every commit message ends with exactly this line and no other co-author line:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`
- Test command: `dotnet test tests/AssetDesk.Api.Tests`. Baseline before Task 1: **418 passed, 0 failed**.

## File Structure

**Shared** (`src/AssetDesk.Shared`)
- `LicenceModels.cs` (new) - `LicenceModels`, `LicenceRenewalStatuses`, `LicenceReceiptModes` constants. Shared because the Web project branches on them too.
- `DTOs/LicenceDto.cs` (new) - every licence DTO and the compliance report DTOs.
- `DTOs/ProcurementDto.cs` (modify) - `ReceiveLineDto` gains licence fields; `GoodsReceiptLineDto` gains its licence destination.

**API** (`src/AssetDesk.Api`)
- `Entities/SoftwareLicence.cs` (new) - the three entities.
- `Entities/LicenceRules.cs` (new) - pure rules: renewal status, masking, reclaimable.
- `Entities/AuditLog.cs` (modify) - `AuditActions.LicenceKeyRevealed`.
- `Data/AppDbContext.cs` (modify) - DbSets and configuration.
- `Data/AuditSaveChangesInterceptor.cs` (modify) - audited types and redaction.
- `Authorization/Permissions.cs` (modify) - three keys and role defaults.
- `Program.cs` (modify) - three policies, one service registration.
- `Services/LicenceUsageReader.cs` (new) - seats owned/assigned/reclaimable and spend per licence, for a tenant.
- `Services/CsvFormat.cs` (new) - `Escape`, extracted from `ReportsController` so the licence export does not become its second copy.
- `Services/GoodsReceiptService.cs` (modify) - Software lines produce entitlements.
- `Services/PdfReportService.cs` (modify) - licence compliance PDF.
- `Controllers/LicencesController.cs` (new) - licences, entitlements, seats, key reveal, renewal count, device seats.
- `Controllers/LicenceReportsController.cs` (new) - `api/reports/licences` JSON, CSV, PDF.
- `Controllers/PurchaseOrdersController.cs` (modify) - `NewLicence` permission check; receipt line destinations.
- `Controllers/ReportsController.cs` (modify) - use `CsvFormat.Escape`.
- `Migrations/*_AddSoftwareLicences.cs`, `Migrations/*_GrantLicencePermissions.cs` (new).

**Web** (`src/AssetDesk.Web`)
- `Services/ApiClient.cs` (modify) - licence calls.
- `Components/LicenceEditor.razor` (new) - the create/edit modal shared by the list and detail pages.
- `Components/LicenceDisplay.cs` (new) - renewal badge variant and label, shared by three pages.
- `Pages/Licences/Index.razor` (new) - `/licences`.
- `Pages/Licences/Detail.razor` (new) - `/licences/{Id:int}`.
- `Layout/MainLayout.razor` (modify) - Licences nav group with renewal badge.
- `Pages/Procurement/PurchaseOrderDetail.razor` (modify) - Software fields in the receive dialog; destination in delivery history.
- `Pages/Assets/View.razor` (modify) - "Licences on this device".

**Tests** (`tests/AssetDesk.Api.Tests`)
- `LicenceRulesTests.cs`, `LicenceSchemaTests.cs`, `LicenceAuditTests.cs`, `LicenceApiTests.cs`, `LicenceEntitlementApiTests.cs`, `LicenceSeatApiTests.cs`, `LicenceReceivingTests.cs`, `LicenceComplianceReportTests.cs` (new)
- `PermissionCatalogTests.cs` (modify)

---

### Task 1: Entities, rules, schema and migration

**Files:**
- Create: `src/AssetDesk.Shared/LicenceModels.cs`
- Create: `src/AssetDesk.Api/Entities/SoftwareLicence.cs`
- Create: `src/AssetDesk.Api/Entities/LicenceRules.cs`
- Modify: `src/AssetDesk.Api/Data/AppDbContext.cs` (DbSets after line 43; configuration immediately before the `// Normalise every DateTime to UTC` block)
- Create: migration `AddSoftwareLicences` (generated)
- Test: `tests/AssetDesk.Api.Tests/LicenceRulesTests.cs`, `tests/AssetDesk.Api.Tests/LicenceSchemaTests.cs`

**Interfaces:**
- Produces:
  - `AssetDesk.Shared.LicenceModels.PerUser`, `.PerDevice`, `.All`, `bool IsValid(string?)`, `string Label(string)`
  - `AssetDesk.Shared.LicenceRenewalStatuses.Perpetual`, `.Active`, `.Due`, `.Expired`
  - `AssetDesk.Shared.LicenceReceiptModes.AddSeats`, `.Renew`, `bool IsValid(string?)`
  - `SoftwareLicence`, `LicenceEntitlement`, `LicenceSeatAssignment` entities (fields below)
  - `AppDbContext.SoftwareLicences`, `.LicenceEntitlements`, `.LicenceSeatAssignments`
  - `LicenceRules.RenewalWindowDays` (90), `int? DaysUntilExpiry(DateTime? expiresAt, DateTime today)`, `string RenewalStatus(DateTime? expiresAt, DateTime today)`, `bool NeedsRenewalAttention(string status)`, `string? Mask(string? key)`, `bool IsReclaimable(bool? userIsActive, string? assetStatus)`

- [ ] **Step 1: Write the shared constants**

Create `src/AssetDesk.Shared/LicenceModels.cs`:

```csharp
namespace AssetDesk.Shared;

/// <summary>
/// How a licence is counted. Shared rather than kept in the Api's entities because the Web
/// project branches on it too - the seat picker offers people for one and devices for the other.
/// </summary>
public static class LicenceModels
{
    public const string PerUser = "PerUser";
    public const string PerDevice = "PerDevice";

    public static readonly string[] All = [PerUser, PerDevice];

    public static bool IsValid(string? model) => model is not null && All.Contains(model);

    public static string Label(string model) => model switch
    {
        PerUser => "Per user",
        PerDevice => "Per device",
        _ => model
    };
}

/// <summary>Computed on read from a licence's expiry - never stored, so it cannot go stale.</summary>
public static class LicenceRenewalStatuses
{
    public const string Perpetual = "Perpetual";
    public const string Active = "Active";
    public const string Due = "Due";
    public const string Expired = "Expired";
}

/// <summary>
/// What receiving a Software purchase-order line does to its licence. A renewal records the cost
/// and moves the expiry without adding seats: receiving "50 seats, 2027 renewal" against a licence
/// that already owns 50 must not report 100 owned.
/// </summary>
public static class LicenceReceiptModes
{
    public const string AddSeats = "AddSeats";
    public const string Renew = "Renew";

    public static bool IsValid(string? mode) => mode is AddSeats or Renew;
}
```

- [ ] **Step 2: Write the failing rules tests**

Create `tests/AssetDesk.Api.Tests/LicenceRulesTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Shared;

namespace AssetDesk.Api.Tests;

public class LicenceRulesTests
{
    private static readonly DateTime Today = new(2026, 9, 13);

    [Fact]
    public void A_licence_with_no_expiry_is_perpetual()
    {
        Assert.Equal(LicenceRenewalStatuses.Perpetual, LicenceRules.RenewalStatus(null, Today));
        Assert.Null(LicenceRules.DaysUntilExpiry(null, Today));
    }

    [Theory]
    [InlineData(-1, LicenceRenewalStatuses.Expired)]
    [InlineData(0, LicenceRenewalStatuses.Due)]
    [InlineData(90, LicenceRenewalStatuses.Due)]
    [InlineData(91, LicenceRenewalStatuses.Active)]
    public void Renewal_status_follows_the_ninety_day_window(int daysAhead, string expected)
    {
        Assert.Equal(expected, LicenceRules.RenewalStatus(Today.AddDays(daysAhead), Today));
    }

    [Fact]
    public void Days_until_expiry_ignores_the_time_of_day()
    {
        // An expiry stored as midnight UTC and a "today" taken mid-afternoon are still one
        // calendar day apart, not zero days and a fraction.
        var expiresAt = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var afternoon = new DateTime(2026, 9, 13, 15, 30, 0, DateTimeKind.Utc);

        Assert.Equal(1, LicenceRules.DaysUntilExpiry(expiresAt, afternoon));
    }

    [Theory]
    [InlineData(LicenceRenewalStatuses.Due, true)]
    [InlineData(LicenceRenewalStatuses.Expired, true)]
    [InlineData(LicenceRenewalStatuses.Active, false)]
    [InlineData(LicenceRenewalStatuses.Perpetual, false)]
    public void Only_due_and_expired_licences_need_renewal_attention(string status, bool expected)
    {
        Assert.Equal(expected, LicenceRules.NeedsRenewalAttention(status));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ABC12", "****")]
    [InlineData("ABCD123", "****")]
    [InlineData("ABCD1234", "****-1234")]
    [InlineData("XXXXX-XXXXX-XXXXX-X7Q2", "****-X7Q2")]
    public void A_key_is_masked_to_at_most_its_last_four_characters(string? key, string? expected)
    {
        Assert.Equal(expected, LicenceRules.Mask(key));
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(null, AssetStatus.Retired, true)]
    [InlineData(null, AssetStatus.Lost, true)]
    [InlineData(null, AssetStatus.InUse, false)]
    [InlineData(null, AssetStatus.Maintenance, false)]
    public void A_seat_is_reclaimable_when_its_holder_is_gone(bool? userIsActive, string? assetStatus, bool expected)
    {
        Assert.Equal(expected, LicenceRules.IsReclaimable(userIsActive, assetStatus));
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceRulesTests"`
Expected: build FAILS - `The name 'LicenceRules' does not exist in the current context`.

- [ ] **Step 4: Write the rules**

Create `src/AssetDesk.Api/Entities/LicenceRules.cs`:

```csharp
using AssetDesk.Shared;

namespace AssetDesk.Api.Entities;

/// <summary>
/// The rules that turn stored licence facts into what a person needs to see. Pure - no DbContext,
/// no clock - so every caller passes "today" and every rule is testable at its edges.
/// </summary>
public static class LicenceRules
{
    /// <summary>The same window WarrantyCheckService uses for an expiring warranty.</summary>
    public const int RenewalWindowDays = 90;

    public static int? DaysUntilExpiry(DateTime? expiresAt, DateTime today) =>
        expiresAt is { } expiry ? (expiry.Date - today.Date).Days : null;

    public static string RenewalStatus(DateTime? expiresAt, DateTime today) =>
        DaysUntilExpiry(expiresAt, today) switch
        {
            null => LicenceRenewalStatuses.Perpetual,
            < 0 => LicenceRenewalStatuses.Expired,
            <= RenewalWindowDays => LicenceRenewalStatuses.Due,
            _ => LicenceRenewalStatuses.Active
        };

    public static bool NeedsRenewalAttention(string status) =>
        status is LicenceRenewalStatuses.Due or LicenceRenewalStatuses.Expired;

    /// <summary>
    /// A key shorter than eight characters is masked entirely: the last four of a five-character
    /// key is most of the key.
    /// </summary>
    public static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return key.Length < 8 ? "****" : $"****-{key[^4..]}";
    }

    /// <summary>
    /// A seat still held by someone who has left, or by a machine that is retired or lost. It is
    /// still counted as assigned - it is, until someone releases it - but it is where the saving is.
    /// </summary>
    public static bool IsReclaimable(bool? userIsActive, string? assetStatus) =>
        userIsActive == false || assetStatus is AssetStatus.Retired or AssetStatus.Lost;
}
```

- [ ] **Step 5: Run the rules tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceRulesTests"`
Expected: PASS, 22 tests.

- [ ] **Step 6: Write the entities**

Create `src/AssetDesk.Api/Entities/SoftwareLicence.cs`:

```csharp
namespace AssetDesk.Api.Entities;

/// <summary>
/// A piece of software the organisation is entitled to use. Deactivated, never deleted: its
/// entitlements are the record of what was bought and its seats the record of who had it.
/// </summary>
public class SoftwareLicence : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }
    public string? Publisher { get; set; }

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>LicenceModels.PerUser or PerDevice. Fixed once any seat exists.</summary>
    public required string LicenceModel { get; set; }

    /// <summary>
    /// Never returned in full except by the audited reveal endpoint, and redacted in the automatic
    /// change log - AuditSaveChangesInterceptor would otherwise write it into a table that only grows.
    /// </summary>
    public string? LicenceKey { get; set; }

    /// <summary>Null means perpetual.</summary>
    public DateTime? ExpiresAt { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<LicenceEntitlement> Entitlements { get; set; } = [];
    public ICollection<LicenceSeatAssignment> Seats { get; set; } = [];
}

/// <summary>
/// One acquisition of seats, or a renewal of them. Seats owned is the sum of SeatsAdded rather than
/// a column on the licence, for the reason GoodsReceipt carries its own rate: fifty seats bought in
/// January and ten in June cost different pesos, and a single count cannot say so.
/// </summary>
public class LicenceEntitlement : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int SoftwareLicenceId { get; set; }
    public SoftwareLicence? SoftwareLicence { get; set; }

    /// <summary>Positive for a purchase, zero for a renewal, negative for a recorded reduction.</summary>
    public int SeatsAdded { get; set; }

    /// <summary>The total for this entry, in Currency.</summary>
    public decimal Cost { get; set; }

    public string Currency { get; set; } = Currencies.PHP;

    /// <summary>Pesos per one unit of Currency, as booked. Exactly 1 for PHP.</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    public DateTime EntitlementDate { get; set; } = DateTime.UtcNow;

    /// <summary>Set when this entry renewed the licence, or dated it for the first time.</summary>
    public DateTime? ExpiresAtAfter { get; set; }

    /// <summary>
    /// The receipt line this entry came from, when it came from receiving a purchase order. Null
    /// for an entry recorded by hand - the same honesty Asset.GoodsReceiptLineId keeps.
    /// </summary>
    public int? GoodsReceiptLineId { get; set; }
    public GoodsReceiptLine? GoodsReceiptLine { get; set; }

    public required string CreatedByUserId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A seat given to a person or to a device. Active while ReleasedAt is null; a released seat stays
/// as history. Exactly one of UserId and AssetId is set - enforced by a check constraint, so no code
/// path can write a seat that belongs to nobody or to both.
/// </summary>
public class LicenceSeatAssignment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int SoftwareLicenceId { get; set; }
    public SoftwareLicence? SoftwareLicence { get; set; }

    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }

    public required string AssignedByUserId { get; set; }
    public string? ReleasedByUserId { get; set; }
    public string? Notes { get; set; }

    public bool IsActive => ReleasedAt is null;
}
```

- [ ] **Step 7: Write the failing schema tests**

Create `tests/AssetDesk.Api.Tests/LicenceSchemaTests.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The rules the database itself holds. These run on SQLite, which proves the constraints are
/// modelled; the generated PostgreSQL SQL is read by hand and exercised on a real database before
/// merge.
/// </summary>
public class LicenceSchemaTests
{
    private static SoftwareLicence Licence(
        Guid tenantId, string name = "Microsoft 365 Business Standard", string model = LicenceModels.PerUser) =>
        new() { TenantId = tenantId, Name = name, LicenceModel = model };

    private static LicenceSeatAssignment Seat(
        Guid tenantId, int licenceId, string? userId = null, int? assetId = null) =>
        new()
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId,
            UserId = userId, AssetId = assetId, AssignedByUserId = "admin-1"
        };

    [Fact]
    public async Task A_licence_name_is_unique_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            db.SoftwareLicences.Add(Licence(tenantId));
            await db.SaveChangesAsync();

            db.SoftwareLicences.Add(Licence(tenantId));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task The_same_licence_name_is_allowed_in_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            db.SoftwareLicences.Add(Licence(tenantA));
            db.SoftwareLicences.Add(Licence(tenantB));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.SoftwareLicences.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task A_seat_with_no_target_is_refused_by_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_seat_naming_both_a_person_and_a_device_is_refused_by_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1", assetId: asset.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_person_holds_one_active_seat_per_licence_until_it_is_released()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var first = Seat(tenantId, licence.Id, userId: "user-1");
            db.LicenceSeatAssignments.Add(first);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var reloaded = await db.LicenceSeatAssignments.SingleAsync();
            reloaded.ReleasedAt = DateTime.UtcNow;
            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, userId: "user-1"));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.LicenceSeatAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task A_device_holds_one_active_seat_per_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await db.SaveChangesAsync();

            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_device_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = Licence(tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();
            db.LicenceSeatAssignments.Add(Seat(tenantId, licence.Id, assetId: asset.Id));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // The seat is deliberately not loaded: the database's cascade has to do this, not
            // EF's change tracker, because AssetsController.DeleteAsset loads only the asset.
            db.Assets.Remove(await db.Assets.SingleAsync());
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.LicenceSeatAssignments.CountAsync());
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task A_receipt_line_feeds_at_most_one_entitlement()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Software" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Status = PurchaseOrderStatus.Ordered, CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine { DeviceType = DeviceTypes.Software, Quantity = 50, UnitPrice = 700m });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var receipt = new GoodsReceipt
            {
                TenantId = tenantId, PurchaseOrderId = order.Id, RequestId = Guid.NewGuid(),
                ReceivedByUserId = "user-1"
            };
            receipt.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = order.Lines.First().Id, QuantityReceived = 50 });
            db.GoodsReceipts.Add(receipt);

            var licence = Licence(tenantId);
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var receiptLineId = receipt.Lines.First().Id;
            LicenceEntitlement Entry() => new()
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, SeatsAdded = 50, Cost = 35000m,
                GoodsReceiptLineId = receiptLineId, CreatedByUserId = "user-1"
            };

            db.LicenceEntitlements.Add(Entry());
            await db.SaveChangesAsync();

            db.LicenceEntitlements.Add(Entry());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
```

- [ ] **Step 8: Run it to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceSchemaTests"`
Expected: build FAILS - `'AppDbContext' does not contain a definition for 'SoftwareLicences'`.

- [ ] **Step 9: Configure the context**

In `src/AssetDesk.Api/Data/AppDbContext.cs`, add after `public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();`:

```csharp
    public DbSet<SoftwareLicence> SoftwareLicences => Set<SoftwareLicence>();
    public DbSet<LicenceEntitlement> LicenceEntitlements => Set<LicenceEntitlement>();
    public DbSet<LicenceSeatAssignment> LicenceSeatAssignments => Set<LicenceSeatAssignment>();
```

Insert immediately before the `// Normalise every DateTime to UTC on the way to the database.` comment (the UTC converter loop must stay last):

```csharp
        modelBuilder.Entity<SoftwareLicence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Publisher).HasMaxLength(200);
            entity.Property(e => e.LicenceModel).HasMaxLength(20).IsRequired();
            entity.Property(e => e.LicenceKey).HasMaxLength(500);

            entity.HasOne(e => e.Supplier)
                .WithMany()
                .HasForeignKey(e => e.SupplierId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        modelBuilder.Entity<LicenceEntitlement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Cost).HasPrecision(18, 2);
            entity.Property(e => e.ExchangeRate).HasPrecision(18, 6);
            entity.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue(Currencies.PHP);

            // Restrict, not Cascade: licences are deactivated, never deleted, and an entitlement
            // is the record of a purchase.
            entity.HasOne(e => e.SoftwareLicence)
                .WithMany(l => l.Entitlements)
                .HasForeignKey(e => e.SoftwareLicenceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Unique: one receipt line is one delivery of one product, so it can feed only one
            // entitlement. PostgreSQL and SQLite both let any number of NULLs through a unique
            // index, so hand-entered entries are unaffected.
            entity.HasOne(e => e.GoodsReceiptLine)
                .WithMany()
                .HasForeignKey(e => e.GoodsReceiptLineId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.GoodsReceiptLineId).IsUnique();

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        modelBuilder.Entity<LicenceSeatAssignment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Ignore(e => e.IsActive);

            // Exactly one target. Held by the database rather than only the API so that no code
            // path - a future import, a hand-written fix - can write a seat nobody holds.
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_LicenceSeatAssignments_ExactlyOneTarget",
                "(\"UserId\" IS NULL) <> (\"AssetId\" IS NULL)"));

            // One active seat per person or device per licence. Partial, so a released seat is
            // history rather than an obstacle to assigning that person again.
            entity.HasIndex(e => new { e.SoftwareLicenceId, e.UserId })
                .IsUnique()
                .HasFilter("\"ReleasedAt\" IS NULL AND \"UserId\" IS NOT NULL")
                .HasDatabaseName("IX_LicenceSeatAssignments_ActiveUserSeat");
            entity.HasIndex(e => new { e.SoftwareLicenceId, e.AssetId })
                .IsUnique()
                .HasFilter("\"ReleasedAt\" IS NULL AND \"AssetId\" IS NOT NULL")
                .HasDatabaseName("IX_LicenceSeatAssignments_ActiveAssetSeat");

            entity.HasOne(e => e.SoftwareLicence)
                .WithMany(l => l.Seats)
                .HasForeignKey(e => e.SoftwareLicenceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Users are deactivated, never deleted, so Restrict costs nothing and keeps a seat
            // from silently vanishing.
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cascade, matching AssetAssignment: deleting an asset already removes its assignment
            // history. Retiring an asset is the path that keeps history.
            entity.HasOne(e => e.Asset)
                .WithMany()
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

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

- [ ] **Step 10: Run the schema tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceSchemaTests"`
Expected: PASS, 8 tests.

- [ ] **Step 11: Generate the migration**

Run from `src/AssetDesk.Api`:
```bash
dotnet ef migrations add AddSoftwareLicences
```
Expected: `Done.` and a new `Migrations/<timestamp>_AddSoftwareLicences.cs`.

- [ ] **Step 12: Read the generated PostgreSQL SQL**

Run from `src/AssetDesk.Api` (no `--no-build`):
```bash
dotnet ef migrations script AddGoodsReceiptRequestId AddSoftwareLicences
```
Confirm, by reading the output:
- `CREATE TABLE "SoftwareLicences"`, `"LicenceEntitlements"`, `"LicenceSeatAssignments"` are all present.
- `"Cost" numeric(18,2)`, `"ExchangeRate" numeric(18,6)`, `"Currency" character varying(3) NOT NULL DEFAULT 'PHP'`.
- `CONSTRAINT "CK_LicenceSeatAssignments_ExactlyOneTarget" CHECK (("UserId" IS NULL) <> ("AssetId" IS NULL))`.
- `CREATE UNIQUE INDEX "IX_LicenceSeatAssignments_ActiveUserSeat" ... WHERE "ReleasedAt" IS NULL AND "UserId" IS NOT NULL` and the `AssetSeat` equivalent.
- `CREATE UNIQUE INDEX "IX_LicenceEntitlements_GoodsReceiptLineId"`.
- `"LicenceSeatAssignments"` → `"Assets"` foreign key is `ON DELETE CASCADE`; every other new foreign key is `ON DELETE RESTRICT`.
- No `ALTER` or `DROP` against any existing table.

If the snapshot also emits an `AlterColumn` dropping the database default on `"GoodsReceipts"."RequestId"`, that is expected and harmless: that column's migration set a default the model does not declare. Leave it in.

- [ ] **Step 13: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 448 passed, 0 failed (418 + 22 + 8).

- [ ] **Step 14: Commit**

```bash
git add src/AssetDesk.Shared/LicenceModels.cs src/AssetDesk.Api/Entities/SoftwareLicence.cs src/AssetDesk.Api/Entities/LicenceRules.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Migrations tests/AssetDesk.Api.Tests/LicenceRulesTests.cs tests/AssetDesk.Api.Tests/LicenceSchemaTests.cs
git commit -m "feat(licences): licence, entitlement and seat entities with their schema rules"
```
(with the attribution line from Global Constraints as the message's last line)

---

### Task 2: Permissions and backfill

**Files:**
- Modify: `src/AssetDesk.Api/Authorization/Permissions.cs`
- Modify: `src/AssetDesk.Api/Program.cs:149` (after `CanManageProcurement`)
- Create: migration `GrantLicencePermissions` (generated, then hand-written body)
- Test: `tests/AssetDesk.Api.Tests/PermissionCatalogTests.cs`

**Interfaces:**
- Produces: `Permissions.LicencesView` = `"iams:licences:view"`, `Permissions.LicencesManage` = `"iams:licences:manage"`, `Permissions.LicenceKeysReveal` = `"iams:licences:reveal"`; policies `CanViewLicences`, `CanManageLicences`, `CanRevealLicenceKeys`.

- [ ] **Step 1: Write the failing tests**

In `tests/AssetDesk.Api.Tests/PermissionCatalogTests.cs`, change the grant-count theory's data to:

```csharp
    [InlineData(Roles.Staff, 18)]
    [InlineData(Roles.Auditor, 6)]
```

and add, before `UnknownRole_GetsNothing`:

```csharp
    [Fact]
    public void Staff_RunsLicencesAndCanRevealKeys()
    {
        // Installing the software is the Staff role's job, and it needs the key to do it. Every
        // reveal is audited, and a tenant can revoke this per role.
        var staff = Permissions.DefaultsFor(Roles.Staff);
        Assert.Contains(Permissions.LicencesView, staff);
        Assert.Contains(Permissions.LicencesManage, staff);
        Assert.Contains(Permissions.LicenceKeysReveal, staff);
    }

    [Fact]
    public void Auditor_SeesLicencesButCannotChangeThemOrReadKeys()
    {
        var auditor = Permissions.DefaultsFor(Roles.Auditor);
        Assert.Contains(Permissions.LicencesView, auditor);
        Assert.DoesNotContain(Permissions.LicencesManage, auditor);
        Assert.DoesNotContain(Permissions.LicenceKeysReveal, auditor);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~PermissionCatalogTests"`
Expected: build FAILS - `'Permissions' does not contain a definition for 'LicencesView'`.

- [ ] **Step 3: Add the keys**

In `src/AssetDesk.Api/Authorization/Permissions.cs`, after `public const string ProcurementManage = "iams:procurement:manage";`:

```csharp

    public const string LicencesView = "iams:licences:view";
    public const string LicencesManage = "iams:licences:manage";
    public const string LicenceKeysReveal = "iams:licences:reveal";
```

In `All`, after the `ProcurementManage` descriptor:

```csharp

        new(LicencesView, "Licences", "View licences",
            "See software licences, who holds their seats, and when they renew."),
        new(LicencesManage, "Licences", "Manage licences",
            "Create and edit licences, record seats bought, and assign or release seats."),
        new(LicenceKeysReveal, "Licences", "Reveal licence keys",
            "Show a licence key in full. Every reveal is recorded in the audit trail."),
```

In `DefaultsFor`, change the Staff bundle's last line to:

```csharp
            ProcurementView, ProcurementManage,
            LicencesView, LicencesManage, LicenceKeysReveal,
```

and the Auditor bundle to:

```csharp
        Roles.Auditor => [AssignmentsView, TicketsFile, ReportsView, AuditView, ProcurementView, LicencesView],
```

- [ ] **Step 4: Add the policies**

In `src/AssetDesk.Api/Program.cs`, after `.RequirePermission("CanManageProcurement", Permissions.ProcurementManage)`:

```csharp
    .RequirePermission("CanViewLicences", Permissions.LicencesView)
    .RequirePermission("CanManageLicences", Permissions.LicencesManage)
    .RequirePermission("CanRevealLicenceKeys", Permissions.LicenceKeysReveal)
```

- [ ] **Step 5: Run the catalogue tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~PermissionCatalogTests|FullyQualifiedName~RolePermissionSeedTests"`
Expected: PASS.

- [ ] **Step 6: Generate the migration shell**

Run from `src/AssetDesk.Api`:
```bash
dotnet ef migrations add GrantLicencePermissions
```
Expected: an empty `Up`/`Down` - no model changed.

- [ ] **Step 7: Write the backfill**

Replace the generated class body in `Migrations/<timestamp>_GrantLicencePermissions.cs` with:

```csharp
    /// <summary>
    /// Backfills iams:licences:view, iams:licences:manage and iams:licences:reveal onto existing
    /// tenants.
    ///
    /// SeedData.EnsureRolePermissionsAsync is gated on Tenant.RolePermissionsSeededAt and never
    /// re-runs, so a key added to Permissions.All today reaches no tenant provisioned before today.
    /// Without this, every existing Admin would silently lack these permissions and the licence
    /// screens would 403 for everyone.
    ///
    /// Backfilling unconditionally is safe only because all three keys are brand new: no tenant can
    /// have revoked something that did not exist. A migration for a key that renames or splits an
    /// existing one must not copy this blindly.
    /// </summary>
    public partial class GrantLicencePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant rows
            // are the cross product of tenants and the built-in roles that hold each key - not a
            // join on RoleId. md5(...)::uuid rather than gen_random_uuid(), which is only built in
            // from PostgreSQL 13 on a database supplied externally with no version pinned; being
            // deterministic also makes the insert idempotent alongside the NOT EXISTS guard.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:view')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:view'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff', 'Auditor')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:view'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:manage'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:reveal')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:reveal'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:reveal'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes each key entirely, including grants a tenant made to a custom role after this
            // ran: rolling back to a catalogue without these keys would otherwise leave phantom
            // ticks in the /admin/roles matrix for permissions no policy checks.
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions""
                WHERE ""Permission"" IN ('iams:licences:view', 'iams:licences:manage', 'iams:licences:reveal');
            ");
        }
    }
```

Keep the generated `using Microsoft.EntityFrameworkCore.Migrations;`, `#nullable disable` and `namespace AssetDesk.Api.Migrations` lines around it.

This SQL is PostgreSQL-only (`md5(...)::uuid`) and cannot run on the SQLite test database. It is verified against PostgreSQL before merge.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 450 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Authorization/Permissions.cs src/AssetDesk.Api/Program.cs src/AssetDesk.Api/Migrations tests/AssetDesk.Api.Tests/PermissionCatalogTests.cs
git commit -m "feat(licences): permissions to view, manage and reveal licence keys"
```

---

### Task 3: Audit the licence tables and redact the key

**Files:**
- Modify: `src/AssetDesk.Api/Data/AuditSaveChangesInterceptor.cs:48-53` (sets) and `:289-307` (`SerialiseChanges`)
- Modify: `src/AssetDesk.Api/Entities/AuditLog.cs` (`AuditActions`)
- Test: `tests/AssetDesk.Api.Tests/LicenceAuditTests.cs`

**Interfaces:**
- Consumes: the entities from Task 1.
- Produces: `AuditActions.LicenceKeyRevealed` = `"LicenceKeyRevealed"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/LicenceAuditTests.cs`:

```csharp
using System.Text.Json;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

public class LicenceAuditTests
{
    private const string OldKey = "AAAAA-BBBBB-CCCCC-OLD1";
    private const string NewKey = "DDDDD-EEEEE-FFFFF-NEW2";

    private sealed class StubUser(string? id) : ICurrentUserAccessor
    {
        public string? GetUserId() => id;
    }

    private static (AppDbContext Db, SqliteConnection Conn) CreateAudited()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(
                new StubUser("admin-1"), NullLogger<AuditSaveChangesInterceptor>.Instance))
            .Options;

        var db = new AppDbContext(options, new FakeTenantProvider(null, isSuperAdmin: true));
        db.Database.EnsureCreated();
        return (db, connection);
    }

    [Fact]
    public async Task Changing_a_licence_key_is_recorded_without_either_value()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Adobe Acrobat Pro", LicenceModel = LicenceModels.PerUser,
                LicenceKey = OldKey
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            licence.LicenceKey = NewKey;
            licence.Notes = "Rotated after staff departure";
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(
                a => a.EntityType == nameof(SoftwareLicence) && a.Action == AuditActions.Updated);

            Assert.DoesNotContain(OldKey, update.Changes);
            Assert.DoesNotContain(NewKey, update.Changes);

            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("from").GetString());
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("to").GetString());

            // Redaction is for the key only - the rest of the row is still audited normally.
            Assert.Equal("Rotated after staff departure", changes["Notes"].GetProperty("to").GetString());
        }
    }

    [Fact]
    public async Task Setting_a_key_where_there_was_none_records_only_that_one_now_exists()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Adobe Acrobat Pro", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            licence.LicenceKey = NewKey;
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            Assert.Equal(JsonValueKind.Null, changes["LicenceKey"].GetProperty("from").ValueKind);
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("to").GetString());
            Assert.DoesNotContain(NewKey, update.Changes);
        }
    }

    [Fact]
    public async Task Assigning_and_releasing_a_seat_are_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Microsoft 365", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var seat = new LicenceSeatAssignment
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, UserId = "user-1", AssignedByUserId = "admin-1"
            };
            db.LicenceSeatAssignments.Add(seat);
            await db.SaveChangesAsync();

            seat.ReleasedAt = DateTime.UtcNow;
            seat.ReleasedByUserId = "admin-1";
            await db.SaveChangesAsync();

            var entries = await db.AuditLogs
                .Where(a => a.EntityType == nameof(LicenceSeatAssignment))
                .OrderBy(a => a.Id)
                .ToListAsync();

            Assert.Equal(new[] { AuditActions.Created, AuditActions.Updated }, entries.Select(e => e.Action));
            Assert.All(entries, e => Assert.Equal(seat.Id.ToString(), e.EntityId));
        }
    }

    [Fact]
    public async Task Recording_an_entitlement_is_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Microsoft 365", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceEntitlements.Add(new LicenceEntitlement
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, SeatsAdded = 50, Cost = 35000m,
                CreatedByUserId = "admin-1"
            });
            await db.SaveChangesAsync();

            Assert.Single(await db.AuditLogs
                .Where(a => a.EntityType == nameof(LicenceEntitlement) && a.Action == AuditActions.Created)
                .ToListAsync());
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceAuditTests"`
Expected: FAIL - `SingleAsync` finds no audit rows for the licence types (`Sequence contains no elements`), and nothing redacts the key.

- [ ] **Step 3: Audit the types and redact the key**

In `src/AssetDesk.Api/Data/AuditSaveChangesInterceptor.cs`, replace the `AuditedTypes` and `IgnoredProperties` declarations with:

```csharp
    private static readonly HashSet<string> AuditedTypes =
    [
        nameof(Asset), nameof(AssetAssignment), nameof(Ticket), nameof(TicketComment), nameof(TicketAttachment),
        nameof(SoftwareLicence), nameof(LicenceEntitlement), nameof(LicenceSeatAssignment)
    ];

    // Noise, or already captured by the audited fields themselves.
    private static readonly HashSet<string> IgnoredProperties =
        ["CreatedAt", "UpdatedAt"];

    /// <summary>
    /// Secrets. A change to one is recorded - who rotated the key, and when, is exactly what an
    /// audit trail is for - but never its value, for the reason ApplicationUser is kept out of
    /// AuditedTypes altogether: Changes is a table that only grows, readable by anyone holding
    /// iams:audit:view.
    /// </summary>
    private static readonly HashSet<string> RedactedProperties =
        [nameof(SoftwareLicence.LicenceKey)];

    private const string RedactedValue = "[redacted]";
```

In `SerialiseChanges`, replace the body of the `foreach` with:

```csharp
            if (!property.IsModified) continue;
            if (IgnoredProperties.Contains(property.Metadata.Name)) continue;
            if (Equals(property.OriginalValue, property.CurrentValue)) continue;

            // Compared above on the real values, so rotating one key to another is still recorded
            // as a change even though both sides serialise identically. Null stays null: whether a
            // key exists is not the secret.
            if (RedactedProperties.Contains(property.Metadata.Name))
            {
                changes[property.Metadata.Name] = new
                {
                    from = property.OriginalValue is null ? null : RedactedValue,
                    to = property.CurrentValue is null ? null : RedactedValue
                };
                continue;
            }

            changes[property.Metadata.Name] = new
            {
                from = CapValue(property.OriginalValue),
                to = CapValue(property.CurrentValue)
            };
```

- [ ] **Step 4: Add the reveal action**

In `src/AssetDesk.Api/Entities/AuditLog.cs`, add inside `AuditActions` after `PasswordResetSent`:

```csharp

    /// Someone read a licence key in full. Written explicitly by the reveal endpoint: reading a key
    /// changes no row, so the automatic change log would never see it.
    public const string LicenceKeyRevealed = "LicenceKeyRevealed";
```

- [ ] **Step 5: Run the audit tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceAuditTests|FullyQualifiedName~AuditLogTests"`
Expected: PASS - the 4 new tests, and every existing `AuditLogTests` test unchanged.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 454 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Data/AuditSaveChangesInterceptor.cs src/AssetDesk.Api/Entities/AuditLog.cs tests/AssetDesk.Api.Tests/LicenceAuditTests.cs
git commit -m "feat(licences): audit licence changes with the key redacted"
```

---
### Task 4: Licences API - register, masked keys, reveal, renewal count

**Files:**
- Create: `src/AssetDesk.Shared/DTOs/LicenceDto.cs`
- Create: `src/AssetDesk.Api/Services/LicenceUsageReader.cs`
- Create: `src/AssetDesk.Api/Controllers/LicencesController.cs`
- Modify: `src/AssetDesk.Api/Program.cs:173` (register the reader after `IGoodsReceiptService`)
- Test: `tests/AssetDesk.Api.Tests/LicenceTestKit.cs`, `tests/AssetDesk.Api.Tests/LicenceApiTests.cs`

**Interfaces:**
- Consumes: Task 1 entities and `LicenceRules`; Task 2 policy names; Task 3 `AuditActions.LicenceKeyRevealed`.
- Produces:
  - DTOs `SoftwareLicenceDto`, `SoftwareLicenceDetailDto`, `LicenceEntitlementDto`, `LicenceSeatDto`, `UpsertSoftwareLicenceDto`, `RevealedLicenceKeyDto` (shapes below)
  - `record LicenceUsage(int SeatsOwned, int SeatsAssigned, int Reclaimable, decimal SpendInPesos)` with `int OverAssigned` and `static LicenceUsage None`
  - `ILicenceUsageReader.ReadAsync(Guid tenantId, int? licenceId = null, CancellationToken ct = default) : Task<Dictionary<int, LicenceUsage>>`
  - `LicencesController(AppDbContext db, ITenantProvider tenantProvider, ILicenceUsageReader usage)` with `GetAll`, `GetById`, `Create`, `Update`, `Deactivate`, `RevealKey`, `GetRenewalCount`, and the private `DetailAsync(Guid tenantId, int id, CancellationToken ct) : Task<SoftwareLicenceDetailDto?>` and `CurrentUserId` that Tasks 5 and 6 reuse
  - Test helpers in `LicenceTestKit` used by Tasks 5, 6 and 8

- [ ] **Step 1: Write the DTOs**

Create `src/AssetDesk.Shared/DTOs/LicenceDto.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record SoftwareLicenceDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public int? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public required string LicenceModel { get; init; }

    /// <summary>
    /// The only form of the key any read carries - e.g. "****-X7Q2", or null when there is none.
    /// The full key comes only from the audited reveal endpoint.
    /// </summary>
    public string? MaskedKey { get; init; }

    public DateTime? ExpiresAt { get; init; }
    public required string RenewalStatus { get; init; }
    public int? DaysUntilExpiry { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }

    public int SeatsOwned { get; init; }
    public int SeatsAssigned { get; init; }

    /// <summary>How far assignments exceed what is owned. Allowed and recorded, never blocked.</summary>
    public int OverAssigned { get; init; }

    /// <summary>Active seats held by a deactivated person or a retired or lost device.</summary>
    public int Reclaimable { get; init; }

    /// <summary>True once any seat exists; the licence model cannot change after that.</summary>
    public bool ModelLocked { get; init; }
}

public record SoftwareLicenceDetailDto
{
    public required SoftwareLicenceDto Licence { get; init; }
    public decimal SpendInPesos { get; init; }

    /// <summary>Newest first.</summary>
    public List<LicenceEntitlementDto> Entitlements { get; init; } = [];

    /// <summary>Active seats first, then released ones, each group newest first.</summary>
    public List<LicenceSeatDto> Seats { get; init; } = [];
}

public record LicenceEntitlementDto
{
    public int Id { get; init; }
    public int SeatsAdded { get; init; }
    public decimal Cost { get; init; }
    public required string Currency { get; init; }
    public decimal ExchangeRate { get; init; }
    public decimal CostInPesos { get; init; }
    public DateTime EntitlementDate { get; init; }
    public DateTime? ExpiresAtAfter { get; init; }
    public int? PurchaseOrderId { get; init; }
    public string? PurchaseOrderReference { get; init; }
    public string? CreatedByName { get; init; }
    public string? Notes { get; init; }
}

public record LicenceSeatDto
{
    public int Id { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public int? AssetId { get; init; }
    public string? AssetTag { get; init; }
    public string? AssetName { get; init; }
    public string? AssetStatus { get; init; }
    public DateTime AssignedAt { get; init; }
    public DateTime? ReleasedAt { get; init; }
    public string? AssignedByName { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public bool IsReclaimable { get; init; }
}

public record UpsertSoftwareLicenceDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)]
    public string? Publisher { get; init; }

    public int? SupplierId { get; init; }

    [Required]
    public string LicenceModel { get; init; } = "PerUser";

    /// <summary>
    /// Null or blank keeps the stored key - the form never receives the key, so it cannot send it
    /// back. A value replaces it; ClearKey removes it.
    /// </summary>
    [StringLength(500)]
    public string? LicenceKey { get; init; }

    public bool ClearKey { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

public record RevealedLicenceKeyDto
{
    public required string LicenceKey { get; init; }
}
```

- [ ] **Step 2: Write the test kit**

Create `tests/AssetDesk.Api.Tests/LicenceTestKit.cs`:

```csharp
using System.Security.Claims;
using System.Text.Json;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

internal static class LicenceTestKit
{
    public const string ActingUserId = "admin-1";

    public static LicencesController Controller(
        AppDbContext db, ITenantProvider tenants, string userId = ActingUserId) =>
        new(db, tenants, new LicenceUsageReader(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
                }
            }
        };

    public static async Task<SoftwareLicence> SeedLicenceAsync(
        AppDbContext db, Guid tenantId,
        string name = "Microsoft 365 Business Standard",
        string model = LicenceModels.PerUser,
        string? key = null,
        DateTime? expiresAt = null,
        bool isActive = true)
    {
        var licence = new SoftwareLicence
        {
            TenantId = tenantId, Name = name, LicenceModel = model,
            LicenceKey = key, ExpiresAt = expiresAt, IsActive = isActive
        };
        db.SoftwareLicences.Add(licence);
        await db.SaveChangesAsync();
        return licence;
    }

    public static async Task SeedEntitlementAsync(
        AppDbContext db, Guid tenantId, int licenceId, int seats,
        decimal cost = 0m, string currency = Currencies.PHP, decimal rate = 1m)
    {
        db.LicenceEntitlements.Add(new LicenceEntitlement
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, SeatsAdded = seats,
            Cost = cost, Currency = currency, ExchangeRate = rate, CreatedByUserId = ActingUserId
        });
        await db.SaveChangesAsync();
    }

    public static async Task<LicenceSeatAssignment> SeedUserSeatAsync(
        AppDbContext db, Guid tenantId, int licenceId, string userId)
    {
        var seat = new LicenceSeatAssignment
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, UserId = userId, AssignedByUserId = ActingUserId
        };
        db.LicenceSeatAssignments.Add(seat);
        await db.SaveChangesAsync();
        return seat;
    }

    public static async Task<LicenceSeatAssignment> SeedDeviceSeatAsync(
        AppDbContext db, Guid tenantId, int licenceId, int assetId)
    {
        var seat = new LicenceSeatAssignment
        {
            TenantId = tenantId, SoftwareLicenceId = licenceId, AssetId = assetId, AssignedByUserId = ActingUserId
        };
        db.LicenceSeatAssignments.Add(seat);
        await db.SaveChangesAsync();
        return seat;
    }

    /// <summary>The ApiResponse payload of a successful action.</summary>
    public static T Data<T>(ActionResult<ApiResponse<T>> result) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Data!;

    /// <summary>The refusal message of a failed action.</summary>
    public static string? Message<T>(ActionResult<ApiResponse<T>> result) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Message;

    /// <summary>
    /// Everything the action would put on the wire. Asserting a secret is absent from this, rather
    /// than from one named property, is what catches a key that leaks through a field nobody
    /// thought to check.
    /// </summary>
    public static string Wire<T>(ActionResult<ApiResponse<T>> result) =>
        JsonSerializer.Serialize(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
}
```

- [ ] **Step 3: Write the failing API tests**

Create `tests/AssetDesk.Api.Tests/LicenceApiTests.cs`:

```csharp
using System.Reflection;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceApiTests
{
    private const string Key = "XXXXX-YYYYY-ZZZZZ-X7Q2";

    private static UpsertSoftwareLicenceDto Upsert(
        string name = "Microsoft 365 Business Standard", string model = LicenceModels.PerUser) => new()
    {
        Name = name,
        Publisher = "Microsoft",
        LicenceModel = model
    };

    [Fact]
    public async Task A_licence_is_created_against_the_callers_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .Create(Upsert() with { LicenceKey = Key }, default);

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.SoftwareLicences.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal(Key, saved.LicenceKey);
            Assert.Equal("****-X7Q2", Data(result).Licence.MaskedKey);
        }
    }

    [Fact]
    public async Task A_duplicate_licence_name_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).Create(Upsert(), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("A licence named 'Microsoft 365 Business Standard' already exists.", Message(result));
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task An_unknown_licence_model_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .Create(Upsert(model: "Concurrent"), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.SoftwareLicences);
        }
    }

    [Fact]
    public async Task A_licence_cannot_name_another_tenants_supplier()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = new Supplier { TenantId = tenantB, Name = "Their Reseller" };
            db.Suppliers.Add(theirs);
            await db.SaveChangesAsync();

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .Create(Upsert() with { SupplierId = theirs.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Supplier not found.", Message(result));
            Assert.Empty(db.SoftwareLicences.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task No_read_puts_the_full_key_on_the_wire()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var list = await controller.GetAll(default);
            var detail = await controller.GetById(licence.Id, default);

            Assert.DoesNotContain(Key, Wire(list));
            Assert.DoesNotContain(Key, Wire(detail));
            Assert.Contains("****-X7Q2", Wire(detail));
        }
    }

    [Fact]
    public async Task Saving_without_a_key_keeps_it_a_new_key_replaces_it_and_clearing_removes_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            await controller.Update(licence.Id, Upsert() with { Notes = "Renewed through reseller" }, default);
            Assert.Equal(Key, (await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);

            await controller.Update(licence.Id, Upsert() with { LicenceKey = "NEWKEY-0000-9999" }, default);
            Assert.Equal("NEWKEY-0000-9999", (await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);

            await controller.Update(licence.Id, Upsert() with { ClearKey = true }, default);
            Assert.Null((await db.SoftwareLicences.AsNoTracking().SingleAsync()).LicenceKey);
        }
    }

    [Fact]
    public async Task The_licence_model_is_fixed_once_a_seat_exists()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var unused = await SeedLicenceAsync(db, tenantId, "Zoom Workplace");
            var inUse = await SeedLicenceAsync(db, tenantId);
            var seat = await SeedUserSeatAsync(db, tenantId, inUse.Id, "user-1");

            // A released seat still counts: it points at a person, and a per-device licence
            // would leave that history pointing at the wrong kind of holder.
            seat.ReleasedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var changedFreely = await controller.Update(
                unused.Id, Upsert("Zoom Workplace", LicenceModels.PerDevice), default);
            Assert.IsType<OkObjectResult>(changedFreely.Result);

            var refused = await controller.Update(
                inUse.Id, Upsert(model: LicenceModels.PerDevice), default);
            Assert.IsType<BadRequestObjectResult>(refused.Result);
            Assert.Equal(LicenceModels.PerUser,
                (await db.SoftwareLicences.AsNoTracking().SingleAsync(l => l.Id == inUse.Id)).LicenceModel);
        }
    }

    [Fact]
    public async Task The_list_reports_seats_owned_assigned_over_and_reclaimable()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var departed = await TestDb.SeedUserAsync(db, tenantId, "user-3", "Ana Cruz");
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 2);
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-1");
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-2");
            await SeedUserSeatAsync(db, tenantId, licence.Id, "user-3");
            departed.IsActive = false;
            await db.SaveChangesAsync();

            var row = Data(await Controller(db, new FakeTenantProvider(tenantId)).GetAll(default)).Single();

            Assert.Equal(2, row.SeatsOwned);
            Assert.Equal(3, row.SeatsAssigned);
            Assert.Equal(1, row.OverAssigned);
            Assert.Equal(1, row.Reclaimable);
            Assert.True(row.ModelLocked);
        }
    }

    [Fact]
    public async Task Revealing_a_key_returns_it_and_records_who_saw_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, key: Key);

            var result = await Controller(db, new FakeTenantProvider(tenantId), userId: "staff-7")
                .RevealKey(licence.Id, default);

            Assert.Equal(Key, Data(result).LicenceKey);
            var entry = await db.AuditLogs.SingleAsync();
            Assert.Equal(AuditActions.LicenceKeyRevealed, entry.Action);
            Assert.Equal(nameof(SoftwareLicence), entry.EntityType);
            Assert.Equal(licence.Id.ToString(), entry.EntityId);
            Assert.Equal("staff-7", entry.UserId);
            Assert.Equal(tenantId, entry.TenantId);
            Assert.Null(entry.Changes);
        }
    }

    [Fact]
    public async Task Another_tenants_key_cannot_be_revealed()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB, key: Key);

            // Super admin, current tenant A: the global filter admits tenant B's licence, so only
            // the explicit tenant predicate stands between this caller and the key.
            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .RevealKey(theirs.Id, default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.DoesNotContain(Key, Wire(result));
            Assert.Empty(db.AuditLogs.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Another_tenants_licence_cannot_be_updated_or_deactivated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB);
            var controller = Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true));

            var update = await controller.Update(theirs.Id, Upsert("Renamed"), default);
            var deactivate = await controller.Deactivate(theirs.Id, default);

            Assert.IsType<NotFoundObjectResult>(update.Result);
            Assert.IsType<NotFoundObjectResult>(deactivate.Result);
            var unchanged = await db.SoftwareLicences.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal("Microsoft 365 Business Standard", unchanged.Name);
            Assert.True(unchanged.IsActive);
        }
    }

    [Fact]
    public async Task Deactivating_a_licence_keeps_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            await Controller(db, new FakeTenantProvider(tenantId)).Deactivate(licence.Id, default);

            Assert.False((await db.SoftwareLicences.AsNoTracking().SingleAsync()).IsActive);
        }
    }

    [Fact]
    public async Task The_renewal_count_includes_only_active_licences_that_are_due_or_expired()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.Date;
            await SeedLicenceAsync(db, tenantId, "Expired", expiresAt: today.AddDays(-3));
            await SeedLicenceAsync(db, tenantId, "Due", expiresAt: today.AddDays(30));
            await SeedLicenceAsync(db, tenantId, "Active", expiresAt: today.AddDays(200));
            await SeedLicenceAsync(db, tenantId, "Perpetual");
            await SeedLicenceAsync(db, tenantId, "Retired and expired", expiresAt: today.AddDays(-10), isActive: false);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).GetRenewalCount(default);

            Assert.Equal(2, Assert.IsType<OkObjectResult>(result.Result).Value);
        }
    }

    [Fact]
    public async Task A_caller_with_no_organisation_is_refused()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var result = await Controller(db, new FakeTenantProvider(null, isSuperAdmin: true)).GetAll(default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Select an organisation first.", Message(result));
        }
    }

    [Theory]
    [InlineData(nameof(LicencesController.Create), "CanManageLicences")]
    [InlineData(nameof(LicencesController.Update), "CanManageLicences")]
    [InlineData(nameof(LicencesController.Deactivate), "CanManageLicences")]
    [InlineData(nameof(LicencesController.RevealKey), "CanRevealLicenceKeys")]
    public void Each_write_is_gated_on_its_own_policy(string action, string policy)
    {
        var method = typeof(LicencesController).GetMethod(action)!;
        Assert.Equal(policy, method.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.Equal("CanViewLicences", typeof(LicencesController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceApiTests"`
Expected: build FAILS - `The type or namespace name 'LicencesController' could not be found`.

- [ ] **Step 5: Write the usage reader**

Create `src/AssetDesk.Api/Services/LicenceUsageReader.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public sealed record LicenceUsage(int SeatsOwned, int SeatsAssigned, int Reclaimable, decimal SpendInPesos)
{
    public static readonly LicenceUsage None = new(0, 0, 0, 0m);

    public int OverAssigned => Math.Max(0, SeatsAssigned - SeatsOwned);
}

public interface ILicenceUsageReader
{
    /// <summary>Usage per licence id, for every licence in the tenant that has any, or just one.</summary>
    Task<Dictionary<int, LicenceUsage>> ReadAsync(
        Guid tenantId, int? licenceId = null, CancellationToken ct = default);
}

/// <summary>
/// The one place seats owned, seats assigned, reclaimable seats and spend are worked out. The
/// licence screens and the compliance report both read through here, so the two cannot disagree
/// about what "over-assigned" means.
/// </summary>
public class LicenceUsageReader(AppDbContext db) : ILicenceUsageReader
{
    public async Task<Dictionary<int, LicenceUsage>> ReadAsync(
        Guid tenantId, int? licenceId = null, CancellationToken ct = default)
    {
        var entitlements = await db.LicenceEntitlements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && (licenceId == null || e.SoftwareLicenceId == licenceId))
            .Select(e => new { e.SoftwareLicenceId, e.SeatsAdded, e.Cost, e.ExchangeRate })
            .ToListAsync(ct);

        var activeSeats = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                     && s.ReleasedAt == null
                     && (licenceId == null || s.SoftwareLicenceId == licenceId))
            .Select(s => new
            {
                s.SoftwareLicenceId,
                UserIsActive = s.User == null ? (bool?)null : s.User.IsActive,
                AssetStatus = s.Asset == null ? null : s.Asset.Status
            })
            .ToListAsync(ct);

        // Spend is summed here rather than in SQL: SQLite, which the test suite runs on, cannot
        // aggregate a decimal, and the report and the screens must agree to the centavo.
        var owned = entitlements.ToLookup(e => e.SoftwareLicenceId);
        var seats = activeSeats.ToLookup(s => s.SoftwareLicenceId);

        return owned.Select(g => g.Key)
            .Union(seats.Select(g => g.Key))
            .ToDictionary(id => id, id => new LicenceUsage(
                SeatsOwned: owned[id].Sum(e => e.SeatsAdded),
                SeatsAssigned: seats[id].Count(),
                Reclaimable: seats[id].Count(s => LicenceRules.IsReclaimable(s.UserIsActive, s.AssetStatus)),
                SpendInPesos: owned[id].Sum(e => e.Cost * e.ExchangeRate)));
    }
}
```

- [ ] **Step 6: Write the controller**

Create `src/AssetDesk.Api/Controllers/LicencesController.cs`:

```csharp
using System.Security.Claims;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// Every query filters on the tenant explicitly rather than trusting the global query filter,
/// which has an IsSuperAdmin() bypass. The depreciation feature shipped a Critical for leaning on
/// that filter, and procurement had to be corrected for the same shape twice.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewLicences")]
public class LicencesController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILicenceUsageReader usage) : ControllerBase
{
    private string CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated user is required.");

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<SoftwareLicenceDto>>>> GetAll(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<SoftwareLicenceDto>>.Fail("Select an organisation first."));

        var licences = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Include(l => l.Supplier)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

        var usageByLicence = await usage.ReadAsync(tenantId, ct: ct);
        var locked = await LicencesWithSeatsAsync(tenantId, ct);
        var today = DateTime.UtcNow;

        return Ok(ApiResponse<List<SoftwareLicenceDto>>.Ok(licences
            .Select(l => Map(l, usageByLicence.GetValueOrDefault(l.Id, LicenceUsage.None), locked.Contains(l.Id), today))
            .ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> GetById(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        return await DetailAsync(tenantId, id, ct) is { } detail
            ? Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok(detail))
            : NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));
    }

    [HttpPost]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Create(
        UpsertSoftwareLicenceDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        if (await ValidateAsync(tenantId, dto, existing: null, ct) is { } error)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(error));

        var licence = new SoftwareLicence
        {
            TenantId = tenantId,
            Name = dto.Name.Trim(),
            Publisher = TrimToNull(dto.Publisher),
            SupplierId = dto.SupplierId,
            LicenceModel = dto.LicenceModel,
            LicenceKey = dto.ClearKey ? null : TrimToNull(dto.LicenceKey),
            ExpiresAt = dto.ExpiresAt,
            Notes = TrimToNull(dto.Notes),
            IsActive = dto.IsActive
        };

        db.SoftwareLicences.Add(licence);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = licence.Id },
            ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, licence.Id, ct))!));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Update(
        int id, UpsertSoftwareLicenceDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (await ValidateAsync(tenantId, dto, licence, ct) is { } error)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(error));

        licence.Name = dto.Name.Trim();
        licence.Publisher = TrimToNull(dto.Publisher);
        licence.SupplierId = dto.SupplierId;
        licence.LicenceModel = dto.LicenceModel;
        licence.ExpiresAt = dto.ExpiresAt;
        licence.Notes = TrimToNull(dto.Notes);
        licence.IsActive = dto.IsActive;
        licence.UpdatedAt = DateTime.UtcNow;

        // The edit form never holds the key, so a blank field means "unchanged", not "remove".
        if (dto.ClearKey)
            licence.LicenceKey = null;
        else if (TrimToNull(dto.LicenceKey) is { } newKey)
            licence.LicenceKey = newKey;

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    /// <summary>Deactivates rather than deletes - entitlements and seat history reference it.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Deactivate(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        licence.IsActive = false;
        licence.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    /// <summary>
    /// The only response that carries a full key. A POST so the key does not end up in browser
    /// history or an intermediary's access log, and the audit row is saved before the key is
    /// returned: if the record of who saw it cannot be written, nobody sees it.
    /// </summary>
    [HttpPost("{id:int}/key/reveal")]
    [Authorize(Policy = "CanRevealLicenceKeys")]
    public async Task<ActionResult<ApiResponse<RevealedLicenceKeyDto>>> RevealKey(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<RevealedLicenceKeyDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Id == id)
            .Select(l => new { l.LicenceKey })
            .FirstOrDefaultAsync(ct);

        if (licence is null)
            return NotFound(ApiResponse<RevealedLicenceKeyDto>.Fail("Licence not found."));
        if (string.IsNullOrEmpty(licence.LicenceKey))
            return NotFound(ApiResponse<RevealedLicenceKeyDto>.Fail("No key is recorded for this licence."));

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            EntityType = nameof(SoftwareLicence),
            EntityId = id.ToString(),
            Action = AuditActions.LicenceKeyRevealed,
            UserId = CurrentUserId,
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<RevealedLicenceKeyDto>.Ok(new RevealedLicenceKeyDto { LicenceKey = licence.LicenceKey }));
    }

    /// <summary>
    /// A bare count for the nav badge, in the shape api/warrantyalerts/count already returns. With
    /// no organisation selected there is nothing to renew, so it is zero rather than a refusal the
    /// layout would have to handle on every page.
    /// </summary>
    [HttpGet("renewals/count")]
    public async Task<ActionResult<int>> GetRenewalCount(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return Ok(0);

        var expiries = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive && l.ExpiresAt != null)
            .Select(l => l.ExpiresAt)
            .ToListAsync(ct);

        var today = DateTime.UtcNow;
        return Ok(expiries.Count(e => LicenceRules.NeedsRenewalAttention(LicenceRules.RenewalStatus(e, today))));
    }

    private async Task<string?> ValidateAsync(
        Guid tenantId, UpsertSoftwareLicenceDto dto, SoftwareLicence? existing, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "A licence needs a name.";

        if (!LicenceModels.IsValid(dto.LicenceModel))
            return "Choose whether the licence is counted per user or per device.";

        if (await db.SoftwareLicences.AnyAsync(
                l => l.TenantId == tenantId && l.Name == name && (existing == null || l.Id != existing.Id), ct))
            return $"A licence named '{name}' already exists.";

        // An unchanged supplier is left alone even if it has since been retired - editing a
        // licence's notes must not force someone to pick a new reseller.
        if (dto.SupplierId is { } supplierId && supplierId != existing?.SupplierId)
        {
            var supplier = await db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == supplierId && s.TenantId == tenantId, ct);
            if (supplier is null)
                return "Supplier not found.";
            if (!supplier.IsActive)
                return $"Supplier '{supplier.Name}' is inactive.";
        }

        if (existing is not null
            && existing.LicenceModel != dto.LicenceModel
            && await db.LicenceSeatAssignments.AnyAsync(
                s => s.TenantId == tenantId && s.SoftwareLicenceId == existing.Id, ct))
            return "The licence model cannot change once seats have been assigned - every seat would point at the wrong kind of holder.";

        return null;
    }

    private async Task<HashSet<int>> LicencesWithSeatsAsync(Guid tenantId, CancellationToken ct) =>
        (await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.SoftwareLicenceId)
            .Distinct()
            .ToListAsync(ct))
        .ToHashSet();

    /// <summary>
    /// The detail every write returns, so a screen that has just changed a licence shows what the
    /// database now holds rather than what it sent.
    /// </summary>
    private async Task<SoftwareLicenceDetailDto?> DetailAsync(Guid tenantId, int id, CancellationToken ct)
    {
        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Id == id)
            .Include(l => l.Supplier)
            .FirstOrDefaultAsync(ct);
        if (licence is null) return null;

        var entitlements = await db.LicenceEntitlements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SoftwareLicenceId == id)
            .Select(e => new
            {
                e.Id, e.SeatsAdded, e.Cost, e.Currency, e.ExchangeRate, e.EntitlementDate,
                e.ExpiresAtAfter, e.CreatedByUserId, e.Notes,
                PurchaseOrderId = e.GoodsReceiptLine == null ? (int?)null : e.GoodsReceiptLine.GoodsReceipt!.PurchaseOrderId,
                PoNumber = e.GoodsReceiptLine == null ? (int?)null : e.GoodsReceiptLine.GoodsReceipt!.PurchaseOrder!.PoNumber
            })
            .ToListAsync(ct);

        var seats = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SoftwareLicenceId == id)
            .Select(s => new
            {
                s.Id, s.UserId, s.AssetId, s.AssignedAt, s.ReleasedAt, s.AssignedByUserId, s.Notes,
                UserName = s.User == null ? null : s.User.FullName,
                UserIsActive = s.User == null ? (bool?)null : s.User.IsActive,
                AssetTag = s.Asset == null ? null : s.Asset.AssetTag,
                AssetName = s.Asset == null ? null : s.Asset.Name,
                AssetStatus = s.Asset == null ? null : s.Asset.Status
            })
            .ToListAsync(ct);

        var names = await UserNamesAsync(
            entitlements.Select(e => e.CreatedByUserId).Concat(seats.Select(s => s.AssignedByUserId)), ct);
        var licenceUsage = (await usage.ReadAsync(tenantId, id, ct)).GetValueOrDefault(id, LicenceUsage.None);
        var today = DateTime.UtcNow;

        return new SoftwareLicenceDetailDto
        {
            Licence = Map(licence, licenceUsage, modelLocked: seats.Count > 0, today),
            SpendInPesos = licenceUsage.SpendInPesos,
            Entitlements = [.. entitlements
                .OrderByDescending(e => e.EntitlementDate)
                .ThenByDescending(e => e.Id)
                .Select(e => new LicenceEntitlementDto
                {
                    Id = e.Id,
                    SeatsAdded = e.SeatsAdded,
                    Cost = e.Cost,
                    Currency = e.Currency,
                    ExchangeRate = e.ExchangeRate,
                    CostInPesos = e.Cost * e.ExchangeRate,
                    EntitlementDate = e.EntitlementDate,
                    ExpiresAtAfter = e.ExpiresAtAfter,
                    PurchaseOrderId = e.PurchaseOrderId,
                    PurchaseOrderReference = e.PoNumber is { } n ? $"PO-{n:D4}" : null,
                    CreatedByName = names.GetValueOrDefault(e.CreatedByUserId),
                    Notes = e.Notes
                })],
            Seats = [.. seats
                .OrderBy(s => s.ReleasedAt is null ? 0 : 1)
                .ThenByDescending(s => s.ReleasedAt ?? s.AssignedAt)
                .Select(s => new LicenceSeatDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    UserName = s.UserName,
                    AssetId = s.AssetId,
                    AssetTag = s.AssetTag,
                    AssetName = s.AssetName,
                    AssetStatus = s.AssetStatus,
                    AssignedAt = s.AssignedAt,
                    ReleasedAt = s.ReleasedAt,
                    AssignedByName = names.GetValueOrDefault(s.AssignedByUserId),
                    Notes = s.Notes,
                    IsActive = s.ReleasedAt is null,
                    IsReclaimable = s.ReleasedAt is null && LicenceRules.IsReclaimable(s.UserIsActive, s.AssetStatus)
                })]
        };
    }

    /// <summary>
    /// ApplicationUser has no query filter, so this lookup is not tenant-scoped. What bounds it is
    /// the ids: they come off this tenant's own entitlement and seat rows.
    /// </summary>
    private async Task<Dictionary<string, string>> UserNamesAsync(IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private static SoftwareLicenceDto Map(SoftwareLicence l, LicenceUsage u, bool modelLocked, DateTime today) => new()
    {
        Id = l.Id,
        Name = l.Name,
        Publisher = l.Publisher,
        SupplierId = l.SupplierId,
        SupplierName = l.Supplier?.Name,
        LicenceModel = l.LicenceModel,
        MaskedKey = LicenceRules.Mask(l.LicenceKey),
        ExpiresAt = l.ExpiresAt,
        RenewalStatus = LicenceRules.RenewalStatus(l.ExpiresAt, today),
        DaysUntilExpiry = LicenceRules.DaysUntilExpiry(l.ExpiresAt, today),
        Notes = l.Notes,
        IsActive = l.IsActive,
        SeatsOwned = u.SeatsOwned,
        SeatsAssigned = u.SeatsAssigned,
        OverAssigned = u.OverAssigned,
        Reclaimable = u.Reclaimable,
        ModelLocked = modelLocked
    };

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] **Step 7: Register the reader**

In `src/AssetDesk.Api/Program.cs`, after `builder.Services.AddScoped<IGoodsReceiptService, GoodsReceiptService>();`:

```csharp
builder.Services.AddScoped<ILicenceUsageReader, LicenceUsageReader>();
```

- [ ] **Step 8: Run the API tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceApiTests"`
Expected: PASS, 18 tests.

- [ ] **Step 9: Prove the isolation tests hold for the right reason**

Temporarily delete `l.TenantId == tenantId && ` from the `RevealKey` query only, and run:
`dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~Another_tenants_key_cannot_be_revealed"`
Expected: FAIL (the key is returned). Restore the predicate and confirm it passes again. Record both outputs in the task report.

- [ ] **Step 10: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 472 passed, 0 failed.

- [ ] **Step 11: Commit**

```bash
git add src/AssetDesk.Shared/DTOs/LicenceDto.cs src/AssetDesk.Api/Services/LicenceUsageReader.cs src/AssetDesk.Api/Controllers/LicencesController.cs src/AssetDesk.Api/Program.cs tests/AssetDesk.Api.Tests/LicenceTestKit.cs tests/AssetDesk.Api.Tests/LicenceApiTests.cs
git commit -m "feat(licences): licence register with masked keys and an audited reveal"
```

---

### Task 5: Recording entitlements by hand

**Files:**
- Modify: `src/AssetDesk.Shared/DTOs/LicenceDto.cs` (append `AddLicenceEntitlementDto`)
- Modify: `src/AssetDesk.Api/Controllers/LicencesController.cs` (constructor gains `ILookupService lookups`; new `AddEntitlement` action)
- Modify: `tests/AssetDesk.Api.Tests/LicenceTestKit.cs` (`Controller` passes `new LookupService(db)`)
- Modify: `tests/AssetDesk.Api.Tests/LicenceApiTests.cs` (one `InlineData`)
- Test: `tests/AssetDesk.Api.Tests/LicenceEntitlementApiTests.cs`

**Interfaces:**
- Consumes: Task 4's controller, `DetailAsync`, `CurrentUserId`; `CurrencyRules.Validate(string currency, decimal rate) : string?`; `ILookupService.IsActiveValueAsync(string lookupType, string value, CancellationToken ct)`; `LookupTypes.Currency`.
- Produces: `POST api/licences/{id}/entitlements` → `LicencesController.AddEntitlement(int id, AddLicenceEntitlementDto dto, CancellationToken ct) : Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>>`; constructor `LicencesController(AppDbContext db, ITenantProvider tenantProvider, ILicenceUsageReader usage, ILookupService lookups)`.

- [ ] **Step 1: Add the DTO**

Append to `src/AssetDesk.Shared/DTOs/LicenceDto.cs`:

```csharp

public record AddLicenceEntitlementDto
{
    /// <summary>Positive adds seats, zero renews, negative records a reduction.</summary>
    [Range(-100000, 100000)]
    public int SeatsAdded { get; init; }

    [Range(0, 1000000000, ErrorMessage = "A cost cannot be negative")]
    public decimal Cost { get; init; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = "PHP";

    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal ExchangeRate { get; init; } = 1m;

    public DateTime EntitlementDate { get; init; } = DateTime.UtcNow;

    /// <summary>The licence's new expiry, when this entry renews it or dates it for the first time.</summary>
    public DateTime? ExpiresAt { get; init; }

    public string? Notes { get; init; }
}
```

- [ ] **Step 2: Update the test kit**

In `tests/AssetDesk.Api.Tests/LicenceTestKit.cs`, change the controller construction to:

```csharp
        new(db, tenants, new LicenceUsageReader(db), new LookupService(db))
```

In `tests/AssetDesk.Api.Tests/LicenceApiTests.cs`, add to `Each_write_is_gated_on_its_own_policy`:

```csharp
    [InlineData(nameof(LicencesController.AddEntitlement), "CanManageLicences")]
```

- [ ] **Step 3: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/LicenceEntitlementApiTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceEntitlementApiTests
{
    private static readonly DateTime EntryDate = new(2026, 9, 13);

    private static AddLicenceEntitlementDto Entry(int seats, DateTime? expiresAt = null, string? notes = null) => new()
    {
        SeatsAdded = seats,
        Cost = 35000m,
        Currency = Currencies.PHP,
        ExchangeRate = 1m,
        EntitlementDate = EntryDate,
        ExpiresAt = expiresAt,
        Notes = notes
    };

    [Fact]
    public async Task Adding_seats_records_the_purchase_at_its_own_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).AddEntitlement(
                licence.Id,
                Entry(50) with { Cost = 1200m, Currency = Currencies.USD, ExchangeRate = 58.20m },
                default);

            var detail = Data(result);
            Assert.Equal(50, detail.Licence.SeatsOwned);
            Assert.Equal(69840m, detail.SpendInPesos);

            var saved = await db.LicenceEntitlements.SingleAsync();
            Assert.Equal(Currencies.USD, saved.Currency);
            Assert.Equal(58.20m, saved.ExchangeRate);
            Assert.Equal(ActingUserId, saved.CreatedByUserId);
            Assert.Null(saved.GoodsReceiptLineId);
        }
    }

    [Fact]
    public async Task A_renewal_moves_the_expiry_without_adding_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, expiresAt: new DateTime(2026, 10, 1));
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var renewedTo = new DateTime(2027, 10, 1);
            var detail = Data(await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0, expiresAt: renewedTo), default));

            Assert.Equal(50, detail.Licence.SeatsOwned);
            Assert.Equal(renewedTo, detail.Licence.ExpiresAt);
            Assert.Equal(renewedTo, (await db.LicenceEntitlements.OrderBy(e => e.Id).LastAsync()).ExpiresAtAfter);
        }
    }

    [Fact]
    public async Task An_entry_that_changes_nothing_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task Removing_seats_needs_a_reason()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-10), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(1, await db.LicenceEntitlements.CountAsync());
        }
    }

    [Fact]
    public async Task A_reduction_within_what_is_owned_is_recorded()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);

            var detail = Data(await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-10, notes: "Downsized at renewal") with { Cost = 0m }, default));

            Assert.Equal(40, detail.Licence.SeatsOwned);
        }
    }

    [Fact]
    public async Task Seats_owned_cannot_go_below_zero()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(-6, notes: "Cancelled"), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Only 5 seats are owned, so 6 cannot be removed.", Message(result));
            Assert.Equal(1, await db.LicenceEntitlements.CountAsync());
        }
    }

    [Fact]
    public async Task An_expiry_on_or_before_the_entry_date_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(0, expiresAt: EntryDate), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
            Assert.Null((await db.SoftwareLicences.AsNoTracking().SingleAsync()).ExpiresAt);
        }
    }

    [Fact]
    public async Task A_foreign_currency_entry_at_parity_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId)).AddEntitlement(
                licence.Id, Entry(10) with { Currency = Currencies.USD, ExchangeRate = 1m }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task A_deactivated_licence_takes_no_entries()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, isActive: false);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AddEntitlement(licence.Id, Entry(10), default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements);
        }
    }

    [Fact]
    public async Task Another_tenants_licence_takes_no_entries()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AddEntitlement(theirs.Id, Entry(10), default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Empty(db.LicenceEntitlements.IgnoreQueryFilters());
        }
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceEntitlementApiTests"`
Expected: build FAILS - `'LicencesController' does not contain a definition for 'AddEntitlement'` (and the test kit's four-argument constructor does not exist yet).

- [ ] **Step 5: Implement**

In `src/AssetDesk.Api/Controllers/LicencesController.cs`, change the constructor to:

```csharp
public class LicencesController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILicenceUsageReader usage,
    ILookupService lookups) : ControllerBase
```

Add after `Deactivate`:

```csharp
    [HttpPost("{id:int}/entitlements")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> AddEntitlement(
        int id, AddLicenceEntitlementDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (!licence.IsActive)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"Licence '{licence.Name}' is deactivated. Reactivate it before recording seats against it."));

        if (dto.SeatsAdded == 0 && dto.ExpiresAt is null)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                "Record seats added or removed, or a new expiry date."));

        if (dto.Cost < 0)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("A cost cannot be negative."));

        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency, ct))
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail($"'{dto.Currency}' is not a valid currency."));

        if (CurrencyRules.Validate(dto.Currency, dto.ExchangeRate) is { } currencyError)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(currencyError));

        if (dto.ExpiresAt is { } expiry && expiry.Date <= dto.EntitlementDate.Date)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                "The new expiry must be after the entry's date."));

        if (dto.SeatsAdded < 0)
        {
            if (string.IsNullOrWhiteSpace(dto.Notes))
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Say why seats are being removed."));

            // Read then write, deliberately without a lock. This floor is a guard against a typo,
            // not a money invariant: two administrators reducing the same licence at the same
            // moment could take it below zero, and both reductions would sit in the ledger with
            // their authors and reasons, where the next person to open the licence sees them.
            var owned = await db.LicenceEntitlements
                .Where(e => e.TenantId == tenantId && e.SoftwareLicenceId == id)
                .SumAsync(e => e.SeatsAdded, ct);

            if (owned + dto.SeatsAdded < 0)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"Only {owned} seats are owned, so {-dto.SeatsAdded} cannot be removed."));
        }

        db.LicenceEntitlements.Add(new LicenceEntitlement
        {
            TenantId = tenantId,
            SoftwareLicenceId = id,
            SeatsAdded = dto.SeatsAdded,
            Cost = dto.Cost,
            Currency = dto.Currency,
            ExchangeRate = dto.ExchangeRate,
            EntitlementDate = dto.EntitlementDate,
            ExpiresAtAfter = dto.ExpiresAt,
            CreatedByUserId = CurrentUserId,
            Notes = TrimToNull(dto.Notes)
        });

        if (dto.ExpiresAt is { } newExpiry)
        {
            licence.ExpiresAt = newExpiry;
            licence.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }
```

- [ ] **Step 6: Run the entitlement and API tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceEntitlementApiTests|FullyQualifiedName~LicenceApiTests"`
Expected: PASS, 29 tests (10 new, 19 in `LicenceApiTests`).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 483 passed, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Shared/DTOs/LicenceDto.cs src/AssetDesk.Api/Controllers/LicencesController.cs tests/AssetDesk.Api.Tests/LicenceTestKit.cs tests/AssetDesk.Api.Tests/LicenceApiTests.cs tests/AssetDesk.Api.Tests/LicenceEntitlementApiTests.cs
git commit -m "feat(licences): record seats bought, renewals and reductions by hand"
```

---

### Task 6: Assigning and releasing seats

**Files:**
- Modify: `src/AssetDesk.Shared/DTOs/LicenceDto.cs` (append `AssignLicenceSeatDto`, `DeviceLicenceDto`)
- Modify: `src/AssetDesk.Api/Controllers/LicencesController.cs` (`AssignSeat`, `ReleaseSeat`, `GetDeviceLicences`)
- Modify: `tests/AssetDesk.Api.Tests/LicenceApiTests.cs` (two `InlineData`)
- Test: `tests/AssetDesk.Api.Tests/LicenceSeatApiTests.cs`

**Interfaces:**
- Consumes: Task 4/5 controller, `DetailAsync`, `CurrentUserId`.
- Produces:
  - `POST api/licences/{id}/seats` → `AssignSeat(int id, AssignLicenceSeatDto dto, CancellationToken ct) : Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>>`
  - `POST api/licences/{id}/seats/{seatId}/release` → `ReleaseSeat(int id, int seatId, CancellationToken ct) : Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>>`
  - `GET api/licences/device/{assetId}` → `GetDeviceLicences(int assetId, CancellationToken ct) : Task<ActionResult<ApiResponse<List<DeviceLicenceDto>>>>`

- [ ] **Step 1: Add the DTOs**

Append to `src/AssetDesk.Shared/DTOs/LicenceDto.cs`:

```csharp

/// <summary>Exactly one of UserId and AssetId, matching the licence's model.</summary>
public record AssignLicenceSeatDto
{
    public string? UserId { get; init; }
    public int? AssetId { get; init; }

    [StringLength(500)]
    public string? Notes { get; init; }
}

public record DeviceLicenceDto
{
    public int LicenceId { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public DateTime AssignedAt { get; init; }
    public required string RenewalStatus { get; init; }
}
```

- [ ] **Step 2: Add the policy cases**

In `tests/AssetDesk.Api.Tests/LicenceApiTests.cs`, add to `Each_write_is_gated_on_its_own_policy`:

```csharp
    [InlineData(nameof(LicencesController.AssignSeat), "CanManageLicences")]
    [InlineData(nameof(LicencesController.ReleaseSeat), "CanManageLicences")]
```

- [ ] **Step 3: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/LicenceSeatApiTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceSeatApiTests
{
    [Fact]
    public async Task A_person_is_given_a_seat_on_a_per_user_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);

            var detail = Data(await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1", Notes = "New starter" }, default));

            var seat = Assert.Single(detail.Seats);
            Assert.Equal("Maria Santos", seat.UserName);
            Assert.True(seat.IsActive);
            Assert.Equal(ActingUserId, (await db.LicenceSeatAssignments.SingleAsync()).AssignedByUserId);
        }
    }

    [Fact]
    public async Task A_device_cannot_take_a_seat_on_a_per_user_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_person_cannot_take_a_seat_on_a_per_device_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_seat_must_name_exactly_one_holder()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var licence = await SeedLicenceAsync(db, tenantId);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            var neither = await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto(), default);
            var both = await controller.AssignSeat(
                licence.Id, new AssignLicenceSeatDto { UserId = "user-1", AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(neither.Result);
            Assert.IsType<BadRequestObjectResult>(both.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_second_active_seat_for_the_same_person_is_refused_until_the_first_is_released()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);
            var controller = Controller(db, new FakeTenantProvider(tenantId));
            var assign = new AssignLicenceSeatDto { UserId = "user-1" };

            var first = Data(await controller.AssignSeat(licence.Id, assign, default));
            var second = await controller.AssignSeat(licence.Id, assign, default);
            Assert.IsType<BadRequestObjectResult>(second.Result);
            Assert.Equal(1, await db.LicenceSeatAssignments.CountAsync());

            await controller.ReleaseSeat(licence.Id, first.Seats.Single().Id, default);
            var again = await controller.AssignSeat(licence.Id, assign, default);

            Assert.IsType<OkObjectResult>(again.Result);
            Assert.Equal(2, await db.LicenceSeatAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Assigning_beyond_what_is_owned_is_allowed_and_counted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 1);
            var controller = Controller(db, new FakeTenantProvider(tenantId));

            await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);
            var detail = Data(await controller.AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-2" }, default));

            Assert.Equal(2, detail.Licence.SeatsAssigned);
            Assert.Equal(1, detail.Licence.OverAssigned);
        }
    }

    [Fact]
    public async Task A_deactivated_person_cannot_be_given_a_seat()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var user = await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            user.IsActive = false;
            await db.SaveChangesAsync();
            var licence = await SeedLicenceAsync(db, tenantId);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { UserId = "user-1" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task A_retired_device_cannot_be_given_a_seat()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001", AssetStatus.Retired);
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);

            var result = await Controller(db, new FakeTenantProvider(tenantId))
                .AssignSeat(licence.Id, new AssignLicenceSeatDto { AssetId = asset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(db.LicenceSeatAssignments);
        }
    }

    [Fact]
    public async Task Another_tenants_person_cannot_be_given_a_seat()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", "Someone Else");
            var ours = await SeedLicenceAsync(db, tenantA);

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AssignSeat(ours.Id, new AssignLicenceSeatDto { UserId = "their-user" }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("User not found.", Message(result));
            Assert.Empty(db.LicenceSeatAssignments.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Another_tenants_device_cannot_be_given_a_seat()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirAsset = await TestDb.SeedAssetAsync(db, tenantB, "LAP-9999");
            var ours = await SeedLicenceAsync(db, tenantA, "Windows 11 Pro", LicenceModels.PerDevice);

            // Super admin, current tenant A: the Assets filter admits tenant B's laptop, so only the
            // explicit predicate refuses it.
            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .AssignSeat(ours.Id, new AssignLicenceSeatDto { AssetId = theirAsset.Id }, default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Asset not found.", Message(result));
            Assert.Empty(db.LicenceSeatAssignments.IgnoreQueryFilters());
        }
    }

    [Fact]
    public async Task Releasing_a_seat_records_who_and_when_and_cannot_happen_twice()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = await SeedLicenceAsync(db, tenantId);
            var seat = await SeedUserSeatAsync(db, tenantId, licence.Id, "user-1");
            var controller = Controller(db, new FakeTenantProvider(tenantId), userId: "staff-7");

            var released = await controller.ReleaseSeat(licence.Id, seat.Id, default);
            var twice = await controller.ReleaseSeat(licence.Id, seat.Id, default);

            Assert.IsType<OkObjectResult>(released.Result);
            Assert.IsType<BadRequestObjectResult>(twice.Result);
            var saved = await db.LicenceSeatAssignments.AsNoTracking().SingleAsync();
            Assert.NotNull(saved.ReleasedAt);
            Assert.Equal("staff-7", saved.ReleasedByUserId);
        }
    }

    [Fact]
    public async Task Another_tenants_seat_cannot_be_released()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantB, "their-user", "Someone Else");
            var theirs = await SeedLicenceAsync(db, tenantB);
            var seat = await SeedUserSeatAsync(db, tenantB, theirs.Id, "their-user");

            var result = await Controller(db, new FakeTenantProvider(tenantA, isSuperAdmin: true))
                .ReleaseSeat(theirs.Id, seat.Id, default);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Null((await db.LicenceSeatAssignments.IgnoreQueryFilters().AsNoTracking().SingleAsync()).ReleasedAt);
        }
    }

    [Fact]
    public async Task Seats_whose_holders_have_gone_are_flagged_reclaimable()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var retired = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var working = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002", AssetStatus.InUse);
            var licence = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            await SeedDeviceSeatAsync(db, tenantId, licence.Id, retired.Id);
            await SeedDeviceSeatAsync(db, tenantId, licence.Id, working.Id);
            retired.Status = AssetStatus.Retired;
            await db.SaveChangesAsync();

            var detail = Data(await Controller(db, new FakeTenantProvider(tenantId)).GetById(licence.Id, default));

            Assert.True(detail.Seats.Single(s => s.AssetTag == "LAP-0001").IsReclaimable);
            Assert.False(detail.Seats.Single(s => s.AssetTag == "LAP-0002").IsReclaimable);
            Assert.Equal(1, detail.Licence.Reclaimable);
        }
    }

    [Fact]
    public async Task The_device_view_lists_only_active_seats_on_that_device()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var laptop = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            var other = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002");
            var windows = await SeedLicenceAsync(db, tenantId, "Windows 11 Pro", LicenceModels.PerDevice);
            var office = await SeedLicenceAsync(db, tenantId, "Office LTSC 2024", LicenceModels.PerDevice);
            var autocad = await SeedLicenceAsync(db, tenantId, "AutoCAD", LicenceModels.PerDevice);
            await SeedDeviceSeatAsync(db, tenantId, windows.Id, laptop.Id);
            var released = await SeedDeviceSeatAsync(db, tenantId, office.Id, laptop.Id);
            released.ReleasedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SeedDeviceSeatAsync(db, tenantId, autocad.Id, other.Id);

            var rows = Data(await Controller(db, new FakeTenantProvider(tenantId)).GetDeviceLicences(laptop.Id, default));

            Assert.Equal("Windows 11 Pro", Assert.Single(rows).Name);
        }
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceSeatApiTests"`
Expected: build FAILS - `'LicencesController' does not contain a definition for 'AssignSeat'`.

- [ ] **Step 5: Implement**

In `src/AssetDesk.Api/Controllers/LicencesController.cs`, add after `AddEntitlement`:

```csharp
    /// <summary>
    /// Over-assignment is allowed. Refusing the fifty-first seat does not stop the fifty-first
    /// install; it only stops it being recorded, and a register that reflects reality - including
    /// non-compliance - is the point.
    /// </summary>
    [HttpPost("{id:int}/seats")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> AssignSeat(
        int id, AssignLicenceSeatDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (!licence.IsActive)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail($"Licence '{licence.Name}' is deactivated."));

        var toUser = !string.IsNullOrWhiteSpace(dto.UserId);
        var toAsset = dto.AssetId is not null;

        if (toUser == toAsset)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Assign the seat to one person or one device."));

        if (licence.LicenceModel == LicenceModels.PerUser && !toUser)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"'{licence.Name}' is licensed per user, so its seats go to people."));

        if (licence.LicenceModel == LicenceModels.PerDevice && !toAsset)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"'{licence.Name}' is licensed per device, so its seats go to devices."));

        if (toUser)
        {
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == dto.UserId && u.TenantId == tenantId)
                .Select(u => new { u.FullName, u.IsActive })
                .FirstOrDefaultAsync(ct);
            if (user is null)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("User not found."));
            if (!user.IsActive)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"{user.FullName} is deactivated, so cannot be given a seat."));
        }
        else
        {
            var asset = await db.Assets
                .AsNoTracking()
                .Where(a => a.Id == dto.AssetId && a.TenantId == tenantId)
                .Select(a => new { a.AssetTag, a.Status })
                .FirstOrDefaultAsync(ct);
            if (asset is null)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Asset not found."));
            if (asset.Status is AssetStatus.Retired or AssetStatus.Lost)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"{asset.AssetTag} is {asset.Status}, so cannot be given a seat."));
        }

        var alreadyHeld = $"That {(toUser ? "person" : "device")} already holds a seat on this licence.";

        if (await HoldsActiveSeatAsync(tenantId, id, dto, ct))
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(alreadyHeld));

        db.LicenceSeatAssignments.Add(new LicenceSeatAssignment
        {
            TenantId = tenantId,
            SoftwareLicenceId = id,
            UserId = toUser ? dto.UserId : null,
            AssetId = toAsset ? dto.AssetId : null,
            AssignedByUserId = CurrentUserId,
            Notes = TrimToNull(dto.Notes)
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The failed insert is still tracked as Added; clear it so neither the check below nor
            // any later save on this scoped context sees or retries it.
            db.ChangeTracker.Clear();

            // Two assignments of the same holder raced past the check above and the partial unique
            // index refused the second - say so. Anything else is a real failure and propagates.
            if (!await HoldsActiveSeatAsync(tenantId, id, dto, ct))
                throw;

            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(alreadyHeld));
        }

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    [HttpPost("{id:int}/seats/{seatId:int}/release")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> ReleaseSeat(
        int id, int seatId, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var seat = await db.LicenceSeatAssignments
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SoftwareLicenceId == id && s.Id == seatId, ct);
        if (seat is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Seat not found."));

        if (seat.ReleasedAt is not null)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("That seat was already released."));

        seat.ReleasedAt = DateTime.UtcNow;
        seat.ReleasedByUserId = CurrentUserId;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    [HttpGet("device/{assetId:int}")]
    public async Task<ActionResult<ApiResponse<List<DeviceLicenceDto>>>> GetDeviceLicences(
        int assetId, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<DeviceLicenceDto>>.Fail("Select an organisation first."));

        var rows = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.AssetId == assetId && s.ReleasedAt == null)
            .Select(s => new
            {
                s.SoftwareLicenceId,
                s.SoftwareLicence!.Name,
                s.SoftwareLicence.Publisher,
                s.SoftwareLicence.ExpiresAt,
                s.AssignedAt
            })
            .ToListAsync(ct);

        var today = DateTime.UtcNow;
        return Ok(ApiResponse<List<DeviceLicenceDto>>.Ok(rows
            .OrderBy(r => r.Name)
            .Select(r => new DeviceLicenceDto
            {
                LicenceId = r.SoftwareLicenceId,
                Name = r.Name,
                Publisher = r.Publisher,
                AssignedAt = r.AssignedAt,
                RenewalStatus = LicenceRules.RenewalStatus(r.ExpiresAt, today)
            })
            .ToList()));
    }

    private Task<bool> HoldsActiveSeatAsync(Guid tenantId, int licenceId, AssignLicenceSeatDto dto, CancellationToken ct) =>
        db.LicenceSeatAssignments.AnyAsync(s =>
            s.TenantId == tenantId
            && s.SoftwareLicenceId == licenceId
            && s.ReleasedAt == null
            && (dto.AssetId == null ? s.UserId == dto.UserId : s.AssetId == dto.AssetId), ct);
```

- [ ] **Step 6: Run the seat and API tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceSeatApiTests|FullyQualifiedName~LicenceApiTests"`
Expected: PASS, 35 tests (14 new, 21 in `LicenceApiTests`).

- [ ] **Step 7: Prove the device-isolation test holds for the right reason**

Temporarily delete ` && a.TenantId == tenantId` from the asset lookup in `AssignSeat`, run
`dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~Another_tenants_device_cannot_be_given_a_seat"`,
confirm it FAILS, restore it, confirm it passes. Record both outputs in the task report.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 499 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Shared/DTOs/LicenceDto.cs src/AssetDesk.Api/Controllers/LicencesController.cs tests/AssetDesk.Api.Tests/LicenceApiTests.cs tests/AssetDesk.Api.Tests/LicenceSeatApiTests.cs
git commit -m "feat(licences): assign seats to people or devices and release them"
```

---
### Task 7: Receiving a Software line into a licence

**Files:**
- Modify: `src/AssetDesk.Shared/DTOs/ProcurementDto.cs` (`NewLicenceInputDto`; `ReceiveLineDto` and `GoodsReceiptLineDto` gain fields)
- Modify: `src/AssetDesk.Api/Services/GoodsReceiptService.cs` (licence resolution before the lock; entitlements instead of assets for Software lines)
- Modify: `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs` (`Receive` permission check; `MapDetailAsync` destinations)
- Modify: `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs` (one replay test beside the existing one)
- Test: `tests/AssetDesk.Api.Tests/LicenceReceivingTests.cs`

**Interfaces:**
- Consumes: Task 1 entities and `LicenceReceiptModes`, `LicenceModels`; Task 2 `Permissions.LicencesManage`; `ClaimsPrincipalExtensions.HasPermission(this ClaimsPrincipal, string)`.
- Produces:
  - `NewLicenceInputDto { string Name; string? Publisher; string LicenceModel }`
  - `ReceiveLineDto` gains `int? SoftwareLicenceId`, `NewLicenceInputDto? NewLicence`, `string LicenceMode` (default `"AddSeats"`), `DateTime? LicenceExpiresAt`
  - `GoodsReceiptLineDto` gains `int? SoftwareLicenceId`, `string? LicenceName`, `DateTime? RenewedTo`

The transaction keeps everything it already does - the execution strategy, `RequestId` and `verifySucceeded`, the order row lock taken first, the conditional claim per line, `CurrencyRules.Validate`, the status recomputation. Do not restructure any of it.

- [ ] **Step 1: Extend the DTOs**

In `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`, replace `GoodsReceiptLineDto` with:

```csharp
public record GoodsReceiptLineDto
{
    public required string DeviceType { get; init; }
    public string? Description { get; init; }
    public int QuantityReceived { get; init; }

    /// <summary>
    /// Where a software line's seats went. Null for hardware, which became assets instead - and
    /// without this the delivery history would show fifty seats arriving and then nothing.
    /// </summary>
    public int? SoftwareLicenceId { get; init; }
    public string? LicenceName { get; init; }

    /// <summary>Set when the line renewed its licence rather than adding seats to it.</summary>
    public DateTime? RenewedTo { get; init; }
}
```

Replace `ReceiveLineDto` with:

```csharp
/// <summary>A licence created in the same transaction as the delivery that brings its first seats.</summary>
public record NewLicenceInputDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)]
    public string? Publisher { get; init; }

    [Required]
    public string LicenceModel { get; init; } = "PerUser";
}

public record ReceiveLineDto
{
    public int PurchaseOrderLineId { get; init; }

    [Range(1, 100000, ErrorMessage = "Quantity received must be at least 1")]
    public int QuantityReceived { get; init; }

    /// <summary>Software lines only: an existing licence, or NewLicence - exactly one of the two.</summary>
    public int? SoftwareLicenceId { get; init; }

    public NewLicenceInputDto? NewLicence { get; init; }

    /// <summary>Software lines only: "AddSeats" or "Renew".</summary>
    public string LicenceMode { get; init; } = "AddSeats";

    /// <summary>Software lines only: required for a renewal, optional when adding seats.</summary>
    public DateTime? LicenceExpiresAt { get; init; }
}
```

- [ ] **Step 2: Write the failing receiving tests**

Create `tests/AssetDesk.Api.Tests/LicenceReceivingTests.cs`:

```csharp
using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// A Software line on a purchase order becomes seats on a licence, not an asset per seat. Every
/// refusal is asserted to write nothing at all - no receipt, no entitlement, no licence, no asset,
/// no advanced received quantity - because a partial delivery is exactly what the transaction
/// around receiving exists to prevent.
/// </summary>
public class LicenceReceivingTests
{
    private static readonly DateTime ReceiptDate = new(2026, 9, 12);

    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Software, PurchaseOrderLine Laptop)> SeedOrderAsync(
        AppDbContext db, Guid tenantId, string currency = Currencies.PHP)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Software" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id, Currency = currency,
            Status = PurchaseOrderStatus.Ordered, OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Software, Description = "Microsoft 365 Business Standard",
            Quantity = 50, UnitPrice = 700m
        });
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop, Description = "Dell Latitude 5540",
            Quantity = 2, UnitPrice = 50000m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order, order.Lines.Single(l => l.DeviceType == DeviceTypes.Software),
            order.Lines.Single(l => l.DeviceType == DeviceTypes.Laptop));
    }

    private static GoodsReceiptService ServiceFor(AppDbContext db) =>
        new(db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);

    private static ReceiveGoodsDto Receive(decimal rate, params ReceiveLineDto[] lines) => new()
    {
        ReceiptDate = ReceiptDate,
        ExchangeRate = rate,
        Notes = "INV-2211",
        Lines = [.. lines]
    };

    private static ReceiveLineDto Line(int lineId, int quantity) =>
        new() { PurchaseOrderLineId = lineId, QuantityReceived = quantity };

    private static NewLicenceInputDto NewLicence(string name = "Microsoft 365 Business Standard") =>
        new() { Name = name, Publisher = "Microsoft", LicenceModel = LicenceModels.PerUser };

    private static async Task AssertNothingWrittenAsync(AppDbContext db, int licencesBefore)
    {
        db.ChangeTracker.Clear();
        Assert.Empty(db.GoodsReceipts.IgnoreQueryFilters());
        Assert.Empty(db.LicenceEntitlements.IgnoreQueryFilters());
        Assert.Empty(db.Assets.IgnoreQueryFilters());
        Assert.Equal(licencesBefore, await db.SoftwareLicences.IgnoreQueryFilters().CountAsync());
        Assert.All(await db.PurchaseOrderLines.ToListAsync(), l => Assert.Equal(0, l.ReceivedQuantity));
    }

    [Fact]
    public async Task A_software_line_becomes_an_entitlement_rather_than_assets()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId, Currencies.USD);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(58.20m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Empty(db.Assets);
            var entry = await db.LicenceEntitlements.SingleAsync();
            Assert.Equal(licence.Id, entry.SoftwareLicenceId);
            Assert.Equal(50, entry.SeatsAdded);
            Assert.Equal(35000m, entry.Cost);
            Assert.Equal(Currencies.USD, entry.Currency);
            Assert.Equal(58.20m, entry.ExchangeRate);
            Assert.Equal(ReceiptDate, entry.EntitlementDate);
            Assert.Equal((await db.GoodsReceiptLines.SingleAsync()).Id, entry.GoodsReceiptLineId);
            Assert.Equal(50, (await db.PurchaseOrderLines.SingleAsync(l => l.Id == software.Id)).ReceivedQuantity);
        }
    }

    [Fact]
    public async Task A_renewal_adds_no_seats_and_moves_the_expiry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, expiresAt: new DateTime(2026, 9, 30));
            await LicenceTestKit.SeedEntitlementAsync(db, tenantId, licence.Id, seats: 50);
            var renewedTo = new DateTime(2027, 9, 30);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    SoftwareLicenceId = licence.Id,
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = renewedTo
                }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(50, await db.LicenceEntitlements.SumAsync(e => e.SeatsAdded));
            Assert.Equal(renewedTo, (await db.SoftwareLicences.SingleAsync()).ExpiresAt);
            var renewal = await db.LicenceEntitlements.SingleAsync(e => e.GoodsReceiptLineId != null);
            Assert.Equal(0, renewal.SeatsAdded);
            Assert.Equal(renewedTo, renewal.ExpiresAtAfter);
            Assert.Equal(50, (await db.PurchaseOrderLines.SingleAsync(l => l.Id == software.Id)).ReceivedQuantity);
        }
    }

    [Fact]
    public async Task A_new_licence_is_created_with_the_delivery_that_brings_its_seats()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() }), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            var licence = await db.SoftwareLicences.SingleAsync();
            Assert.Equal(tenantId, licence.TenantId);
            Assert.Equal("Microsoft 365 Business Standard", licence.Name);
            Assert.Equal(LicenceModels.PerUser, licence.LicenceModel);
            Assert.Equal(order.SupplierId, licence.SupplierId);
            Assert.Equal(licence.Id, (await db.LicenceEntitlements.SingleAsync()).SoftwareLicenceId);
        }
    }

    [Fact]
    public async Task A_mixed_delivery_creates_assets_for_hardware_and_an_entitlement_for_software()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id },
                Line(laptop.Id, 2)), "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(2, await db.Assets.CountAsync());
            Assert.All(await db.Assets.ToListAsync(), a => Assert.Equal(DeviceTypes.Laptop, a.DeviceType));
            Assert.Single(await db.LicenceEntitlements.ToListAsync());
            Assert.Equal(PurchaseOrderStatus.Received, (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task A_software_line_with_no_licence_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m, Line(software.Id, 50)), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_software_line_naming_both_an_existing_and_a_new_licence_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, "Existing");

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id, NewLicence = NewLicence() }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_licence_given_for_a_hardware_line_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, _, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(laptop.Id, 2) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_renewal_without_a_new_expiry_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { SoftwareLicenceId = licence.Id, LicenceMode = LicenceReceiptModes.Renew }),
                "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task An_expiry_on_or_before_the_receipt_date_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    SoftwareLicenceId = licence.Id,
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = ReceiptDate
                }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
            Assert.Null((await db.SoftwareLicences.SingleAsync()).ExpiresAt);
        }
    }

    [Fact]
    public async Task A_renewal_cannot_create_a_licence()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with
                {
                    NewLicence = NewLicence(),
                    LicenceMode = LicenceReceiptModes.Renew,
                    LicenceExpiresAt = new DateTime(2027, 9, 30)
                }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_deactivated_licence_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId, isActive: false);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task Another_tenants_licence_is_refused_and_writes_nothing()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var (order, software, _) = await SeedOrderAsync(db, tenantA);
            var theirs = await LicenceTestKit.SeedLicenceAsync(db, tenantB);

            // Super-admin context: the global filter admits tenant B's licence, so only the explicit
            // tenant predicate refuses it.
            var result = await ServiceFor(db).ReceiveAsync(tenantA, order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = theirs.Id }), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    [Fact]
    public async Task A_refused_delivery_leaves_no_new_licence_behind()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);

            // The software line passes every check and its quantity is claimed; the laptop line then
            // over-receives (3 of 2), so the whole receipt rolls back. A licence created while
            // resolving the software line would survive that unless it is inside the transaction.
            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, Receive(1m,
                Line(software.Id, 50) with { NewLicence = NewLicence() },
                Line(laptop.Id, 3)), "user-1");

            Assert.False(result.Success);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);
        }
    }

    [Fact]
    public async Task A_new_licence_cannot_reuse_an_existing_name()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id,
                Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() }), "user-1");

            Assert.False(result.Success);
            Assert.Equal("A licence named 'Microsoft 365 Business Standard' already exists.", result.Message);
            await AssertNothingWrittenAsync(db, licencesBefore: 1);
        }
    }

    private static PurchaseOrdersController ControllerFor(AppDbContext db, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "buyer-1") };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        return new PurchaseOrdersController(
            db, new FakeTenantProvider(tenantId), new PurchaseOrderNumberAllocator(db), new LookupService(db),
            ServiceFor(db), NullLogger<PurchaseOrdersController>.Instance, new PdfReportService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    [Fact]
    public async Task Creating_a_licence_while_receiving_needs_permission_to_manage_licences()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, _) = await SeedOrderAsync(db, tenantId);
            var dto = Receive(1m, Line(software.Id, 50) with { NewLicence = NewLicence() });

            var buyerOnly = await ControllerFor(db, tenantId, Permissions.ProcurementManage).Receive(order.Id, dto);

            var refusal = Assert.IsType<ObjectResult>(buyerOnly.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, refusal.StatusCode);
            await AssertNothingWrittenAsync(db, licencesBefore: 0);

            var licenceManager = await ControllerFor(db, tenantId, Permissions.ProcurementManage, Permissions.LicencesManage)
                .Receive(order.Id, dto);

            Assert.IsType<OkObjectResult>(licenceManager.Result);
            Assert.Equal(1, await db.SoftwareLicences.CountAsync());
        }
    }

    [Fact]
    public async Task The_delivery_history_names_where_the_seats_went()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, software, laptop) = await SeedOrderAsync(db, tenantId);
            var licence = await LicenceTestKit.SeedLicenceAsync(db, tenantId);

            var result = await ControllerFor(db, tenantId, Permissions.ProcurementManage).Receive(order.Id,
                Receive(1m, Line(software.Id, 50) with { SoftwareLicenceId = licence.Id }, Line(laptop.Id, 2)));

            var detail = Assert.IsType<ApiResponse<PurchaseOrderDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
            var lines = detail.Receipts.Single().Lines;

            var softwareLine = lines.Single(l => l.DeviceType == DeviceTypes.Software);
            Assert.Equal(licence.Id, softwareLine.SoftwareLicenceId);
            Assert.Equal("Microsoft 365 Business Standard", softwareLine.LicenceName);
            Assert.Null(softwareLine.RenewedTo);

            var laptopLine = lines.Single(l => l.DeviceType == DeviceTypes.Laptop);
            Assert.Null(laptopLine.LicenceName);
        }
    }
}
```

- [ ] **Step 3: Write the failing replay test**

In `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs`, add after `A_replayed_delivery_is_not_recorded_twice` (it needs the private `ReplayingExecutionStrategy` in that class):

```csharp

    /// <summary>
    /// The same replay as above, for a software line. An entitlement is written in the same
    /// delegate as the receipt, so a replay that re-ran it would double the seats owned without
    /// any second delivery ever arriving.
    /// </summary>
    [Fact]
    public async Task A_replayed_software_delivery_records_its_seats_once()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(
            new FakeTenantProvider(tenantId),
            builder => builder.ReplaceService<IExecutionStrategyFactory, ReplayingExecutionStrategy>());
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            line.DeviceType = DeviceTypes.Software;
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Microsoft 365", LicenceModel = AssetDesk.Shared.LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var dto = Receive(line.Id, 5) with
            {
                Lines = [new ReceiveLineDto { PurchaseOrderLineId = line.Id, QuantityReceived = 5, SoftwareLicenceId = licence.Id }]
            };
            var result = await ServiceFor(db).ReceiveAsync(tenantId, order.Id, dto, "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();

            Assert.Equal(1, await db.GoodsReceipts.CountAsync());
            Assert.Equal(1, await db.LicenceEntitlements.CountAsync());
            Assert.Equal(5, await db.LicenceEntitlements.SumAsync(e => e.SeatsAdded));
        }
    }
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceReceivingTests|FullyQualifiedName~A_replayed_software_delivery"`
Expected: FAIL. The DTOs compile after Step 1, so the tests build; the service still turns the Software line into 50 assets and refuses nothing. `A_software_line_becomes_an_entitlement_rather_than_assets` fails on `Assert.Empty(db.Assets)`, and the refusal tests fail on `Assert.False(result.Success)`.

- [ ] **Step 5: Resolve licences before the first write**

In `src/AssetDesk.Api/Services/GoodsReceiptService.cs`, add `using AssetDesk.Shared;` to the usings.

Immediately after the `if (CurrencyRules.Validate(order.Currency, dto.ExchangeRate) is { } currencyError) return ServiceResult<int>.Fail(currencyError);` statement, and **before** the comment that begins `// Take the ORDER's row lock before claiming any line.`, insert:

```csharp
                // Software lines are resolved to their licences here, before the order row is locked
                // and before anything is written, so every refusal returns with nothing to roll back.
                // A licence this receipt creates is only described at this point; it is added to the
                // context after the lines are claimed, inside the same transaction, so a later refusal
                // or failure cannot leave it behind.
                var existingLicenceFor = new Dictionary<int, SoftwareLicence>();
                var newLicenceNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var incoming in dto.Lines)
                {
                    var line = order.Lines.FirstOrDefault(l => l.Id == incoming.PurchaseOrderLineId);
                    if (line is null)
                        return ServiceResult<int>.Fail("That line does not belong to this purchase order.");

                    if (line.DeviceType != DeviceTypes.Software)
                    {
                        if (incoming.SoftwareLicenceId is not null || incoming.NewLicence is not null)
                            return ServiceResult<int>.Fail(
                                $"{line.DeviceType} is not software, so it cannot be received into a licence.");
                        continue;
                    }

                    var label = line.Description ?? line.DeviceType;

                    if ((incoming.SoftwareLicenceId is null) == (incoming.NewLicence is null))
                        return ServiceResult<int>.Fail(
                            $"{label}: choose the licence these seats belong to, or name a new one.");

                    if (!LicenceReceiptModes.IsValid(incoming.LicenceMode))
                        return ServiceResult<int>.Fail(
                            $"{label}: choose whether this adds seats or renews the licence.");

                    if (incoming.LicenceMode == LicenceReceiptModes.Renew)
                    {
                        if (incoming.NewLicence is not null)
                            return ServiceResult<int>.Fail($"{label}: a renewal needs an existing licence.");
                        if (incoming.LicenceExpiresAt is null)
                            return ServiceResult<int>.Fail($"{label}: a renewal needs the new expiry date.");
                    }

                    if (incoming.LicenceExpiresAt is { } expiry && expiry.Date <= dto.ReceiptDate.Date)
                        return ServiceResult<int>.Fail($"{label}: the new expiry must be after the receipt date.");

                    if (incoming.NewLicence is { } described)
                    {
                        var name = described.Name?.Trim();
                        if (string.IsNullOrEmpty(name))
                            return ServiceResult<int>.Fail($"{label}: the new licence needs a name.");
                        if (!LicenceModels.IsValid(described.LicenceModel))
                            return ServiceResult<int>.Fail(
                                $"{label}: choose whether the new licence is counted per user or per device.");

                        // Explicit tenant predicate: the global filter has an IsSuperAdmin() bypass, and
                        // a name taken only in another organisation must not block this one.
                        if (!newLicenceNames.Add(name)
                            || await db.SoftwareLicences.AnyAsync(l => l.TenantId == tenantId && l.Name == name, ct))
                            return ServiceResult<int>.Fail($"A licence named '{name}' already exists.");
                        continue;
                    }

                    var licence = await db.SoftwareLicences.FirstOrDefaultAsync(
                        l => l.Id == incoming.SoftwareLicenceId && l.TenantId == tenantId, ct);
                    if (licence is null)
                        return ServiceResult<int>.Fail($"{label}: licence not found.");
                    if (!licence.IsActive)
                        return ServiceResult<int>.Fail($"{label}: licence '{licence.Name}' is deactivated.");

                    existingLicenceFor[line.Id] = licence;
                }
```

- [ ] **Step 6: Write entitlements instead of assets for Software lines**

Replace the loop that creates assets - the one beginning `foreach (var receiptLine in receipt.Lines)` and containing `db.Assets.Add(new Asset` - with:

```csharp
                foreach (var receiptLine in receipt.Lines)
                {
                    var line = order.Lines.First(l => l.Id == receiptLine.PurchaseOrderLineId);

                    if (line.DeviceType == DeviceTypes.Software)
                    {
                        var incoming = dto.Lines.First(l => l.PurchaseOrderLineId == line.Id);
                        var licence = existingLicenceFor.GetValueOrDefault(line.Id)
                            ?? AddLicence(order, incoming.NewLicence!);
                        var renewing = incoming.LicenceMode == LicenceReceiptModes.Renew;

                        db.LicenceEntitlements.Add(new LicenceEntitlement
                        {
                            TenantId = order.TenantId,
                            SoftwareLicence = licence,
                            // A renewal is paid for per seat but adds none: the same seats run for
                            // another term. Counting them again would report seats nobody owns.
                            SeatsAdded = renewing ? 0 : receiptLine.QuantityReceived,
                            Cost = line.UnitPrice * receiptLine.QuantityReceived,
                            Currency = order.Currency,
                            ExchangeRate = dto.ExchangeRate,
                            EntitlementDate = dto.ReceiptDate,
                            ExpiresAtAfter = incoming.LicenceExpiresAt,
                            GoodsReceiptLineId = receiptLine.Id,
                            CreatedByUserId = actingUserId,
                            Notes = dto.Notes
                        });

                        if (incoming.LicenceExpiresAt is { } newExpiry)
                        {
                            licence.ExpiresAt = newExpiry;
                            licence.UpdatedAt = DateTime.UtcNow;
                        }

                        continue;
                    }

                    for (var i = 0; i < receiptLine.QuantityReceived; i++)
                    {
                        db.Assets.Add(new Asset
                        {
                            TenantId = order.TenantId,
                            AssetTag = await tags.NextAsync(line.DeviceType, tagCache, ct),
                            DeviceType = line.DeviceType,
                            Name = line.Description,
                            Status = AssetStatus.Available,
                            PurchasePrice = line.UnitPrice,
                            Currency = order.Currency,
                            ExchangeRate = dto.ExchangeRate,
                            PurchaseDate = dto.ReceiptDate,
                            GoodsReceiptLineId = receiptLine.Id
                        });
                    }
                }
```

Keep every comment that sat inside the original asset loop - move them with the `for` loop, unchanged.

Replace the `logger.LogInformation("Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s)", ...)` call with:

```csharp
                logger.LogInformation(
                    "Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s)",
                    receipt.Lines.Count, order.PoNumber,
                    receipt.Lines
                        .Where(l => order.Lines.First(o => o.Id == l.PurchaseOrderLineId).DeviceType != DeviceTypes.Software)
                        .Sum(l => l.QuantityReceived));
```

Add this private method to the class, after `ReceiveAsync`:

```csharp
    /// <summary>
    /// A licence named in the receive dialog, created inside the receiving transaction. Its supplier
    /// is the order's - the delivery is the evidence of who sold it.
    /// </summary>
    private SoftwareLicence AddLicence(PurchaseOrder order, NewLicenceInputDto described)
    {
        var licence = new SoftwareLicence
        {
            TenantId = order.TenantId,
            Name = described.Name.Trim(),
            Publisher = string.IsNullOrWhiteSpace(described.Publisher) ? null : described.Publisher.Trim(),
            LicenceModel = described.LicenceModel,
            SupplierId = order.SupplierId
        };

        db.SoftwareLicences.Add(licence);
        return licence;
    }
```

- [ ] **Step 7: Gate licence creation and name the destination in the controller**

In `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`, add `using AssetDesk.Api.Authorization;`.

In `Receive`, immediately after the `if (tenantProvider.GetCurrentTenantId() is not { } tenantId) return ...;` statement, insert:

```csharp
        // Adding seats to a licence that already exists is part of receiving - the receipt is the
        // record of that purchase. Creating a licence is licence management, and must not become
        // something anyone holding procurement rights can do as a side effect of a delivery.
        if (dto.Lines.Any(l => l.NewLicence is not null) && !User.HasPermission(Permissions.LicencesManage))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<PurchaseOrderDto>.Fail(
                "Creating a licence while receiving needs permission to manage licences."));
```

In `GetById`, change `await MapDetailAsync(order)` to `await MapDetailAsync(tenantId, order)`.

Replace `MapDetailAsync` with:

```csharp
    /// <summary>Expects Receipts and their Lines to be loaded already - see GetById.</summary>
    private async Task<PurchaseOrderDto> MapDetailAsync(Guid tenantId, PurchaseOrder p)
    {
        var receiverNames = await ResolveUserNamesAsync(p.Receipts.Select(r => r.ReceivedByUserId));

        // A software line became an entitlement rather than assets. Where it went is looked up here
        // so the delivery history can say "added to" or "renewed to" instead of showing seats that
        // arrived and went nowhere.
        var receiptLineIds = p.Receipts.SelectMany(r => r.Lines).Select(l => l.Id).ToList();
        var destinations = (await db.LicenceEntitlements
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId
                         && e.GoodsReceiptLineId != null
                         && receiptLineIds.Contains(e.GoodsReceiptLineId.Value))
                .Select(e => new
                {
                    ReceiptLineId = e.GoodsReceiptLineId!.Value,
                    e.SoftwareLicenceId,
                    LicenceName = e.SoftwareLicence!.Name,
                    e.SeatsAdded,
                    e.ExpiresAtAfter
                })
                .ToListAsync())
            .ToDictionary(e => e.ReceiptLineId);

        return Map(p) with
        {
            // Id breaks the tie so two deliveries booked on the same day come back in a stable
            // order rather than whichever the database happened to return.
            Receipts = [.. p.Receipts
                .OrderByDescending(r => r.ReceiptDate)
                .ThenByDescending(r => r.Id)
                .Select(r => new GoodsReceiptDto
                {
                    Id = r.Id,
                    ReceiptDate = r.ReceiptDate,
                    ExchangeRate = r.ExchangeRate,
                    ReceivedByName = receiverNames.GetValueOrDefault(r.ReceivedByUserId),
                    Notes = r.Notes,
                    TotalUnits = r.Lines.Sum(l => l.QuantityReceived),
                    Lines = [.. r.Lines.Select(l =>
                    {
                        var ordered = p.Lines.First(o => o.Id == l.PurchaseOrderLineId);
                        var destination = destinations.GetValueOrDefault(l.Id);
                        return new GoodsReceiptLineDto
                        {
                            DeviceType = ordered.DeviceType,
                            Description = ordered.Description,
                            QuantityReceived = l.QuantityReceived,
                            SoftwareLicenceId = destination?.SoftwareLicenceId,
                            LicenceName = destination?.LicenceName,
                            RenewedTo = destination is { SeatsAdded: 0 } ? destination.ExpiresAtAfter : null
                        };
                    })]
                })]
        };
    }
```

- [ ] **Step 8: Run the receiving tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceReceivingTests|FullyQualifiedName~GoodsReceiptTests|FullyQualifiedName~PurchaseOrderApiTests"`
Expected: PASS - 16 new in `LicenceReceivingTests`, the new replay test, and every existing `GoodsReceiptTests` and `PurchaseOrderApiTests` test unchanged.

- [ ] **Step 9: Prove the no-stray-licence test holds for the right reason**

Two temporary mutations, each reverted before the next:

1. In the resolution loop from Step 5, replace the `continue;` after the new-licence name check with `AddLicence(order, described); await db.SaveChangesAsync(ct); continue;` and run
   `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~A_refused_delivery_leaves_no_new_licence_behind"`.
   Expected: still PASSES - the licence is saved inside the transaction, and the refusal rolls it back.
2. Keeping mutation 1, also comment out both `await using var tx = await db.Database.BeginTransactionAsync(ct);` and `await tx.CommitAsync(ct);`, and run the same test.
   Expected: FAILS - with no transaction the licence commits on its own save and survives the refusal.

Revert both and confirm the test passes. Record all three outputs in the task report: together they show the test proves transactional containment rather than just the order the code happens to run in.

- [ ] **Step 10: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 516 passed, 0 failed.

- [ ] **Step 11: Commit**

```bash
git add src/AssetDesk.Shared/DTOs/ProcurementDto.cs src/AssetDesk.Api/Services/GoodsReceiptService.cs src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs tests/AssetDesk.Api.Tests/LicenceReceivingTests.cs tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs
git commit -m "feat(licences): receive software purchase-order lines into a licence"
```

---

### Task 8: Licence compliance report

**Files:**
- Create: `src/AssetDesk.Api/Services/CsvFormat.cs`
- Modify: `src/AssetDesk.Api/Controllers/ReportsController.cs` (delete the private `EscapeCsv`; every call becomes `CsvFormat.Escape`)
- Modify: `src/AssetDesk.Shared/DTOs/LicenceDto.cs` (append the report DTOs)
- Modify: `src/AssetDesk.Api/Services/PdfReportService.cs` (interface and implementation)
- Create: `src/AssetDesk.Api/Controllers/LicenceReportsController.cs`
- Test: `tests/AssetDesk.Api.Tests/LicenceComplianceReportTests.cs`

**Interfaces:**
- Consumes: `ILicenceUsageReader`, `LicenceUsage` (Task 4); `LicenceRules`; `IPdfReportService` private helpers `BuildDocument`, `BuildFilterLine`, `AddHeaderRow`, `AddBodyCell`, `FormatCurrency`.
- Produces:
  - `CsvFormat.Escape(string? value) : string`
  - `LicenceComplianceRow`, `LicenceComplianceSummaryDto`
  - `IPdfReportService.BuildLicenceCompliancePdf(LicenceComplianceSummaryDto summary) : byte[]`
  - `GET api/reports/licences`, `GET api/reports/licences/export`, `GET api/reports/licences/pdf` on `LicenceReportsController(AppDbContext db, ITenantProvider tenantProvider, ILicenceUsageReader usage, IPdfReportService pdf)` with actions `Get`, `Export`, `Pdf` (each takes `CancellationToken ct`)

- [ ] **Step 1: Extract the CSV escaper**

Create `src/AssetDesk.Api/Services/CsvFormat.cs`:

```csharp
namespace AssetDesk.Api.Services;

/// <summary>
/// One CSV escaping rule for every export. Lifted out of ReportsController when the licence report
/// became its second caller, rather than copied into it.
/// </summary>
public static class CsvFormat
{
    /// <summary>Quotes a field containing a comma, quote or line break, doubling any quotes inside it.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }
}
```

In `src/AssetDesk.Api/Controllers/ReportsController.cs`, delete the `private static string EscapeCsv(string? value)` method and replace **every** `EscapeCsv(` with `CsvFormat.Escape(`. Then:

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 516 passed, 0 failed - a behaviour-preserving move. Confirm with `grep -n "EscapeCsv" src/AssetDesk.Api/Controllers/ReportsController.cs` that nothing remains.

- [ ] **Step 2: Add the report DTOs**

Append to `src/AssetDesk.Shared/DTOs/LicenceDto.cs`:

```csharp

public record LicenceComplianceRow
{
    public int LicenceId { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public required string LicenceModel { get; init; }
    public int SeatsOwned { get; init; }
    public int SeatsAssigned { get; init; }
    public int OverAssigned { get; init; }
    public int Reclaimable { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public required string RenewalStatus { get; init; }
    public decimal SpendInPesos { get; init; }
}

public record LicenceComplianceSummaryDto
{
    public int LicenceCount { get; init; }
    public int TotalSeatsOwned { get; init; }
    public int TotalSeatsAssigned { get; init; }
    public int OverAssignedLicenceCount { get; init; }
    public int OverAssignedSeats { get; init; }
    public int ReclaimableSeats { get; init; }
    public int RenewalsDue { get; init; }
    public int RenewalsExpired { get; init; }
    public decimal TotalSpendInPesos { get; init; }
    public string PrimaryCurrency { get; init; } = "PHP";
    public DateTime AsOf { get; init; }
    public List<LicenceComplianceRow> Rows { get; init; } = [];
}
```

- [ ] **Step 3: Write the failing report tests**

Create `tests/AssetDesk.Api.Tests/LicenceComplianceReportTests.cs`:

```csharp
using System.Text;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceComplianceReportTests
{
    private static LicenceReportsController ReportFor(AppDbContext db, ITenantProvider tenants) =>
        new(db, tenants, new LicenceUsageReader(db), new PdfReportService());

    private static LicenceComplianceSummaryDto Summary(ActionResult<ApiResponse<LicenceComplianceSummaryDto>> result) =>
        Assert.IsType<ApiResponse<LicenceComplianceSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;

    /// <summary>Splits one CSV record, honouring quoted fields - enough to prove columns line up.</summary>
    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    [Fact]
    public async Task Spend_is_totalled_in_pesos_across_currencies()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 10, cost: 1200m, currency: Currencies.USD, rate: 58.20m);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 10000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(79840m, summary.TotalSpendInPesos);
            Assert.Equal(79840m, summary.Rows.Single().SpendInPesos);
            Assert.Equal(15, summary.TotalSeatsOwned);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);
        }
    }

    [Fact]
    public async Task Deactivated_licences_are_left_out()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedLicenceAsync(db, tenantId, "In use");
            var retired = await SeedLicenceAsync(db, tenantId, "Retired", isActive: false);
            await SeedEntitlementAsync(db, tenantId, retired.Id, seats: 100, cost: 500000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal("In use", Assert.Single(summary.Rows).Name);
            Assert.Equal(0, summary.TotalSeatsOwned);
            Assert.Equal(0m, summary.TotalSpendInPesos);
        }
    }

    [Fact]
    public async Task Over_assigned_and_reclaimable_seats_are_totalled()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var departed = await TestDb.SeedUserAsync(db, tenantId, "user-3", "Ana Cruz");

            var tight = await SeedLicenceAsync(db, tenantId, "Tight");
            await SeedEntitlementAsync(db, tenantId, tight.Id, seats: 2);
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-1");
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-2");
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-3");

            var roomy = await SeedLicenceAsync(db, tenantId, "Roomy");
            await SeedEntitlementAsync(db, tenantId, roomy.Id, seats: 10);
            await SeedUserSeatAsync(db, tenantId, roomy.Id, "user-1");

            departed.IsActive = false;
            await db.SaveChangesAsync();

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(12, summary.TotalSeatsOwned);
            Assert.Equal(4, summary.TotalSeatsAssigned);
            Assert.Equal(1, summary.OverAssignedLicenceCount);
            Assert.Equal(1, summary.OverAssignedSeats);
            Assert.Equal(1, summary.ReclaimableSeats);
        }
    }

    [Fact]
    public async Task Renewals_due_and_expired_are_counted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.Date;
            await SeedLicenceAsync(db, tenantId, "Expired", expiresAt: today.AddDays(-1));
            await SeedLicenceAsync(db, tenantId, "Due soon", expiresAt: today.AddDays(45));
            await SeedLicenceAsync(db, tenantId, "Due later", expiresAt: today.AddDays(90));
            await SeedLicenceAsync(db, tenantId, "Active", expiresAt: today.AddDays(365));
            await SeedLicenceAsync(db, tenantId, "Perpetual");

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(2, summary.RenewalsDue);
            Assert.Equal(1, summary.RenewalsExpired);
            Assert.Equal(LicenceRenewalStatuses.Perpetual, summary.Rows.Single(r => r.Name == "Perpetual").RenewalStatus);
        }
    }

    [Fact]
    public async Task The_csv_has_one_aligned_row_per_licence_and_never_the_key()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, "Acrobat Pro, Team", key: "SECRET-KEY-VALUE-1234");
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 12500m);

            var file = Assert.IsType<FileContentResult>(await ReportFor(db, new FakeTenantProvider(tenantId)).Export(default));
            var text = Encoding.UTF8.GetString(file.FileContents);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

            Assert.Equal("text/csv", file.ContentType);
            Assert.DoesNotContain("SECRET-KEY-VALUE-1234", text);
            Assert.Equal(2, lines.Count);

            var header = Fields(lines[0]);
            var row = Fields(lines[1]);
            Assert.Equal(header.Count, row.Count);
            Assert.Equal("Acrobat Pro, Team", row[header.IndexOf("Licence")]);
            Assert.Equal("12500.00", row[header.IndexOf("Spend (PHP)")]);
        }
    }

    [Fact]
    public async Task The_pdf_is_a_rendered_pdf()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, expiresAt: DateTime.UtcNow.Date.AddDays(10));
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 12500m);

            var file = Assert.IsType<FileContentResult>(await ReportFor(db, new FakeTenantProvider(tenantId)).Pdf(default));

            Assert.Equal("application/pdf", file.ContentType);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(file.FileContents, 0, 5));
        }
    }

    [Fact]
    public async Task Another_tenants_licences_are_not_reported()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB, "Theirs");
            await SeedEntitlementAsync(db, tenantB, theirs.Id, seats: 50, cost: 35000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true)).Get(default));

            Assert.Empty(summary.Rows);
            Assert.Equal(0m, summary.TotalSpendInPesos);
        }
    }

    [Fact]
    public async Task A_caller_with_no_organisation_is_refused()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var result = await ReportFor(db, new FakeTenantProvider(null, isSuperAdmin: true)).Get(default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceComplianceReportTests"`
Expected: build FAILS - `The type or namespace name 'LicenceReportsController' could not be found`.

- [ ] **Step 5: Add the PDF builder**

In `src/AssetDesk.Api/Services/PdfReportService.cs`, add to `IPdfReportService` after `BuildPurchaseOrderPdf`:

```csharp
    byte[] BuildLicenceCompliancePdf(LicenceComplianceSummaryDto summary);
```

Add to `PdfReportService`, after `BuildDepreciationPdf`:

```csharp
    public byte[] BuildLicenceCompliancePdf(LicenceComplianceSummaryDto summary)
    {
        var filters = BuildFilterLine(("As of", summary.AsOf.ToString("yyyy-MM-dd")));

        return BuildDocument("Licence Compliance Report", filters, summary.Rows.Count, content =>
        {
            content.Column(col =>
            {
                col.Item().Text(
                    $"{summary.TotalSeatsAssigned} of {summary.TotalSeatsOwned} seats assigned   •   " +
                    $"Spend {FormatCurrency(summary.TotalSpendInPesos, summary.PrimaryCurrency)}");

                // Named rather than left for a reader to spot in the rows: an over-assigned licence
                // is a compliance exposure, and a reclaimable seat is money already spent on nobody.
                if (summary.OverAssignedLicenceCount > 0 || summary.ReclaimableSeats > 0)
                    col.Item().PaddingTop(4).Text(
                        $"{summary.OverAssignedLicenceCount} licence(s) over-assigned by {summary.OverAssignedSeats} seat(s)   •   " +
                        $"{summary.ReclaimableSeats} seat(s) reclaimable");

                if (summary.RenewalsDue > 0 || summary.RenewalsExpired > 0)
                    col.Item().PaddingTop(4).Text(
                        $"{summary.RenewalsDue} renewal(s) due within {LicenceRules.RenewalWindowDays} days   •   " +
                        $"{summary.RenewalsExpired} expired");

                col.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);    // licence
                        c.RelativeColumn(1.5f); // model
                        c.RelativeColumn(1);    // owned
                        c.RelativeColumn(1);    // assigned
                        c.RelativeColumn(1);    // over
                        c.RelativeColumn(1);    // reclaimable
                        c.RelativeColumn(1.5f); // expires
                        c.RelativeColumn(1.5f); // status
                        c.RelativeColumn(2);    // spend
                    });

                    AddHeaderRow(table,
                        "Licence", "Model", "Owned", "Assigned", "Over", "Reclaimable", "Expires", "Renewal", "Spend");

                    foreach (var row in summary.Rows)
                    {
                        AddBodyCell(table, row.Publisher is null ? row.Name : $"{row.Name} ({row.Publisher})");
                        AddBodyCell(table, LicenceModels.Label(row.LicenceModel));
                        AddBodyCell(table, row.SeatsOwned.ToString());
                        AddBodyCell(table, row.SeatsAssigned.ToString());
                        AddBodyCell(table, row.OverAssigned > 0 ? row.OverAssigned.ToString() : "—");
                        AddBodyCell(table, row.Reclaimable > 0 ? row.Reclaimable.ToString() : "—");
                        AddBodyCell(table, row.ExpiresAt?.ToString("yyyy-MM-dd") ?? "—");
                        AddBodyCell(table, row.RenewalStatus);
                        AddBodyCell(table, FormatCurrency(row.SpendInPesos, summary.PrimaryCurrency));
                    }
                });
            });
        });
    }
```

Add `using AssetDesk.Api.Entities;` and `using AssetDesk.Shared;` to the file's usings if they are not already present.

- [ ] **Step 6: Write the report controller**

Create `src/AssetDesk.Api/Controllers/LicenceReportsController.cs`:

```csharp
using System.Globalization;
using System.Text;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// Sits under api/reports beside the other reports and shares their permission, but is its own
/// controller: ReportsController reads through the global query filter and takes no tenant
/// provider, while every licence read scopes to the caller's organisation explicitly. One summary
/// builder feeds all three formats, as BuildDepreciationSummaryAsync does for depreciation.
/// </summary>
[ApiController]
[Route("api/reports/licences")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewReports")]
public class LicenceReportsController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILicenceUsageReader usage,
    IPdfReportService pdf) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<LicenceComplianceSummaryDto>>> Get(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<LicenceComplianceSummaryDto>.Fail("Select an organisation first."));

        return Ok(ApiResponse<LicenceComplianceSummaryDto>.Ok(await BuildSummaryAsync(tenantId, ct)));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var summary = await BuildSummaryAsync(tenantId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Licence,Publisher,Model,Seats Owned,Seats Assigned,Over-Assigned,Reclaimable,Expires,Renewal Status,Spend (PHP)");

        foreach (var r in summary.Rows)
        {
            sb.AppendLine(string.Join(',',
                CsvFormat.Escape(r.Name),
                CsvFormat.Escape(r.Publisher),
                CsvFormat.Escape(AssetDesk.Shared.LicenceModels.Label(r.LicenceModel)),
                r.SeatsOwned.ToString(CultureInfo.InvariantCulture),
                r.SeatsAssigned.ToString(CultureInfo.InvariantCulture),
                r.OverAssigned.ToString(CultureInfo.InvariantCulture),
                r.Reclaimable.ToString(CultureInfo.InvariantCulture),
                r.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                CsvFormat.Escape(r.RenewalStatus),
                r.SpendInPesos.ToString("F2", CultureInfo.InvariantCulture)));
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
            $"Licence Compliance {summary.AsOf:yyyy-MM-dd}.csv");
    }

    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var summary = await BuildSummaryAsync(tenantId, ct);
        return File(pdf.BuildLicenceCompliancePdf(summary), "application/pdf",
            $"Licence Compliance {summary.AsOf:yyyy-MM-dd}.pdf");
    }

    /// <summary>
    /// Deactivated licences are left out, as Retired and Lost assets are left out of the asset
    /// reports: a compliance figure that counts software nobody uses any more overstates exposure.
    /// </summary>
    private async Task<LicenceComplianceSummaryDto> BuildSummaryAsync(Guid tenantId, CancellationToken ct)
    {
        var licences = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive)
            .OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.Publisher, l.LicenceModel, l.ExpiresAt })
            .ToListAsync(ct);

        var usageByLicence = await usage.ReadAsync(tenantId, ct: ct);
        var asOf = DateTime.UtcNow;

        var rows = licences.Select(l =>
        {
            var u = usageByLicence.GetValueOrDefault(l.Id, LicenceUsage.None);
            return new LicenceComplianceRow
            {
                LicenceId = l.Id,
                Name = l.Name,
                Publisher = l.Publisher,
                LicenceModel = l.LicenceModel,
                SeatsOwned = u.SeatsOwned,
                SeatsAssigned = u.SeatsAssigned,
                OverAssigned = u.OverAssigned,
                Reclaimable = u.Reclaimable,
                ExpiresAt = l.ExpiresAt,
                RenewalStatus = LicenceRules.RenewalStatus(l.ExpiresAt, asOf),
                SpendInPesos = u.SpendInPesos
            };
        }).ToList();

        return new LicenceComplianceSummaryDto
        {
            LicenceCount = rows.Count,
            TotalSeatsOwned = rows.Sum(r => r.SeatsOwned),
            TotalSeatsAssigned = rows.Sum(r => r.SeatsAssigned),
            OverAssignedLicenceCount = rows.Count(r => r.OverAssigned > 0),
            OverAssignedSeats = rows.Sum(r => r.OverAssigned),
            ReclaimableSeats = rows.Sum(r => r.Reclaimable),
            RenewalsDue = rows.Count(r => r.RenewalStatus == AssetDesk.Shared.LicenceRenewalStatuses.Due),
            RenewalsExpired = rows.Count(r => r.RenewalStatus == AssetDesk.Shared.LicenceRenewalStatuses.Expired),
            TotalSpendInPesos = rows.Sum(r => r.SpendInPesos),
            PrimaryCurrency = Currencies.PHP,
            AsOf = asOf,
            Rows = rows
        };
    }
}
```

- [ ] **Step 7: Run the report tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests --filter "FullyQualifiedName~LicenceComplianceReportTests|FullyQualifiedName~DepreciationReportTests"`
Expected: PASS - 8 new, and every existing depreciation report test (including its CSV tests) unchanged.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 524 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Services/CsvFormat.cs src/AssetDesk.Api/Controllers/ReportsController.cs src/AssetDesk.Shared/DTOs/LicenceDto.cs src/AssetDesk.Api/Services/PdfReportService.cs src/AssetDesk.Api/Controllers/LicenceReportsController.cs tests/AssetDesk.Api.Tests/LicenceComplianceReportTests.cs
git commit -m "feat(licences): licence compliance report with CSV and PDF exports"
```

---
### Task 9: Web - API client, licence list, editor and nav

**Files:**
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs` (licence calls, after `GetPurchaseOrderPdfUrl`)
- Create: `src/AssetDesk.Web/Components/LicenceDisplay.cs`
- Create: `src/AssetDesk.Web/Components/LicenceEditor.razor`
- Create: `src/AssetDesk.Web/Pages/Licences/Index.razor`
- Modify: `src/AssetDesk.Web/Layout/MainLayout.razor` (nav group after Procurement; renewal count beside `LoadAlertCount`)

**Interfaces:**
- Consumes: the DTOs from Tasks 4-8; endpoints `GET/POST api/licences`, `GET/PUT/DELETE api/licences/{id}`, `POST api/licences/{id}/key/reveal`, `POST api/licences/{id}/entitlements`, `POST api/licences/{id}/seats`, `POST api/licences/{id}/seats/{seatId}/release`, `GET api/licences/device/{assetId}`, `GET api/licences/renewals/count`, `api/reports/licences/export|pdf`.
- Produces (used by Tasks 10 and 11):
  - `ApiClient.GetLicencesAsync() : Task<List<SoftwareLicenceDto>>`
  - `ApiClient.GetLicenceAsync(int id) : Task<SoftwareLicenceDetailDto?>`
  - `ApiClient.SaveLicenceAsync(int? id, UpsertSoftwareLicenceDto dto) : Task<(bool Success, SoftwareLicenceDetailDto? Licence, string? Error)>`
  - `ApiClient.DeactivateLicenceAsync(int id) : Task<(bool Success, string? Error)>`
  - `ApiClient.RevealLicenceKeyAsync(int id) : Task<(bool Success, string? Key, string? Error)>`
  - `ApiClient.AddLicenceEntitlementAsync(int id, AddLicenceEntitlementDto dto) : Task<(bool Success, string? Error)>`
  - `ApiClient.AssignLicenceSeatAsync(int id, AssignLicenceSeatDto dto) : Task<(bool Success, string? Error)>`
  - `ApiClient.ReleaseLicenceSeatAsync(int id, int seatId) : Task<(bool Success, string? Error)>`
  - `ApiClient.GetDeviceLicencesAsync(int assetId) : Task<List<DeviceLicenceDto>>`
  - `ApiClient.GetLicenceRenewalCountAsync() : Task<int>`
  - `LicenceDisplay.RenewalVariant(string status) : string`, `LicenceDisplay.RenewalLabel(string status, int? daysUntilExpiry) : string`
  - `<LicenceEditor IsOpen Licence OnClose OnSaved />` - `Licence` is a `SoftwareLicenceDto?` (null adds); `OnSaved` is `EventCallback<SoftwareLicenceDetailDto>`

There is no Web test project. Verification for Tasks 9-11 is a clean `dotnet build` plus the class-token check in each task; the running pages are exercised against PostgreSQL before merge.

- [ ] **Step 1: Add the client calls**

In `src/AssetDesk.Web/Services/ApiClient.cs`, add after `public string GetPurchaseOrderPdfUrl(int id) => $"api/purchaseorders/{id}/pdf";`:

```csharp

    // Licences - gated on iams:licences:view; writes need iams:licences:manage, and the key reveal
    // iams:licences:reveal. No licence read carries the full key: RevealLicenceKeyAsync is the only
    // call that returns it, and the API records every use of it.
    public async Task<List<SoftwareLicenceDto>> GetLicencesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<SoftwareLicenceDto>>>("api/licences");
        return response?.Data ?? [];
    }

    /// <summary>Null for a licence that does not exist or belongs to another organisation.</summary>
    public async Task<SoftwareLicenceDetailDto?> GetLicenceAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetAsync($"api/licences/{id}");
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadFromJsonAsync<ApiResponse<SoftwareLicenceDetailDto>>())?.Data;
    }

    public async Task<(bool Success, SoftwareLicenceDetailDto? Licence, string? Error)> SaveLicenceAsync(
        int? id, UpsertSoftwareLicenceDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = id is null
            ? await client.PostAsJsonAsync("api/licences", dto)
            : await client.PutAsJsonAsync($"api/licences/{id}", dto);

        if (!response.IsSuccessStatusCode)
            return (false, null, await ReadErrorMessageAsync(response) ?? "Failed to save the licence.");

        return (true, (await response.Content.ReadFromJsonAsync<ApiResponse<SoftwareLicenceDetailDto>>())?.Data, null);
    }

    public async Task<(bool Success, string? Error)> DeactivateLicenceAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/licences/{id}");
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadErrorMessageAsync(response) ?? "Failed to deactivate the licence.");
    }

    public async Task<(bool Success, string? Key, string? Error)> RevealLicenceKeyAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsync($"api/licences/{id}/key/reveal", null);

        if (!response.IsSuccessStatusCode)
            return (false, null, await ReadErrorMessageAsync(response) ?? "Could not reveal the key.");

        return (true, (await response.Content.ReadFromJsonAsync<ApiResponse<RevealedLicenceKeyDto>>())?.Data?.LicenceKey, null);
    }

    public async Task<(bool Success, string? Error)> AddLicenceEntitlementAsync(int id, AddLicenceEntitlementDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/licences/{id}/entitlements", dto);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadErrorMessageAsync(response) ?? "Failed to record the entry.");
    }

    public async Task<(bool Success, string? Error)> AssignLicenceSeatAsync(int id, AssignLicenceSeatDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/licences/{id}/seats", dto);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadErrorMessageAsync(response) ?? "Failed to assign the seat.");
    }

    public async Task<(bool Success, string? Error)> ReleaseLicenceSeatAsync(int id, int seatId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsync($"api/licences/{id}/seats/{seatId}/release", null);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadErrorMessageAsync(response) ?? "Failed to release the seat.");
    }

    public async Task<List<DeviceLicenceDto>> GetDeviceLicencesAsync(int assetId)
    {
        var response = await SafeGetAsync<ApiResponse<List<DeviceLicenceDto>>>($"api/licences/device/{assetId}");
        return response?.Data ?? [];
    }

    public async Task<int> GetLicenceRenewalCountAsync() =>
        await SafeGetAsync<int>("api/licences/renewals/count");
```

- [ ] **Step 2: Add the shared display helper**

Create `src/AssetDesk.Web/Components/LicenceDisplay.cs`:

```csharp
using AssetDesk.Shared;

namespace AssetDesk.Web.Components;

/// <summary>
/// How a licence's renewal status is shown. Shared by the licence list, the licence page and the
/// asset page so an expiring licence reads the same everywhere.
/// </summary>
public static class LicenceDisplay
{
    public static string RenewalVariant(string status) => status switch
    {
        LicenceRenewalStatuses.Expired => "destructive",
        LicenceRenewalStatuses.Due => "warning",
        LicenceRenewalStatuses.Active => "success",
        _ => "secondary"
    };

    public static string RenewalLabel(string status, int? daysUntilExpiry) => status switch
    {
        LicenceRenewalStatuses.Expired when daysUntilExpiry is { } days =>
            -days == 1 ? "Expired yesterday" : $"Expired {-days} days ago",
        LicenceRenewalStatuses.Expired => "Expired",
        LicenceRenewalStatuses.Due => daysUntilExpiry switch
        {
            0 => "Renews today",
            1 => "Renews tomorrow",
            { } days => $"Renews in {days} days",
            _ => "Renewal due"
        },
        LicenceRenewalStatuses.Active => "Active",
        _ => "Perpetual"
    };
}
```

- [ ] **Step 3: Write the editor**

Create `src/AssetDesk.Web/Components/LicenceEditor.razor`:

```razor
@using System.Globalization
@using AssetDesk.Shared
@inject ApiClient Api
@inject PermissionChecker PermissionChecker

<Modal IsOpen="@IsOpen" Title="@(Licence is null ? "Add Licence" : "Edit Licence")" OnClose="OnClose">
    <ChildContent>
        <div class="space-y-4">
            <FormField LabelText="Name" Required="true">
                <Input Value="@_name" ValueChanged="v => _name = v" Placeholder="e.g. Microsoft 365 Business Standard" MaxLength="200" />
            </FormField>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <FormField LabelText="Publisher">
                    <Input Value="@_publisher" ValueChanged="v => _publisher = v" Placeholder="e.g. Microsoft" MaxLength="200" />
                </FormField>
                <FormField LabelText="Counted" Required="true"
                           HelperText="@(Licence?.ModelLocked == true ? "Fixed once seats have been assigned." : null)">
                    <Select Value="@_model" ValueChanged="v => _model = v ?? LicenceModels.PerUser" Disabled="@(Licence?.ModelLocked == true)">
                        @foreach (var model in LicenceModels.All)
                        {
                            <option value="@model">@LicenceModels.Label(model)</option>
                        }
                    </Select>
                </FormField>
            </div>

            @if (_canSeeSuppliers)
            {
                <FormField LabelText="Supplier">
                    <Select Value="@_supplierId" ValueChanged="v => _supplierId = v ?? string.Empty" Placeholder="No supplier">
                        @foreach (var supplier in _suppliers.Where(s => s.IsActive || s.Id.ToString(CultureInfo.InvariantCulture) == _supplierId))
                        {
                            <option value="@supplier.Id">@supplier.Name</option>
                        }
                    </Select>
                </FormField>
            }

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <FormField LabelText="Licence key"
                           HelperText="@(Licence?.MaskedKey is null ? "Stored masked. Revealing it later is recorded." : "Leave blank to keep the current key.")">
                    @* Never pre-filled: this form is not given the key, so there is nothing to show and a
                       blank field on save means "unchanged". *@
                    <Input Value="@_key" ValueChanged="v => _key = v" Placeholder="@(Licence?.MaskedKey ?? "Optional")"
                           MaxLength="500" AutoComplete="off" Disabled="@_clearKey" />
                </FormField>
                <FormField LabelText="Expires" HelperText="Leave blank for a perpetual licence.">
                    <Input Type="date" Value="@_expiresAt" ValueChanged="v => _expiresAt = v" />
                </FormField>
            </div>

            @if (Licence?.MaskedKey is not null)
            {
                <Checkbox Value="@_clearKey" ValueChanged="v => _clearKey = v">Remove the stored key</Checkbox>
            }

            <FormField LabelText="Notes">
                <Textarea Value="@_notes" ValueChanged="v => _notes = v" Rows="2" Placeholder="Agreement number, reseller contact, renewal terms..." />
            </FormField>

            @if (Licence is not null)
            {
                <Checkbox Value="@_isActive" ValueChanged="v => _isActive = v">Active</Checkbox>
            }

            @if (!string.IsNullOrEmpty(_error))
            {
                <Alert Variant="destructive">@_error</Alert>
            }
        </div>
    </ChildContent>
    <FooterContent>
        <Button Variant="ghost" OnClick="OnClose">Cancel</Button>
        <Button OnClick="Save" Loading="@_saving" Disabled="@string.IsNullOrWhiteSpace(_name)">
            @(Licence is null ? "Add" : "Save")
        </Button>
    </FooterContent>
</Modal>

@code {
    [Parameter] public bool IsOpen { get; set; }

    /// <summary>The licence being edited, or null to add one.</summary>
    [Parameter] public SoftwareLicenceDto? Licence { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<SoftwareLicenceDetailDto> OnSaved { get; set; }

    private bool _wasOpen;
    private string _name = "";
    private string _publisher = "";
    private string _model = LicenceModels.PerUser;
    private string _supplierId = "";
    private string _key = "";
    private bool _clearKey;
    private string _expiresAt = "";
    private string _notes = "";
    private bool _isActive = true;
    private string? _error;
    private bool _saving;

    private List<SupplierDto> _suppliers = [];
    private bool _canSeeSuppliers;

    protected override async Task OnParametersSetAsync()
    {
        // Reset only as the dialog opens. Resetting on every parameter pass would wipe what the user
        // has typed whenever the parent re-renders while the dialog is open.
        if (IsOpen && !_wasOpen)
        {
            _name = Licence?.Name ?? "";
            _publisher = Licence?.Publisher ?? "";
            _model = Licence?.LicenceModel ?? LicenceModels.PerUser;
            _supplierId = Licence?.SupplierId?.ToString(CultureInfo.InvariantCulture) ?? "";
            _key = "";
            _clearKey = false;
            _expiresAt = Licence?.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            _notes = Licence?.Notes ?? "";
            _isActive = Licence?.IsActive ?? true;
            _error = null;

            // Suppliers are procurement data. Someone who manages licences without procurement
            // access still gets the rest of the form, rather than a 403 as it opens.
            _canSeeSuppliers = await PermissionChecker.HasPermissionAsync("iams:procurement:view");
            if (_canSeeSuppliers)
                _suppliers = await Api.GetSuppliersAsync();
        }

        _wasOpen = IsOpen;
    }

    private async Task Save()
    {
        _error = null;
        _saving = true;

        var dto = new UpsertSoftwareLicenceDto
        {
            Name = _name.Trim(),
            Publisher = BlankToNull(_publisher),
            // With the supplier field hidden, keep whatever is stored rather than clearing it.
            SupplierId = _canSeeSuppliers
                ? int.TryParse(_supplierId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var supplierId) ? supplierId : null
                : Licence?.SupplierId,
            LicenceModel = _model,
            LicenceKey = _clearKey ? null : BlankToNull(_key),
            ClearKey = _clearKey,
            ExpiresAt = DateTime.TryParse(_expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires)
                ? expires
                : null,
            Notes = BlankToNull(_notes),
            IsActive = _isActive
        };

        var (success, saved, error) = await Api.SaveLicenceAsync(Licence?.Id, dto);
        _saving = false;

        // The API is the authority on duplicate names and the model lock; show its message as-is.
        if (!success || saved is null)
        {
            _error = error ?? "Could not save the licence.";
            return;
        }

        await OnSaved.InvokeAsync(saved);
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] **Step 4: Write the list page**

Create `src/AssetDesk.Web/Pages/Licences/Index.razor`:

```razor
@page "/licences"
@attribute [Authorize]
@using AssetDesk.Shared
@inject ApiClient Api
@inject AuthService AuthService
@inject IJSRuntime JS
@inject IConfiguration Configuration
@inject NavigationManager Navigation
@inject SnackbarService Snackbar
@inject PermissionChecker PermissionChecker

<PageTitle>Licences - AssetDesk</PageTitle>

<PermissionView Permission="iams:licences:view" ShowDeniedMessage="true">
<div class="space-y-6">
    <div class="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div>
            <h1 class="text-2xl font-bold text-foreground">Licences</h1>
            <p class="text-muted-foreground mt-1">
                Software the organisation is entitled to use, who holds its seats, and when it renews.
                Assigning more seats than are owned is allowed - it shows here so it can be put right.
            </p>
        </div>
        <div class="flex flex-wrap items-center gap-2">
            <PermissionView Permission="iams:reports:view">
                <Button Variant="outline" OnClick="() => DownloadReport(pdf: false)">CSV</Button>
                <Button Variant="outline" OnClick="() => DownloadReport(pdf: true)">PDF</Button>
            </PermissionView>
            <PermissionView Permission="iams:licences:manage">
                <Button OnClick="() => _showEditor = true">
                    <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4" />
                    </svg>
                    Add Licence
                </Button>
            </PermissionView>
        </div>
    </div>

    <Card Class="overflow-hidden">
        @if (_loading)
        {
            <div class="p-4 sm:p-5 space-y-2">
                @for (int i = 0; i < 4; i++)
                {
                    <div class="h-12 rounded-xl skeleton"></div>
                }
            </div>
        }
        else if (_licences.Count == 0)
        {
            <div class="p-4 sm:p-5">
                <EmptyState Title="No licences yet" Description="Add a licence, or receive a software line on a purchase order." />
            </div>
        }
        else
        {
            <Table>
                <TableHeader>
                    <TableRow>
                        <TableHead>Licence</TableHead>
                        <TableHead>Counted</TableHead>
                        <TableHead>Seats</TableHead>
                        <TableHead>Reclaimable</TableHead>
                        <TableHead>Renewal</TableHead>
                        <TableHead>Status</TableHead>
                    </TableRow>
                </TableHeader>
                <TableBody>
                    @foreach (var licence in _licences)
                    {
                        <TableRow @key="licence.Id" @onclick="() => Navigation.NavigateTo($"/licences/{licence.Id}")"
                                  Class="@(licence.IsActive ? "cursor-pointer" : "cursor-pointer opacity-50")">
                            <TableCell>
                                <span class="font-medium text-foreground text-sm">@licence.Name</span>
                                @if (!string.IsNullOrEmpty(licence.Publisher))
                                {
                                    <p class="text-xs text-muted-foreground">@licence.Publisher</p>
                                }
                            </TableCell>
                            <TableCell>@LicenceModels.Label(licence.LicenceModel)</TableCell>
                            <TableCell>
                                <div class="flex flex-wrap items-center gap-2">
                                    <span class="whitespace-nowrap">@licence.SeatsAssigned of @licence.SeatsOwned</span>
                                    @if (licence.OverAssigned > 0)
                                    {
                                        <Badge Variant="destructive">@licence.OverAssigned over</Badge>
                                    }
                                </div>
                            </TableCell>
                            <TableCell>
                                @if (licence.Reclaimable > 0)
                                {
                                    <Badge Variant="warning">@licence.Reclaimable</Badge>
                                }
                                else
                                {
                                    <span class="text-muted-foreground">—</span>
                                }
                            </TableCell>
                            <TableCell>
                                <Badge Variant="@LicenceDisplay.RenewalVariant(licence.RenewalStatus)">
                                    @LicenceDisplay.RenewalLabel(licence.RenewalStatus, licence.DaysUntilExpiry)
                                </Badge>
                            </TableCell>
                            <TableCell>
                                <Badge Variant="@(licence.IsActive ? "success" : "secondary")">@(licence.IsActive ? "Active" : "Inactive")</Badge>
                            </TableCell>
                        </TableRow>
                    }
                </TableBody>
            </Table>
        }
    </Card>
</div>

<LicenceEditor IsOpen="@_showEditor" OnClose="() => _showEditor = false" OnSaved="OnCreated" />
</PermissionView>

@code {
    private List<SoftwareLicenceDto> _licences = [];
    private bool _loading = true;
    private bool _showEditor;

    protected override async Task OnInitializedAsync()
    {
        // Matches the page's PermissionView gate - see the identical note on
        // Pages/Procurement/Suppliers.razor.
        if (!await PermissionChecker.HasPermissionAsync("iams:licences:view"))
        {
            _loading = false;
            return;
        }

        _licences = await Api.GetLicencesAsync();
        _loading = false;
    }

    private void OnCreated(SoftwareLicenceDetailDto saved)
    {
        _showEditor = false;
        Snackbar.Success("Licence added");
        Navigation.NavigateTo($"/licences/{saved.Licence.Id}");
    }

    private async Task DownloadReport(bool pdf)
    {
        var url = pdf ? Api.GetReportPdfUrl("licences") : Api.GetReportExportUrl("licences");
        var token = await AuthService.GetTokenAsync();
        var baseUrl = (Configuration["ApiBaseUrl"] ?? "https://localhost:5021").TrimEnd('/');
        await JS.InvokeVoidAsync("downloadWithAuth", $"{baseUrl}/{url}", token);
    }
}
```

- [ ] **Step 5: Add the nav group and renewal badge**

In `src/AssetDesk.Web/Layout/MainLayout.razor`, immediately after the closing `</PermissionView>` of the `iams:procurement:view` nav group (the one containing the Suppliers and Purchase Orders `NavItem`s), add:

```razor

                <PermissionView Permission="iams:licences:view">
                    <div class="mt-2 space-y-1">
                        <div class="mx-2 my-2 h-px bg-sidebar-border"></div>
                        <p class="@SectionLabelClasses">Licences</p>
                        <NavItem Href="/licences" Icon="tag" Label="Software Licences" BadgeCount="@_licenceRenewalCount" Collapsed="_sidebarCollapsed" OnNavigation="CloseMobileMenu" />
                    </div>
                </PermissionView>
```

In the `@code` block, add a field beside `_alertCount`:

```csharp
    private int _licenceRenewalCount;
```

Immediately after the existing `await LoadAlertCount();` call, add:

```csharp
        await LoadLicenceRenewalCount();
```

and after the `LoadAlertCount` method:

```csharp
    /// <summary>
    /// Licences due within ninety days or already expired. Asked only of someone who can see
    /// licences - the endpoint is gated, and a guaranteed 403 on every page load is noise.
    /// </summary>
    private async Task LoadLicenceRenewalCount()
    {
        if (!await PermissionChecker.HasPermissionAsync("iams:licences:view"))
            return;

        _licenceRenewalCount = await Api.GetLicenceRenewalCountAsync();
    }
```

`LoadAlertCount` already runs only on the authenticated path - CLAUDE.md's `AuthorizeRouteView` gotcha - and this call sits beside it, so it inherits that gate.

- [ ] **Step 6: Build**

Stop any running API or Web server, then run: `dotnet build`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 7: Check every class token exists in the compiled stylesheet**

For each distinct class token in `Components/LicenceEditor.razor`, `Pages/Licences/Index.razor` and the MainLayout addition, confirm it appears in `src/AssetDesk.Web/wwwroot/css/app.css` using a literal match on the CSS selector - e.g. `grep -F '.sm\:grid-cols-2' src/AssetDesk.Web/wwwroot/css/app.css`. Tailwind escapes `:` and `/` in selectors, so a regex that does not allow for the backslash reports false misses. Every token used above was checked when this plan was written; confirm none was added. Record the check in the task report.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Web/Services/ApiClient.cs src/AssetDesk.Web/Components/LicenceDisplay.cs src/AssetDesk.Web/Components/LicenceEditor.razor src/AssetDesk.Web/Pages/Licences/Index.razor src/AssetDesk.Web/Layout/MainLayout.razor
git commit -m "feat(licences): licence list, editor and renewal badge in the web app"
```

---

### Task 10: Web - licence page

**Files:**
- Create: `src/AssetDesk.Web/Pages/Licences/Detail.razor`

**Interfaces:**
- Consumes: Task 9's client calls, `LicenceDisplay`, `LicenceEditor`; `ApiClient.GetUserListAsync() : Task<List<UserListItem>?>` (needs `iams:users:read`); `ApiClient.GetAssetsAsync(..., int page = 1, int pageSize = 20) : Task<PagedResponse<AssetDto>?>`; `CurrencyFormat.Format(decimal?, string?)`, `CurrencyFormat.SymbolFor(string?)`, `CurrencyFormat.Php`, `CurrencyFormat.Usd`.

- [ ] **Step 1: Write the page**

Create `src/AssetDesk.Web/Pages/Licences/Detail.razor`:

```razor
@page "/licences/{Id:int}"
@attribute [Authorize]
@using System.Globalization
@using AssetDesk.Shared
@inject ApiClient Api
@inject SnackbarService Snackbar
@inject PermissionChecker PermissionChecker

<PageTitle>@(_detail?.Licence.Name ?? "Licence") - AssetDesk</PageTitle>

<PermissionView Permission="iams:licences:view" ShowDeniedMessage="true">
@if (_loading)
{
    <div class="space-y-6 animate-pulse">
        <div class="flex items-center gap-4">
            <div class="w-8 h-8 rounded-lg skeleton"></div>
            <div class="h-8 w-48 rounded skeleton"></div>
        </div>
        <div class="bg-card rounded-2xl border border-border p-6">
            <div class="space-y-4">
                <div class="h-6 w-1/3 rounded skeleton"></div>
                <div class="h-4 w-1/2 rounded skeleton"></div>
            </div>
        </div>
    </div>
}
else if (_detail is null)
{
    <Card Class="p-6">
        <EmptyState Title="Licence not found" Description="It may belong to another organisation.">
            <a href="/licences" class="inline-flex items-center gap-2 px-4 py-2 bg-primary text-white rounded-xl font-medium hover:bg-primary/90 transition-all duration-200">
                Back to Licences
            </a>
        </EmptyState>
    </Card>
}
else
{
    var licence = _detail.Licence;

    <div class="space-y-4 sm:space-y-6">
        <div class="flex flex-col gap-4">
            <div class="flex items-center gap-2 sm:gap-4">
                <a href="/licences" class="p-2 rounded-lg text-muted-foreground hover:text-muted-foreground hover:bg-muted transition-all duration-200">
                    <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 19l-7-7 7-7" />
                    </svg>
                </a>
                <div class="flex-1 min-w-0">
                    <div class="flex items-center gap-3 flex-wrap">
                        <h1 class="text-xl sm:text-2xl font-bold text-foreground truncate">@licence.Name</h1>
                        <Badge Variant="secondary">@LicenceModels.Label(licence.LicenceModel)</Badge>
                        <Badge Variant="@LicenceDisplay.RenewalVariant(licence.RenewalStatus)">
                            @LicenceDisplay.RenewalLabel(licence.RenewalStatus, licence.DaysUntilExpiry)
                        </Badge>
                        @if (!licence.IsActive)
                        {
                            <Badge Variant="secondary">Inactive</Badge>
                        }
                    </div>
                    <p class="text-sm text-muted-foreground mt-1">@(licence.Publisher ?? "—")</p>
                </div>
            </div>
            <PermissionView Permission="iams:licences:manage">
                <div class="flex flex-wrap items-center gap-2 sm:gap-3">
                    <Button Variant="outline" OnClick="() => _showEditor = true">Edit</Button>
                    @if (licence.IsActive)
                    {
                        <Button OnClick="OpenEntitlement">Record Seats or Renewal</Button>
                        <Button OnClick="OpenAssign">Assign Seat</Button>
                        <Button Variant="outline" Class="border-destructive/30 text-destructive hover:bg-destructive/10 hover:text-destructive" OnClick="() => _showDeactivate = true">
                            Deactivate
                        </Button>
                    }
                </div>
            </PermissionView>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-3 gap-4 sm:gap-6">
            <div class="lg:col-span-2 space-y-4 sm:space-y-6">
                <Card>
                    <CardHeader>
                        <CardTitle>Details</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <div class="grid grid-cols-1 sm:grid-cols-2 gap-4 sm:gap-6">
                            <DetailItem Label="Supplier" Value="@(licence.SupplierName ?? "—")" />
                            <DetailItem Label="Expires" Value="@(licence.ExpiresAt?.ToString("MMM dd, yyyy") ?? "Never")" />
                            <DetailItem Label="Spend" Value="@CurrencyFormat.Format(_detail.SpendInPesos, CurrencyFormat.Php)" />
                            <div>
                                <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide mb-1">Licence key</p>
                                @if (licence.MaskedKey is null)
                                {
                                    <p class="text-sm text-foreground">—</p>
                                }
                                else if (_revealedKey is not null)
                                {
                                    <div class="flex flex-wrap items-center gap-2">
                                        <span class="text-sm text-foreground font-mono break-all">@_revealedKey</span>
                                        <Button Variant="ghost" Size="sm" OnClick="() => _revealedKey = null">Hide</Button>
                                    </div>
                                }
                                else
                                {
                                    <div class="flex flex-wrap items-center gap-2">
                                        <span class="text-sm text-foreground font-mono">@licence.MaskedKey</span>
                                        @* Revealing is recorded against the person who does it; the button says
                                           nothing more because the audit trail, not a warning, is the control. *@
                                        <PermissionView Permission="iams:licences:reveal">
                                            <Button Variant="ghost" Size="sm" OnClick="RevealKey" Loading="@_revealing">Reveal</Button>
                                        </PermissionView>
                                    </div>
                                }
                            </div>
                        </div>
                        @if (!string.IsNullOrEmpty(licence.Notes))
                        {
                            <div class="mt-4">
                                <p class="text-xs font-medium text-muted-foreground uppercase tracking-wide mb-1">Notes</p>
                                <p class="text-sm text-foreground whitespace-pre-wrap">@licence.Notes</p>
                            </div>
                        }
                    </CardContent>
                </Card>

                <Card Class="overflow-hidden">
                    <div class="p-4 sm:p-5 pb-2 flex items-center justify-between gap-3">
                        <h3 class="text-lg font-semibold leading-none tracking-tight text-foreground">Seats</h3>
                        @if (_detail.Seats.Any(s => !s.IsActive))
                        {
                            <Button Variant="ghost" Size="sm" OnClick="() => _showReleased = !_showReleased">
                                @(_showReleased ? "Hide released" : $"Show released ({_detail.Seats.Count(s => !s.IsActive)})")
                            </Button>
                        }
                    </div>
                    @if (!VisibleSeats.Any())
                    {
                        <div class="p-4 sm:p-5 pt-0">
                            <p class="text-sm text-muted-foreground">No seats assigned.</p>
                        </div>
                    }
                    else
                    {
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead>Held by</TableHead>
                                    <TableHead>Assigned</TableHead>
                                    <TableHead>Status</TableHead>
                                    <TableHead Class="text-right">&nbsp;</TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                @foreach (var seat in VisibleSeats)
                                {
                                    <TableRow @key="seat.Id" Class="@(seat.IsActive ? "" : "opacity-50")">
                                        <TableCell>
                                            @if (seat.AssetId is { } assetId)
                                            {
                                                <a href="/assets/@assetId" class="font-medium text-foreground text-sm hover:underline">@seat.AssetTag</a>
                                                @if (!string.IsNullOrEmpty(seat.AssetName))
                                                {
                                                    <p class="text-xs text-muted-foreground">@seat.AssetName</p>
                                                }
                                            }
                                            else
                                            {
                                                <span class="font-medium text-foreground text-sm">@(seat.UserName ?? "Unknown user")</span>
                                            }
                                        </TableCell>
                                        <TableCell>
                                            <span class="whitespace-nowrap">@seat.AssignedAt.ToString("MMM dd, yyyy")</span>
                                            @if (seat.AssignedByName is not null)
                                            {
                                                <p class="text-xs text-muted-foreground">by @seat.AssignedByName</p>
                                            }
                                        </TableCell>
                                        <TableCell>
                                            <div class="flex flex-wrap items-center gap-2">
                                                @if (seat.IsActive)
                                                {
                                                    <Badge Variant="success">Active</Badge>
                                                    @if (seat.IsReclaimable)
                                                    {
                                                        <Badge Variant="warning">Reclaimable</Badge>
                                                    }
                                                }
                                                else
                                                {
                                                    <Badge Variant="secondary">Released @seat.ReleasedAt?.ToString("MMM dd, yyyy")</Badge>
                                                }
                                            </div>
                                        </TableCell>
                                        <TableCell Class="text-right">
                                            @if (seat.IsActive)
                                            {
                                                <PermissionView Permission="iams:licences:manage">
                                                    <Button Variant="ghost" Size="sm" OnClick="() => ReleaseSeat(seat)" Loading="@(_releasingSeatId == seat.Id)">Release</Button>
                                                </PermissionView>
                                            }
                                        </TableCell>
                                    </TableRow>
                                }
                            </TableBody>
                        </Table>
                    }
                </Card>

                <Card Class="overflow-hidden">
                    <div class="p-4 sm:p-5 pb-2">
                        <h3 class="text-lg font-semibold leading-none tracking-tight text-foreground">Seats bought and renewals</h3>
                    </div>
                    @if (_detail.Entitlements.Count == 0)
                    {
                        <div class="p-4 sm:p-5 pt-0">
                            <p class="text-sm text-muted-foreground">Nothing recorded yet.</p>
                        </div>
                    }
                    else
                    {
                        <Table>
                            <TableHeader>
                                <TableRow>
                                    <TableHead>Date</TableHead>
                                    <TableHead>Entry</TableHead>
                                    <TableHead Class="text-right">Seats</TableHead>
                                    <TableHead Class="text-right">Cost</TableHead>
                                    <TableHead>Source</TableHead>
                                </TableRow>
                            </TableHeader>
                            <TableBody>
                                @foreach (var entry in _detail.Entitlements)
                                {
                                    <TableRow @key="entry.Id">
                                        <TableCell><span class="whitespace-nowrap">@entry.EntitlementDate.ToString("MMM dd, yyyy")</span></TableCell>
                                        <TableCell>
                                            <span class="text-sm text-foreground">@EntryLabel(entry)</span>
                                            @if (entry.ExpiresAtAfter is { } until)
                                            {
                                                <p class="text-xs text-muted-foreground">until @until.ToString("MMM dd, yyyy")</p>
                                            }
                                            @if (!string.IsNullOrEmpty(entry.Notes))
                                            {
                                                <p class="text-xs text-muted-foreground whitespace-pre-wrap">@entry.Notes</p>
                                            }
                                        </TableCell>
                                        <TableCell Class="text-right">@(entry.SeatsAdded > 0 ? $"+{entry.SeatsAdded}" : entry.SeatsAdded.ToString(CultureInfo.InvariantCulture))</TableCell>
                                        <TableCell Class="text-right">
                                            @* The row's own currency, then pesos for a foreign one - the same rule the
                                               asset register follows, so a USD renewal never shows a peso sign. *@
                                            <span class="whitespace-nowrap">@CurrencyFormat.Format(entry.Cost, entry.Currency)</span>
                                            @if (entry.Currency != CurrencyFormat.Php)
                                            {
                                                <p class="text-xs text-muted-foreground whitespace-nowrap">@CurrencyFormat.Format(entry.CostInPesos, CurrencyFormat.Php)</p>
                                            }
                                        </TableCell>
                                        <TableCell>
                                            @if (entry.PurchaseOrderId is { } orderId)
                                            {
                                                <a href="/purchase-orders/@orderId" class="text-sm text-primary hover:underline">@entry.PurchaseOrderReference</a>
                                            }
                                            else
                                            {
                                                <span class="text-sm text-muted-foreground">@(entry.CreatedByName is null ? "Recorded by hand" : $"Recorded by {entry.CreatedByName}")</span>
                                            }
                                        </TableCell>
                                    </TableRow>
                                }
                            </TableBody>
                        </Table>
                    }
                </Card>
            </div>

            <div class="space-y-4 sm:space-y-6">
                <Card>
                    <CardHeader>
                        <CardTitle>Compliance</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <div class="space-y-3 text-sm">
                            <div class="flex justify-between">
                                <span class="text-muted-foreground">Seats owned</span>
                                <span class="text-foreground font-medium">@licence.SeatsOwned</span>
                            </div>
                            <div class="flex justify-between">
                                <span class="text-muted-foreground">Seats assigned</span>
                                <span class="text-foreground font-medium">@licence.SeatsAssigned</span>
                            </div>
                            <div class="flex justify-between">
                                <span class="text-muted-foreground">Over-assigned</span>
                                <span class="@(licence.OverAssigned > 0 ? "text-destructive font-semibold" : "text-foreground font-medium")">@licence.OverAssigned</span>
                            </div>
                            <div class="flex justify-between">
                                <span class="text-muted-foreground">Reclaimable</span>
                                <span class="text-foreground font-medium">@licence.Reclaimable</span>
                            </div>
                        </div>
                    </CardContent>
                </Card>
            </div>
        </div>
    </div>

    <LicenceEditor IsOpen="@_showEditor" Licence="licence" OnClose="() => _showEditor = false" OnSaved="OnEdited" />

    <Modal IsOpen="@_showEntitlement" Title="Record Seats or Renewal" OnClose="() => _showEntitlement = false">
        <ChildContent>
            <div class="space-y-4">
                <FormField LabelText="This entry">
                    <Select Value="@_entryKind" ValueChanged="v => _entryKind = v ?? EntryAddSeats">
                        <option value="@EntryAddSeats">Adds seats</option>
                        <option value="@EntryRenew">Renews existing seats</option>
                        <option value="@EntryReduce">Removes seats</option>
                    </Select>
                </FormField>

                <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    @if (_entryKind != EntryRenew)
                    {
                        <FormField LabelText="Seats" Required="true">
                            <Input Type="number" Value="@_entrySeats" ValueChanged="v => _entrySeats = v" Min="1" />
                        </FormField>
                    }
                    <FormField LabelText="Date">
                        <Input Type="date" Value="@_entryDate" ValueChanged="v => _entryDate = v" />
                    </FormField>
                </div>

                <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <FormField LabelText="Cost">
                        <Input Type="number" Value="@_entryCost" ValueChanged="v => _entryCost = v" Min="0" Placeholder="0" />
                    </FormField>
                    <FormField LabelText="Currency">
                        <Select Value="@_entryCurrency" ValueChanged="OnEntryCurrencyChanged">
                            @foreach (var code in EntryCurrencies)
                            {
                                <option value="@code">@code</option>
                            }
                        </Select>
                    </FormField>
                </div>

                @if (_entryCurrency != CurrencyFormat.Php)
                {
                    <FormField LabelText="@($"Exchange Rate ({CurrencyFormat.SymbolFor(CurrencyFormat.Php)} per 1 {_entryCurrency})")" Required="true">
                        <Input Type="number" Value="@_entryRate" ValueChanged="v => _entryRate = v" Placeholder="58.20" />
                    </FormField>
                }

                <FormField LabelText="@(_entryKind == EntryRenew ? "Renewed until" : "New expiry (optional)")" Required="@(_entryKind == EntryRenew)">
                    <Input Type="date" Value="@_entryExpiry" ValueChanged="v => _entryExpiry = v" />
                </FormField>

                <FormField LabelText="Notes" Required="@(_entryKind == EntryReduce)">
                    <Textarea Value="@_entryNotes" ValueChanged="v => _entryNotes = v" Rows="2"
                              Placeholder="@(_entryKind == EntryReduce ? "Why are seats being removed?" : "Invoice number, agreement reference...")" />
                </FormField>

                @if (!string.IsNullOrEmpty(_entryError))
                {
                    <Alert Variant="destructive">@_entryError</Alert>
                }
            </div>
        </ChildContent>
        <FooterContent>
            <Button Variant="ghost" OnClick="() => _showEntitlement = false">Cancel</Button>
            <Button OnClick="SaveEntitlement" Loading="@_savingEntry">Record</Button>
        </FooterContent>
    </Modal>

    <Modal IsOpen="@_showAssign" Title="Assign Seat" OnClose="() => _showAssign = false">
        <ChildContent>
            <div class="space-y-4">
                @if (licence.LicenceModel == LicenceModels.PerUser)
                {
                    <FormField LabelText="Person" Required="true">
                        <Select Value="@_assignTarget" ValueChanged="v => _assignTarget = v ?? string.Empty" Placeholder="Select a person...">
                            @foreach (var user in (_users ?? []).Where(u => !HeldBy(u.Id)))
                            {
                                <option value="@user.Id">@user.FullName @(user.Department is not null ? $"({user.Department})" : "")</option>
                            }
                        </Select>
                    </FormField>
                }
                else
                {
                    <FormField LabelText="Device" Required="true">
                        <Select Value="@_assignTarget" ValueChanged="v => _assignTarget = v ?? string.Empty" Placeholder="Select a device...">
                            @foreach (var asset in (_devices ?? []).Where(a => !HeldBy(a.Id)))
                            {
                                <option value="@asset.Id">@asset.AssetTag @(string.IsNullOrEmpty(asset.Name) ? "" : $"- {asset.Name}")</option>
                            }
                        </Select>
                    </FormField>
                }

                @if (licence.SeatsAssigned >= licence.SeatsOwned)
                {
                    @* Allowed on purpose - see LicencesController.AssignSeat - but never silently. *@
                    <Alert Variant="warning">
                        All @licence.SeatsOwned owned seats are already assigned. This seat will be recorded as over-assigned.
                    </Alert>
                }

                <FormField LabelText="Notes (Optional)">
                    <Textarea Value="@_assignNotes" ValueChanged="v => _assignNotes = v" Rows="2" Placeholder="Assignment notes..." />
                </FormField>

                @if (!string.IsNullOrEmpty(_assignError))
                {
                    <Alert Variant="destructive">@_assignError</Alert>
                }
            </div>
        </ChildContent>
        <FooterContent>
            <Button Variant="ghost" OnClick="() => _showAssign = false">Cancel</Button>
            <Button OnClick="AssignSeat" Loading="@_assigning" Disabled="@string.IsNullOrEmpty(_assignTarget)">Assign</Button>
        </FooterContent>
    </Modal>

    <Modal IsOpen="@_showDeactivate" Title="Deactivate Licence" OnClose="() => _showDeactivate = false">
        <ChildContent>
            <p class="text-muted-foreground">
                Deactivate <strong>@licence.Name</strong>? It stays on record with its seats and history, drops out of the
                compliance report, and can be reactivated by editing it.
            </p>
        </ChildContent>
        <FooterContent>
            <Button Variant="ghost" OnClick="() => _showDeactivate = false">Keep Active</Button>
            <Button Variant="destructive" OnClick="Deactivate" Loading="@_deactivating">Deactivate</Button>
        </FooterContent>
    </Modal>
}
</PermissionView>

@code {
    [Parameter] public int Id { get; set; }

    private const string EntryAddSeats = "AddSeats";
    private const string EntryRenew = "Renew";
    private const string EntryReduce = "Reduce";
    private static readonly string[] EntryCurrencies = [CurrencyFormat.Php, CurrencyFormat.Usd];

    private SoftwareLicenceDetailDto? _detail;
    private bool _loading = true;

    private bool _showEditor;
    private bool _showDeactivate;
    private bool _deactivating;

    private string? _revealedKey;
    private bool _revealing;

    private bool _showReleased;
    private int? _releasingSeatId;

    private bool _showEntitlement;
    private bool _savingEntry;
    private string? _entryError;
    private string _entryKind = EntryAddSeats;
    private string _entrySeats = "";
    private string _entryCost = "";
    private string _entryCurrency = CurrencyFormat.Php;
    private string _entryRate = "1";
    private string _entryDate = "";
    private string _entryExpiry = "";
    private string _entryNotes = "";

    private bool _showAssign;
    private bool _assigning;
    private string? _assignError;
    private string _assignTarget = "";
    private string _assignNotes = "";
    private List<UserListItem>? _users;
    private List<AssetDto>? _devices;

    private IEnumerable<LicenceSeatDto> VisibleSeats =>
        _detail?.Seats.Where(s => s.IsActive || _showReleased) ?? [];

    protected override async Task OnInitializedAsync()
    {
        // Matches the page's PermissionView gate - see the identical note on
        // Pages/Procurement/Suppliers.razor.
        if (!await PermissionChecker.HasPermissionAsync("iams:licences:view"))
        {
            _loading = false;
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _detail = await Api.GetLicenceAsync(Id);
        _loading = false;
    }

    private static string EntryLabel(LicenceEntitlementDto entry) => entry.SeatsAdded switch
    {
        > 0 => "Seats added",
        < 0 => "Seats removed",
        _ => "Renewal"
    };

    private bool HeldBy(string userId) =>
        _detail?.Seats.Any(s => s.IsActive && s.UserId == userId) == true;

    private bool HeldBy(int assetId) =>
        _detail?.Seats.Any(s => s.IsActive && s.AssetId == assetId) == true;

    private async Task RevealKey()
    {
        _revealing = true;
        var (success, key, error) = await Api.RevealLicenceKeyAsync(Id);
        _revealing = false;

        if (success)
            _revealedKey = key;
        else
            Snackbar.Error(error ?? "Could not reveal the key.");
    }

    private void OnEdited(SoftwareLicenceDetailDto saved)
    {
        _showEditor = false;
        _detail = saved;
        // A revealed key may be the one that was just replaced or removed.
        _revealedKey = null;
        Snackbar.Success("Licence updated");
    }

    private async Task Deactivate()
    {
        _deactivating = true;
        var (success, error) = await Api.DeactivateLicenceAsync(Id);
        _deactivating = false;
        _showDeactivate = false;

        if (!success)
        {
            Snackbar.Error(error ?? "Could not deactivate the licence.");
            return;
        }

        Snackbar.Success("Licence deactivated");
        await LoadAsync();
    }

    private void OpenEntitlement()
    {
        _entryKind = EntryAddSeats;
        _entrySeats = "";
        _entryCost = "";
        _entryCurrency = CurrencyFormat.Php;
        _entryRate = "1";
        _entryDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _entryExpiry = "";
        _entryNotes = "";
        _entryError = null;
        _showEntitlement = true;
    }

    private void OnEntryCurrencyChanged(string? code)
    {
        _entryCurrency = code ?? CurrencyFormat.Php;
        // A foreign currency starts with an empty rate so it cannot be booked at parity by accident -
        // the same reason the receive dialog on a purchase order starts empty.
        _entryRate = _entryCurrency == CurrencyFormat.Php ? "1" : "";
    }

    private async Task SaveEntitlement()
    {
        _entryError = null;

        var seats = 0;
        if (_entryKind != EntryRenew
            && (!int.TryParse(_entrySeats, NumberStyles.Integer, CultureInfo.InvariantCulture, out seats) || seats <= 0))
        {
            _entryError = "Enter how many seats.";
            return;
        }

        var cost = string.IsNullOrWhiteSpace(_entryCost) ? 0m : ParseDecimal(_entryCost);
        if (cost is null or < 0)
        {
            _entryError = "Enter a cost of zero or more.";
            return;
        }

        decimal rate;
        if (_entryCurrency == CurrencyFormat.Php)
            rate = 1m;
        else if (ParseDecimal(_entryRate) is { } parsedRate && parsedRate > 0)
            rate = parsedRate;
        else
        {
            _entryError = $"Enter the exchange rate ({CurrencyFormat.SymbolFor(CurrencyFormat.Php)} per 1 {_entryCurrency}).";
            return;
        }

        DateTime? expiry = DateTime.TryParse(_entryExpiry, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedExpiry)
            ? parsedExpiry
            : null;

        if (_entryKind == EntryRenew && expiry is null)
        {
            _entryError = "Enter the date the renewal runs until.";
            return;
        }

        if (_entryKind == EntryReduce && string.IsNullOrWhiteSpace(_entryNotes))
        {
            _entryError = "Say why seats are being removed.";
            return;
        }

        var dto = new AddLicenceEntitlementDto
        {
            SeatsAdded = _entryKind switch { EntryAddSeats => seats, EntryReduce => -seats, _ => 0 },
            Cost = cost.Value,
            Currency = _entryCurrency,
            ExchangeRate = rate,
            EntitlementDate = DateTime.TryParse(_entryDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : DateTime.UtcNow,
            ExpiresAt = expiry,
            Notes = string.IsNullOrWhiteSpace(_entryNotes) ? null : _entryNotes.Trim()
        };

        _savingEntry = true;
        var (success, error) = await Api.AddLicenceEntitlementAsync(Id, dto);
        _savingEntry = false;

        if (!success)
        {
            _entryError = error;
            return;
        }

        _showEntitlement = false;
        Snackbar.Success("Recorded");
        await LoadAsync();
    }

    private async Task OpenAssign()
    {
        _assignTarget = "";
        _assignNotes = "";
        _assignError = null;
        _showAssign = true;

        try
        {
            if (_detail!.Licence.LicenceModel == LicenceModels.PerUser)
            {
                _users ??= await Api.GetUserListAsync() ?? [];
            }
            else if (_devices is null)
            {
                // Software rows are licences recorded the old way, and a retired or lost machine
                // cannot take a seat - the API refuses both, so neither is offered.
                var page = await Api.GetAssetsAsync(pageSize: 1000);
                _devices = page?.Items
                    .Where(a => a.DeviceType != "Software" && a.Status is not ("Retired" or "Lost"))
                    .OrderBy(a => a.AssetTag)
                    .ToList() ?? [];
            }
        }
        catch (HttpRequestException)
        {
            _assignError = "You do not have access to the list of people or devices needed to assign a seat.";
        }
    }

    private async Task AssignSeat()
    {
        _assignError = null;

        var dto = _detail!.Licence.LicenceModel == LicenceModels.PerUser
            ? new AssignLicenceSeatDto { UserId = _assignTarget }
            : new AssignLicenceSeatDto { AssetId = int.Parse(_assignTarget, CultureInfo.InvariantCulture) };

        _assigning = true;
        var (success, error) = await Api.AssignLicenceSeatAsync(Id, dto with
        {
            Notes = string.IsNullOrWhiteSpace(_assignNotes) ? null : _assignNotes.Trim()
        });
        _assigning = false;

        if (!success)
        {
            _assignError = error;
            return;
        }

        _showAssign = false;
        Snackbar.Success("Seat assigned");
        await LoadAsync();
    }

    private async Task ReleaseSeat(LicenceSeatDto seat)
    {
        _releasingSeatId = seat.Id;
        var (success, error) = await Api.ReleaseLicenceSeatAsync(Id, seat.Id);
        _releasingSeatId = null;

        if (!success)
        {
            Snackbar.Error(error ?? "Could not release the seat.");
            return;
        }

        Snackbar.Success("Seat released");
        await LoadAsync();
    }

    // Same DOM-value parsing rule as Assets/Edit.razor.ParseDecimal: a number input is always
    // invariant-formatted, so parsing it under CurrentCulture can silently misread the value.
    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded.` with `0 Error(s)`. If `Alert` rejects `Variant="warning"`, open `Components/UI/Alert.razor`, use the variant name it defines for a caution state, and note the substitution in the task report.

- [ ] **Step 3: Check every class token exists in the compiled stylesheet**

As in Task 9 Step 7, for every distinct class token in `Pages/Licences/Detail.razor`, using literal selector matches. Record the check in the task report.

- [ ] **Step 4: Commit**

```bash
git add src/AssetDesk.Web/Pages/Licences/Detail.razor
git commit -m "feat(licences): licence page with seats, entitlements and key reveal"
```

---

### Task 11: Web - receiving software, delivery history, device licences

**Files:**
- Modify: `src/AssetDesk.Web/Pages/Procurement/PurchaseOrderDetail.razor`
- Modify: `src/AssetDesk.Web/Pages/Assets/View.razor`

**Interfaces:**
- Consumes: Task 7's `ReceiveLineDto` fields and `GoodsReceiptLineDto` destination fields; Task 9's `GetLicencesAsync`, `GetDeviceLicencesAsync`, `LicenceDisplay`.

- [ ] **Step 1: Carry licence choices on each receive line**

In `src/AssetDesk.Web/Pages/Procurement/PurchaseOrderDetail.razor`, replace the `ReceiveLineFormModel` class with:

```csharp
    private sealed class ReceiveLineFormModel
    {
        public required PurchaseOrderLineDto Line { get; set; }
        public string QuantityReceived { get; set; } = "";

        public bool IsSoftware => Line.DeviceType == "Software";

        /// <summary>A licence id, NewLicenceChoice, or empty when nothing is chosen yet.</summary>
        public string LicenceChoice { get; set; } = "";
        public string Mode { get; set; } = LicenceReceiptModes.AddSeats;
        public string ExpiresAt { get; set; } = "";
        public string NewName { get; set; } = "";
        public string NewPublisher { get; set; } = "";
        public string NewModel { get; set; } = LicenceModels.PerUser;
    }
```

Add these fields beside `_receiveLines`:

```csharp
    private const string NewLicenceChoice = "new";
    private List<SoftwareLicenceDto> _licences = [];
    private bool _canSeeLicences;
    private bool _canCreateLicences;
```

Change `private void OpenReceiveModal()` to `private async Task OpenReceiveModal()`. Replace the statement that builds `_receiveLines` with:

```csharp
        _receiveLines = _order.Lines
            .Where(l => l.OutstandingQuantity > 0)
            .Select(l => new ReceiveLineFormModel
            {
                Line = l,
                QuantityReceived = l.OutstandingQuantity.ToString(),
                // The order line's description is usually the product name - a sensible start for a
                // licence created from it.
                NewName = l.Description ?? ""
            })
            .ToList();

        // Software lines go onto a licence, which needs licence access to choose. Asked only when
        // the delivery has a software line, so receiving laptops never touches the licence API.
        if (_receiveLines.Any(l => l.IsSoftware))
        {
            _canSeeLicences = await PermissionChecker.HasPermissionAsync("iams:licences:view");
            _canCreateLicences = await PermissionChecker.HasPermissionAsync("iams:licences:manage");
            _licences = _canSeeLicences
                ? (await Api.GetLicencesAsync()).Where(l => l.IsActive).ToList()
                : [];
        }
```

- [ ] **Step 2: Add the software fields to the receive dialog**

Replace the `@foreach (var line in _receiveLines)` block inside the Receive modal with:

```razor
                        @foreach (var line in _receiveLines)
                        {
                            <div class="rounded-xl border border-border p-3 space-y-3">
                                <div class="flex items-end gap-3">
                                    <div class="flex-1">
                                        <p class="text-sm font-medium text-foreground">@(line.Line.Description ?? line.Line.DeviceType)</p>
                                        <p class="text-xs text-muted-foreground">
                                            @line.Line.DeviceType - outstanding: @line.Line.OutstandingQuantity of @line.Line.Quantity
                                        </p>
                                    </div>
                                    <div class="w-24">
                                        <FormField LabelText="Qty">
                                            <Input Type="number" Value="@line.QuantityReceived" ValueChanged="v => line.QuantityReceived = v ?? string.Empty" Min="0" Max="@line.Line.OutstandingQuantity.ToString()" />
                                        </FormField>
                                    </div>
                                </div>

                                @if (line.IsSoftware)
                                {
                                    @if (!_canSeeLicences)
                                    {
                                        <p class="text-xs text-muted-foreground">
                                            Software is received onto a licence rather than as assets, which needs permission to view licences.
                                        </p>
                                    }
                                    else
                                    {
                                        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                            <FormField LabelText="Licence" Required="true">
                                                <Select Value="@line.LicenceChoice" ValueChanged="v => OnLicenceChosen(line, v)" Placeholder="Choose a licence">
                                                    @foreach (var licence in _licences)
                                                    {
                                                        <option value="@licence.Id">@licence.Name</option>
                                                    }
                                                    @if (_canCreateLicences)
                                                    {
                                                        <option value="@NewLicenceChoice">New licence…</option>
                                                    }
                                                </Select>
                                            </FormField>
                                            <FormField LabelText="This delivery">
                                                <Select Value="@line.Mode" ValueChanged="v => line.Mode = v ?? LicenceReceiptModes.AddSeats" Disabled="@(line.LicenceChoice == NewLicenceChoice)">
                                                    <option value="@LicenceReceiptModes.AddSeats">Adds seats</option>
                                                    <option value="@LicenceReceiptModes.Renew">Renews existing seats</option>
                                                </Select>
                                            </FormField>
                                        </div>

                                        @if (line.LicenceChoice == NewLicenceChoice)
                                        {
                                            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                                <FormField LabelText="New licence name" Required="true">
                                                    <Input Value="@line.NewName" ValueChanged="v => line.NewName = v" MaxLength="200" />
                                                </FormField>
                                                <FormField LabelText="Publisher">
                                                    <Input Value="@line.NewPublisher" ValueChanged="v => line.NewPublisher = v" MaxLength="200" />
                                                </FormField>
                                                <FormField LabelText="Counted">
                                                    <Select Value="@line.NewModel" ValueChanged="v => line.NewModel = v ?? LicenceModels.PerUser">
                                                        @foreach (var model in LicenceModels.All)
                                                        {
                                                            <option value="@model">@LicenceModels.Label(model)</option>
                                                        }
                                                    </Select>
                                                </FormField>
                                            </div>
                                        }

                                        <FormField LabelText="@(line.Mode == LicenceReceiptModes.Renew ? "Renewed until" : "Expires (optional)")"
                                                   Required="@(line.Mode == LicenceReceiptModes.Renew)">
                                            <Input Type="date" Value="@line.ExpiresAt" ValueChanged="v => line.ExpiresAt = v" />
                                        </FormField>
                                    }
                                }
                            </div>
                        }
```

Add to the `@code` block:

```csharp
    private static void OnLicenceChosen(ReceiveLineFormModel line, string? choice)
    {
        line.LicenceChoice = choice ?? "";
        // A renewal needs a licence that already exists; the API refuses the combination, so the
        // dialog does not offer it.
        if (line.LicenceChoice == NewLicenceChoice)
            line.Mode = LicenceReceiptModes.AddSeats;
    }
```

- [ ] **Step 3: Send the licence choices**

In `ReceiveGoods()`, replace the body of the `foreach (var line in _receiveLines)` loop with:

```csharp
            if (!int.TryParse(line.QuantityReceived, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                continue;

            var receiveLine = new ReceiveLineDto { PurchaseOrderLineId = line.Line.Id, QuantityReceived = quantity };

            if (line.IsSoftware)
            {
                var label = line.Line.Description ?? line.Line.DeviceType;

                if (!_canSeeLicences)
                {
                    _receiveError = $"{label}: receiving software needs permission to view licences.";
                    return;
                }

                if (string.IsNullOrEmpty(line.LicenceChoice))
                {
                    _receiveError = $"{label}: choose the licence these seats belong to.";
                    return;
                }

                var isNew = line.LicenceChoice == NewLicenceChoice;
                var renewing = !isNew && line.Mode == LicenceReceiptModes.Renew;
                DateTime? expiresAt = DateTime.TryParse(line.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedExpiry)
                    ? parsedExpiry
                    : null;

                if (renewing && expiresAt is null)
                {
                    _receiveError = $"{label}: enter the date the renewal runs until.";
                    return;
                }

                if (isNew && string.IsNullOrWhiteSpace(line.NewName))
                {
                    _receiveError = $"{label}: name the new licence.";
                    return;
                }

                receiveLine = receiveLine with
                {
                    SoftwareLicenceId = isNew ? null : int.Parse(line.LicenceChoice, CultureInfo.InvariantCulture),
                    NewLicence = isNew
                        ? new NewLicenceInputDto
                        {
                            Name = line.NewName.Trim(),
                            Publisher = string.IsNullOrWhiteSpace(line.NewPublisher) ? null : line.NewPublisher.Trim(),
                            LicenceModel = line.NewModel
                        }
                        : null,
                    LicenceMode = renewing ? LicenceReceiptModes.Renew : LicenceReceiptModes.AddSeats,
                    LicenceExpiresAt = expiresAt
                };
            }

            lines.Add(receiveLine);
```

- [ ] **Step 4: Name where software went in the delivery history**

Replace the paragraph in the Deliveries card that renders `@string.Join(", ", receipt.Lines.Select(l => ...))` - keep its explanatory comment - with:

```razor
                                        <p class="text-xs text-muted-foreground mt-1">
                                            @* The description, where there is one, because two lines of the same device
                                               type - two laptop models - would otherwise read identically here. A software
                                               line names the licence it went to, since it produced no assets to find. *@
                                            @string.Join(", ", receipt.Lines.Select(LineSummary))
                                        </p>
```

Add to the `@code` block:

```csharp
    private static string LineSummary(GoodsReceiptLineDto line)
    {
        var what = $"{line.QuantityReceived} × {(string.IsNullOrWhiteSpace(line.Description) ? line.DeviceType : line.Description)}";
        return line switch
        {
            { RenewedTo: { } until } => $"{what} → renewed to {until:MMM dd, yyyy}",
            { LicenceName: { } name } => $"{what} → added to {name}",
            _ => what
        };
    }
```

- [ ] **Step 5: Show a device's licences on the asset page**

In `src/AssetDesk.Web/Pages/Assets/View.razor`, immediately after the closing `}` of the `@if (!string.IsNullOrEmpty(_asset.PurchaseOrderReference))` Procurement card block, add:

```razor

                @if (_deviceLicences.Count > 0)
                {
                    <Card>
                        <CardHeader>
                            <CardTitle>Licences on this device</CardTitle>
                        </CardHeader>
                        <CardContent>
                            <div class="space-y-2">
                                @foreach (var licence in _deviceLicences)
                                {
                                    <div class="flex items-center justify-between gap-3 text-sm" @key="licence.LicenceId">
                                        <a href="/licences/@licence.LicenceId" class="font-medium text-foreground hover:underline">@licence.Name</a>
                                        <Badge Variant="@LicenceDisplay.RenewalVariant(licence.RenewalStatus)">@licence.RenewalStatus</Badge>
                                    </div>
                                }
                            </div>
                        </CardContent>
                    </Card>
                }
```

In the `@code` block, add a field beside `_depreciation`:

```csharp
    private List<DeviceLicenceDto> _deviceLicences = [];
```

In `OnInitializedAsync`, immediately after the `if (await PermissionChecker.HasPermissionAsync("iams:reports:view")) _ = LoadDepreciationAsync();` statement, add:

```csharp

        // Gated for the same reason as the depreciation load above: Auditor-shaped roles can open an
        // asset without licence access, and should make no request rather than a guaranteed 403.
        if (await PermissionChecker.HasPermissionAsync("iams:licences:view"))
            _ = LoadDeviceLicencesAsync();
```

and after `LoadDepreciationAsync`:

```csharp
    private async Task LoadDeviceLicencesAsync()
    {
        _deviceLicences = await Api.GetDeviceLicencesAsync(Id);
        StateHasChanged();
    }
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 7: Check every class token exists in the compiled stylesheet**

As in Task 9 Step 7, for every class token added or changed in both files. Record the check in the task report.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests`
Expected: 524 passed, 0 failed - the Web project has no tests, so this confirms nothing in the shared DTOs shifted under the API.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Web/Pages/Procurement/PurchaseOrderDetail.razor src/AssetDesk.Web/Pages/Assets/View.razor
git commit -m "feat(licences): receive software onto licences and show device licences"
```

---

## Before merge

Not tasks for an implementer - these run once every task is reviewed, against a throwaway local PostgreSQL database (never the remote development database):

1. **Migrations** apply cleanly; `\d "LicenceSeatAssignments"` shows the `CK_LicenceSeatAssignments_ExactlyOneTarget` check and both partial unique indexes with their `WHERE` clauses.
2. **Permission backfill**: on a database that already holds a tenant created *before* `GrantLicencePermissions` runs, the Admin, Staff and Auditor rows gain exactly the keys tabled in Global Constraints, and running the migration twice inserts nothing new.
3. **Receiving**: a purchase order with a laptop line and a software line, received in one delivery at a foreign rate, then a renewal against the same licence - check assets, entitlements, seats owned and expiry in the database.
4. **Constraints on Npgsql**: assign a person twice to one licence through the API and confirm the friendly refusal, then insert the same row directly with `psql` and confirm the index refuses it.
5. **Key reveal**: reveal a key through the UI and confirm the `AuditLogs` row; confirm no other response in the browser's network log carries the key.
6. **Pages**: `/licences`, `/licences/{id}`, the receive dialog with a software line, the asset page's device licences, and the compliance CSV and PDF.
