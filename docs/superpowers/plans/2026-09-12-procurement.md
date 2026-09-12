# Procurement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record what was ordered, from whom, and what actually arrived — and have the assets that arrived create themselves, already priced, dated and traceable to the order.

**Architecture:** Five new tenant-scoped entities (`Supplier`, `PurchaseOrder`, `PurchaseOrderLine`, `GoodsReceipt`, `GoodsReceiptLine`) plus one nullable link on `Asset`. Receiving is a single transaction inside an EF execution strategy that creates the receipt, creates one `Asset` per unit, advances each line's received quantity, and recomputes the order's status. The exchange rate lives on the receipt, not the order, so two deliveries against one USD order carry the rates actually booked.

**Tech Stack:** .NET 10, ASP.NET Core Web API, Blazor WebAssembly, EF Core + Npgsql, QuestPDF, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-12-procurement-design.md`

## Global Constraints

- Build: `dotnet build`. Tests: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`. **Baseline is 323 passing, 0 failing.** Every task ends at 323 + the tests it added. **Measure the baseline yourself before each task rather than trusting a number** — earlier features drifted when fix waves added tests beyond the plan's projection.
- **Stop any running API before `dotnet build`.** A running server holds a lock on the output DLLs and the build fails with MSB3027 "file is locked by AssetDesk.Api", which reads like a code error and is not.
- **Receiving is one transaction, inside `_db.Database.CreateExecutionStrategy()`.** Production is Npgsql with `EnableRetryOnFailure` and EF Core refuses a user-initiated transaction under a retrying strategy. The delegate can run more than once, so it must `_db.ChangeTracker.Clear()` on entry and re-read every entity. Any side effect that must happen once (a notification, an email) goes **outside** the delegate — `FulfilAsync` documents exactly this.
- **The over-receipt guard is a conditional claim inside the transaction, not a pre-check.** A friendly pre-check is good UX and is not the invariant. Two concurrent receipts must not both claim the last unit.
- **The exchange rate comes from the receipt, never from the purchase order.** Assets created by a receipt take the PO's `Currency` and the receipt's `ExchangeRate`.
- **`PartiallyReceived` and `Received` are set by the receive operation, never by hand.** A status a user could set independently of the quantities would immediately disagree with them.
- **Tenant isolation must be explicit in every controller — do not rely on the global query filter.** That filter has an `IsSuperAdmin()` bypass (`AppDbContext.cs:544-547`). The depreciation feature shipped a Critical here: its controller leaned on the filter and every test used a single tenant, so a SuperAdmin could silently rewrite another organisation's rows. Every controller in this plan resolves the tenant explicitly and filters on it, and every controller gets a super-admin isolation test.
- **Do not write a peso sign** as a literal `₱` or the escape `₱`. Use `AssetDesk.Shared.CurrencyFormat`. After every task `grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src` must return only `CurrencyFormat.cs` and `EstateDashboard.razor`.
- There is **no test project for the Blazor assembly**. Tasks 10 and 11 are verified by `dotnet build` and by running the app. Never claim test coverage for Razor changes.
- Any **new** Tailwind class requires `npm --prefix src/AssetDesk.Web run build:css` plus a bump to the `?v=` in `wwwroot/index.html` and `cacheName` in `service-worker.published.js`. `wwwroot/css/app.css` is a committed artifact `dotnet build` does not regenerate.
- Tests run on in-memory SQLite via `TestDb.Create()`. A green suite does **not** prove a migration applies on PostgreSQL, and does **not** prove transactional behaviour under a real provider. Read generated SQL by hand.
- Commit messages follow the repo's conventional-commit style and end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`

## File Structure

**Create:**
- `src/AssetDesk.Api/Services/AssetTagGenerator.cs` — the one tag generator
- `src/AssetDesk.Api/Entities/Supplier.cs`
- `src/AssetDesk.Api/Entities/PurchaseOrder.cs` — order, line, status constants, workflow table
- `src/AssetDesk.Api/Entities/GoodsReceipt.cs` — receipt and receipt line
- `src/AssetDesk.Api/Services/PurchaseOrderNumberAllocator.cs`
- `src/AssetDesk.Api/Services/GoodsReceiptService.cs` — the transactional receive
- `src/AssetDesk.Api/Controllers/SuppliersController.cs`
- `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`
- `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`
- `src/AssetDesk.Web/Pages/Procurement/Suppliers.razor`
- `src/AssetDesk.Web/Pages/Procurement/PurchaseOrders.razor`
- `src/AssetDesk.Web/Pages/Procurement/PurchaseOrderDetail.razor`
- Migrations: `<ts>_AddSuppliers`, `<ts>_AddPurchaseOrders`, `<ts>_AddGoodsReceipts`, `<ts>_GrantProcurementPermissions`
- Tests: `AssetTagGeneratorTests.cs`, `SupplierApiTests.cs`, `PurchaseOrderApiTests.cs`, `PurchaseOrderWorkflowTests.cs`, `GoodsReceiptTests.cs`

**Modify:**
- `src/AssetDesk.Api/Controllers/AssetsController.cs` — use the shared generator
- `src/AssetDesk.Api/Services/AssetImportService.cs` — use the shared generator
- `src/AssetDesk.Api/Entities/Asset.cs` — `GoodsReceiptLineId`
- `src/AssetDesk.Api/Data/AppDbContext.cs` — five DbSets, config, query filters
- `src/AssetDesk.Api/Authorization/Permissions.cs` — two keys
- `src/AssetDesk.Api/Program.cs` — two policies, service registrations
- `src/AssetDesk.Api/Services/PdfReportService.cs` — the PO PDF
- `src/AssetDesk.Web/Services/ApiClient.cs`, `Layout/MainLayout.razor`, `Pages/Assets/View.razor`

---

### Task 1: One asset tag generator

**Files:**
- Create: `src/AssetDesk.Api/Services/AssetTagGenerator.cs`, `tests/AssetDesk.Api.Tests/AssetTagGeneratorTests.cs`
- Modify: `src/AssetDesk.Api/Controllers/AssetsController.cs:480-516`, `src/AssetDesk.Api/Services/AssetImportService.cs:155,179-220`

**Interfaces:**
- Produces: `IAssetTagGenerator` with `Task<string> NextAsync(string deviceType, Dictionary<string, int> sequenceCache, CancellationToken ct = default)` and `static string PrefixFor(string deviceType)`. Task 8 calls `NextAsync` once per unit received, passing one cache across the whole receipt.

This is a **behaviour-preserving refactor**. Two copies exist today — `AssetsController.cs:480` and `AssetImportService.cs:179` — identical through the prefix switch and `baseTag`, differing only in that the importer caches a sequence per `baseTag` so a bulk import does not re-query per row. Receiving would be the third copy.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/AssetTagGeneratorTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Tag generation existed twice before this class - once in AssetsController and once in
/// AssetImportService, identical except for the importer's sequence cache. These pin the
/// behaviour both had, so the extraction can be shown to preserve it.
/// </summary>
public class AssetTagGeneratorTests
{
    [Theory]
    [InlineData("Laptop", "LAP")]
    [InlineData("Desktop", "DSK")]
    [InlineData("Monitor", "MON")]
    [InlineData("Phone", "PHN")]
    [InlineData("Tablet", "TAB")]
    [InlineData("Printer", "PRN")]
    [InlineData("Network", "NET")]
    [InlineData("Server", "SVR")]
    [InlineData("Peripheral", "PER")]
    [InlineData("Software", "SFT")]
    [InlineData("Other", "OTH")]
    [InlineData("Drone", "OTH")]
    public void Each_device_type_maps_to_its_prefix(string deviceType, string expected)
    {
        Assert.Equal(expected, AssetTagGenerator.PrefixFor(deviceType));
    }

    [Fact]
    public async Task The_first_tag_of_the_day_is_sequence_one()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);

            var tag = await generator.NextAsync(DeviceTypes.Laptop, new Dictionary<string, int>());

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", tag);
        }
    }

    [Fact]
    public async Task Successive_calls_sharing_a_cache_do_not_collide()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);
            var cache = new Dictionary<string, int>();

            var first = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var second = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var third = await generator.NextAsync(DeviceTypes.Laptop, cache);

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", first);
            Assert.Equal($"LAP-{today}-0002", second);
            Assert.Equal($"LAP-{today}-0003", third);
        }
    }

    [Fact]
    public async Task The_sequence_continues_past_tags_already_in_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            await TestDb.SeedAssetAsync(db, tenantId, $"LAP-{today}-0007");

            var generator = new AssetTagGenerator(db);
            var tag = await generator.NextAsync(DeviceTypes.Laptop, new Dictionary<string, int>());

            Assert.Equal($"LAP-{today}-0008", tag);
        }
    }

    [Fact]
    public async Task Different_device_types_have_independent_sequences()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);
            var cache = new Dictionary<string, int>();

            var laptop = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var monitor = await generator.NextAsync(DeviceTypes.Monitor, cache);

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", laptop);
            Assert.Equal($"MON-{today}-0001", monitor);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~AssetTagGeneratorTests"`

Expected: FAIL to compile — `The type or namespace name 'AssetTagGenerator' could not be found`.

- [ ] **Step 3: Write the generator**

Create `src/AssetDesk.Api/Services/AssetTagGenerator.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IAssetTagGenerator
{
    /// <param name="sequenceCache">
    /// Carried across a batch so creating many assets at once does not re-query per row. Pass a
    /// fresh dictionary for a single asset; pass one dictionary for a whole import or receipt.
    /// </param>
    Task<string> NextAsync(string deviceType, Dictionary<string, int> sequenceCache, CancellationToken ct = default);
}

/// <summary>
/// The one place asset tags are made. Format is PREFIX-yyyyMMdd-NNNN, e.g. LAP-20251218-0001.
///
/// This existed twice before - AssetsController and AssetImportService each had a private copy,
/// identical apart from the importer's sequence cache. Goods receipt would have been the third,
/// and the multi-currency work already showed what a fourth copy costs: the same aggregation
/// shipped in four places, two were missed, and the final review caught it as a Critical.
/// </summary>
public class AssetTagGenerator(AppDbContext db) : IAssetTagGenerator
{
    public static string PrefixFor(string deviceType) => deviceType switch
    {
        DeviceTypes.Laptop => "LAP",
        DeviceTypes.Desktop => "DSK",
        DeviceTypes.Monitor => "MON",
        DeviceTypes.Phone => "PHN",
        DeviceTypes.Tablet => "TAB",
        DeviceTypes.Printer => "PRN",
        DeviceTypes.Network => "NET",
        DeviceTypes.Server => "SVR",
        DeviceTypes.Peripheral => "PER",
        DeviceTypes.Software => "SFT",
        _ => "OTH"
    };

    public async Task<string> NextAsync(
        string deviceType, Dictionary<string, int> sequenceCache, CancellationToken ct = default)
    {
        var baseTag = $"{PrefixFor(deviceType)}-{DateTime.UtcNow:yyyyMMdd}-";

        if (!sequenceCache.TryGetValue(baseTag, out var current))
        {
            var todayTags = await db.Assets
                .Where(a => a.AssetTag.StartsWith(baseTag))
                .Select(a => a.AssetTag)
                .ToListAsync(ct);

            current = 0;
            foreach (var tag in todayTags)
            {
                if (int.TryParse(tag.Replace(baseTag, ""), out var seq) && seq > current)
                    current = seq;
            }
        }

        current++;
        sequenceCache[baseTag] = current;
        return $"{baseTag}{current:D4}";
    }
}
```

- [ ] **Step 4: Register it**

In `src/AssetDesk.Api/Program.cs`, beside the other scoped service registrations:

```csharp
builder.Services.AddScoped<IAssetTagGenerator, AssetTagGenerator>();
```

- [ ] **Step 5: Move both existing callers onto it**

In `src/AssetDesk.Api/Controllers/AssetsController.cs`: inject `IAssetTagGenerator tags` into the primary constructor alongside the existing parameters, replace the call at line 130 with

```csharp
        var assetTag = await tags.NextAsync(dto.DeviceType, new Dictionary<string, int>());
```

and **delete** the private `GenerateAssetTagAsync` (lines 480-516 inclusive).

In `src/AssetDesk.Api/Services/AssetImportService.cs`: inject `IAssetTagGenerator tags` into the primary constructor, replace the call at line 155 with

```csharp
        var assetTag = await tags.NextAsync(deviceType, tagSequenceCache, ct);
```

and **delete** the private `GenerateAssetTagAsync` (lines 179-220 inclusive). The local `tagSequenceCache` variable stays — it is now passed in rather than consumed internally.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 339 passing, 0 failing (323 baseline + 16 — the 12-case theory plus 4 facts). The pre-existing import and asset-creation tests must pass **unchanged**; they are what proves the refactor preserved behaviour. If any fails, the extraction changed something — fix the extraction, not the test.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Services/AssetTagGenerator.cs src/AssetDesk.Api/Controllers/AssetsController.cs src/AssetDesk.Api/Services/AssetImportService.cs src/AssetDesk.Api/Program.cs tests/AssetDesk.Api.Tests/AssetTagGeneratorTests.cs
git commit -m "refactor(assets): one asset tag generator instead of two

AssetsController and AssetImportService each carried a private copy, identical
through the prefix switch and the base tag, differing only in the importer's
sequence cache. Goods receipt would have been the third.

The multi-currency work already showed the cost of that pattern: the same
aggregation in four places, two missed, caught as a Critical by the final
review. Extracting now, before the third caller exists, is cheaper than
extracting after.

Behaviour-preserving - the pre-existing import and asset-creation tests pass
unchanged, which is the proof.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Suppliers

**Files:**
- Create: `src/AssetDesk.Api/Entities/Supplier.cs`, `src/AssetDesk.Api/Controllers/SuppliersController.cs`, `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`, `tests/AssetDesk.Api.Tests/SupplierApiTests.cs`
- Modify: `src/AssetDesk.Api/Data/AppDbContext.cs`

**Interfaces:**
- Produces: `Supplier` entity; `SupplierDto` (`Id`, `Name`, `ContactName`, `Email`, `Phone`, `Address`, `Notes`, `IsActive`); `UpsertSupplierDto` (same minus `Id`); `GET/POST /api/suppliers`, `PUT/DELETE /api/suppliers/{id}`. Task 3's `PurchaseOrder` holds a `SupplierId`; Task 11 renders the list.

Gate the controller on the permissions from Task 5 **once Task 5 exists** — until then use `[Authorize]` alone and revisit. (Task 5 is ordered after this deliberately: suppliers are useful to have working before the permission catalogue changes.)

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/SupplierApiTests.cs`:

```csharp
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class SupplierApiTests
{
    private static SuppliersController ControllerFor(
        AssetDesk.Api.Data.AppDbContext db, ITenantProvider tenants) => new(db, tenants);

    private static UpsertSupplierDto Dto(string name) => new()
    {
        Name = name,
        ContactName = "Maria Santos",
        Email = "maria@example.com",
        Phone = "+63 2 8888 0000"
    };

    [Fact]
    public async Task A_supplier_is_created_against_the_callers_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(Dto("Acme Computers"));

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.Suppliers.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal("Acme Computers", saved.Name);
            Assert.True(saved.IsActive);
        }
    }

    [Fact]
    public async Task A_duplicate_name_within_a_tenant_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(Dto("Acme Computers"));

            var result = await controller.Create(Dto("Acme Computers"));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(1, await db.Suppliers.CountAsync());
        }
    }

    [Fact]
    public async Task Two_tenants_may_each_have_a_supplier_of_the_same_name()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            await ControllerFor(db, new FakeTenantProvider(tenantA)).Create(Dto("Acme Computers"));
            await ControllerFor(db, new FakeTenantProvider(tenantB)).Create(Dto("Acme Computers"));

            Assert.Equal(2, await db.Suppliers.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_edit_another_tenants_supplier()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            var bSupplier = new Supplier { TenantId = tenantB, Name = "Acme Computers" };
            db.Suppliers.Add(bSupplier);
            await db.SaveChangesAsync();

            // A super admin has no tenant of their own; the controller must refuse rather than
            // reach into whichever tenant's row happens to match.
            var result = await ControllerFor(db, new FakeTenantProvider(null, isSuperAdmin: true))
                .Update(bSupplier.Id, Dto("Renamed By Mistake"));

            Assert.IsNotType<OkObjectResult>(result.Result);
            var reloaded = await db.Suppliers.IgnoreQueryFilters().SingleAsync(s => s.Id == bSupplier.Id);
            Assert.Equal("Acme Computers", reloaded.Name);
        }
    }

    [Fact]
    public async Task Deactivating_a_supplier_keeps_the_row()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(Dto("Acme Computers"));
            var supplier = await db.Suppliers.SingleAsync();

            await controller.Delete(supplier.Id);

            // Purchase orders reference suppliers, so a supplier is deactivated rather than
            // deleted - the same reasoning LookupValue documents for its rows.
            var reloaded = await db.Suppliers.SingleAsync();
            Assert.False(reloaded.IsActive);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~SupplierApiTests"`

Expected: FAIL to compile — `The type or namespace name 'SuppliersController' could not be found`.

- [ ] **Step 3: Create the entity**

Create `src/AssetDesk.Api/Entities/Supplier.cs`:

```csharp
namespace AssetDesk.Api.Entities;

/// <summary>
/// Someone the organisation buys from. Deactivated rather than deleted once purchase orders
/// reference it - the same reasoning LookupValue documents for its own rows.
/// </summary>
public class Supplier : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Configure it**

In `src/AssetDesk.Api/Data/AppDbContext.cs`, add the DbSet beside the others:

```csharp
    public DbSet<Supplier> Suppliers => Set<Supplier>();
```

and a configuration block alongside the other `modelBuilder.Entity<...>` blocks:

```csharp
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ContactName).HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Address).HasMaxLength(500);

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

`OnDelete(DeleteBehavior.Restrict)` matches the eleven other tenant foreign keys in this file. Only `RolePermission` cascades.

- [ ] **Step 5: Create the DTOs**

Create `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record SupplierDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
}

public record UpsertSupplierDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)] public string? ContactName { get; init; }
    [EmailAddress, StringLength(256)] public string? Email { get; init; }
    [StringLength(50)] public string? Phone { get; init; }
    [StringLength(500)] public string? Address { get; init; }
    public string? Notes { get; init; }
}
```

- [ ] **Step 6: Create the controller**

Create `src/AssetDesk.Api/Controllers/SuppliersController.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class SuppliersController(AppDbContext db, ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>
    /// Every query filters on the tenant explicitly rather than trusting the global query
    /// filter, which has an IsSuperAdmin() bypass. The depreciation feature shipped a Critical
    /// for exactly this: a super-admin caller could rewrite another organisation's rows because
    /// the lookup key was not unique across tenants.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<SupplierDto>>>> GetAll()
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<SupplierDto>>.Fail("Select an organisation first."));

        var suppliers = await db.Suppliers
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .Select(s => Map(s))
            .ToListAsync();

        return Ok(ApiResponse<List<SupplierDto>>.Ok(suppliers));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SupplierDto>>> Create(UpsertSupplierDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SupplierDto>.Fail("Select an organisation first."));

        if (await db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.Name == dto.Name))
            return BadRequest(ApiResponse<SupplierDto>.Fail($"A supplier named '{dto.Name}' already exists."));

        var supplier = new Supplier
        {
            TenantId = tenantId,
            Name = dto.Name,
            ContactName = dto.ContactName,
            Email = dto.Email,
            Phone = dto.Phone,
            Address = dto.Address,
            Notes = dto.Notes
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), ApiResponse<SupplierDto>.Ok(Map(supplier)));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<SupplierDto>>> Update(int id, UpsertSupplierDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SupplierDto>.Fail("Select an organisation first."));

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (supplier is null)
            return NotFound(ApiResponse<SupplierDto>.Fail("Supplier not found."));

        if (await db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.Name == dto.Name && s.Id != id))
            return BadRequest(ApiResponse<SupplierDto>.Fail($"A supplier named '{dto.Name}' already exists."));

        supplier.Name = dto.Name;
        supplier.ContactName = dto.ContactName;
        supplier.Email = dto.Email;
        supplier.Phone = dto.Phone;
        supplier.Address = dto.Address;
        supplier.Notes = dto.Notes;
        supplier.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<SupplierDto>.Ok(Map(supplier)));
    }

    /// <summary>Deactivates rather than deletes - purchase orders reference suppliers.</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (supplier is null)
            return NotFound(ApiResponse<object>.Fail("Supplier not found."));

        supplier.IsActive = false;
        supplier.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new object()));
    }

    private static SupplierDto Map(Supplier s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        ContactName = s.ContactName,
        Email = s.Email,
        Phone = s.Phone,
        Address = s.Address,
        Notes = s.Notes,
        IsActive = s.IsActive
    };
}
```

If `ApiResponse<T>.Ok`/`.Fail` differ from this in the codebase, match `AssetsController` — read it first.

- [ ] **Step 7: Generate the migration and check its SQL**

```bash
cd src/AssetDesk.Api && dotnet ef migrations add AddSuppliers
dotnet ef migrations script --idempotent --no-build | grep -A 14 'CREATE TABLE "Suppliers"'
```

Expected: `"Name" character varying(200) NOT NULL`, a unique index over `("TenantId","Name")`, and the FK to `Tenants` with `ON DELETE RESTRICT`. The SQLite suite cannot prove this.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 344 passing, 0 failing.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Entities/Supplier.cs src/AssetDesk.Api/Controllers/SuppliersController.cs src/AssetDesk.Shared/DTOs/ProcurementDto.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/SupplierApiTests.cs
git commit -m "feat(procurement): the supplier register

Every query filters on the tenant explicitly rather than trusting the global
query filter, which has an IsSuperAdmin() bypass. The depreciation feature
shipped a Critical for exactly that: its controller leaned on the filter, every
test used a single tenant, and a super admin could silently rewrite another
organisation's rows. The super-admin isolation test here is the guard.

Suppliers deactivate rather than delete, because purchase orders reference
them - the same reasoning LookupValue documents for its rows.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Purchase orders and their lines

**Files:**
- Create: `src/AssetDesk.Api/Entities/PurchaseOrder.cs`, `src/AssetDesk.Api/Services/PurchaseOrderNumberAllocator.cs`
- Modify: `src/AssetDesk.Api/Data/AppDbContext.cs`, `src/AssetDesk.Api/Program.cs`
- Test: `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`

**Interfaces:**
- Consumes: `Supplier` (Task 2).
- Produces: `PurchaseOrder` (`PoNumber`, `SupplierId`, `Currency`, `Status`, `OrderDate`, `ExpectedDate`, `Notes`, `CreatedByUserId`), `PurchaseOrderLine` (`PurchaseOrderId`, `DeviceType`, `Description`, `Quantity`, `UnitPrice`, `ReceivedQuantity`), `PurchaseOrderStatus` constants, `IPurchaseOrderNumberAllocator.NextAsync(Guid tenantId, CancellationToken ct = default)`. Tasks 6, 8 and 9 all consume these.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class PurchaseOrderApiTests
{
    [Fact]
    public async Task A_new_order_starts_as_a_draft_with_nothing_received()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                TenantId = tenantId,
                PoNumber = 1,
                SupplierId = supplier.Id,
                Currency = Currencies.PHP,
                Status = PurchaseOrderStatus.Draft,
                OrderDate = new DateTime(2026, 9, 12),
                CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Laptop,
                Description = "Dell Latitude 5540",
                Quantity = 10,
                UnitPrice = 50000m
            });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var saved = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
            var line = Assert.Single(saved.Lines);
            Assert.Equal(10, line.Quantity);
            Assert.Equal(0, line.ReceivedQuantity);
        }
    }

    [Fact]
    public async Task Po_numbers_are_sequential_within_a_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var allocator = new PurchaseOrderNumberAllocator(db);

            Assert.Equal(1, await allocator.NextAsync(tenantId));

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Draft,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();

            Assert.Equal(2, await allocator.NextAsync(tenantId));
        }
    }

    [Fact]
    public async Task Po_numbers_restart_per_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var supplierA = new Supplier { TenantId = tenantA, Name = "Acme" };
            db.Suppliers.Add(supplierA);
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantA, PoNumber = 7, SupplierId = supplierA.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Draft,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();

            var allocator = new PurchaseOrderNumberAllocator(db);

            Assert.Equal(8, await allocator.NextAsync(tenantA));
            Assert.Equal(1, await allocator.NextAsync(tenantB));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~PurchaseOrderApiTests"`

Expected: FAIL to compile — `The type or namespace name 'PurchaseOrder' could not be found`.

- [ ] **Step 3: Create the entities and status constants**

Create `src/AssetDesk.Api/Entities/PurchaseOrder.cs`:

```csharp
namespace AssetDesk.Api.Entities;

public class PurchaseOrder : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Per-tenant display number, rendered PO-0042. Distinct from Id, which is global.</summary>
    public int PoNumber { get; set; }

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>The currency the order is placed in. The rate lives on each receipt, not here.</summary>
    public string Currency { get; set; } = Currencies.PHP;

    public string Status { get; set; } = PurchaseOrderStatus.Draft;

    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public DateTime? ExpectedDate { get; set; }
    public string? Notes { get; set; }

    public required string CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public int Id { get; set; }

    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public required string DeviceType { get; set; }
    public string? Description { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Running total maintained by the receive operation, not derived by summing receipt lines.
    /// It is what the over-receipt guard reads inside the transaction, and a derived sum would
    /// have to be recomputed under the same lock to be safe.
    /// </summary>
    public int ReceivedQuantity { get; set; }

    public int OutstandingQuantity => Quantity - ReceivedQuantity;
}

public static class PurchaseOrderStatus
{
    public const string Draft = "Draft";
    public const string Ordered = "Ordered";
    public const string PartiallyReceived = "PartiallyReceived";
    public const string Received = "Received";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All = [Draft, Ordered, PartiallyReceived, Received, Cancelled];

    public static bool IsValid(string status) => All.Contains(status);
}
```

`GoodsReceipt` does not exist until Task 7, so **omit the `Receipts` collection entirely here** — Task 7 adds it. Do not write it and comment it out, and do not create a stub entity: commented-out code in a commit is a defect a reviewer will rightly flag, and an omitted navigation property costs nothing to add later.

- [ ] **Step 4: Create the number allocator**

Create `src/AssetDesk.Api/Services/PurchaseOrderNumberAllocator.cs`:

```csharp
using AssetDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IPurchaseOrderNumberAllocator
{
    Task<int> NextAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Mirrors TicketNumberAllocator exactly, including its race: max + 1 is a read-then-write, so
/// two concurrent creations can pick the same number. The unique index on (TenantId, PoNumber)
/// is what actually holds - it turns a lost race into a failed insert rather than a duplicate
/// number, the same way (TenantId, TicketNumber) does for tickets.
///
/// IgnoreQueryFilters plus an explicit tenant filter, because the caller may be a super admin
/// whose filter would otherwise admit every tenant's rows and return the global maximum.
/// </summary>
public class PurchaseOrderNumberAllocator(AppDbContext db) : IPurchaseOrderNumberAllocator
{
    public async Task<int> NextAsync(Guid tenantId, CancellationToken ct = default)
    {
        var highest = await db.PurchaseOrders
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId)
            .MaxAsync(p => (int?)p.PoNumber, ct);

        return (highest ?? 0) + 1;
    }
}
```

- [ ] **Step 5: Configure the entities**

In `src/AssetDesk.Api/Data/AppDbContext.cs`:

```csharp
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
```

and:

```csharp
        modelBuilder.Entity<PurchaseOrder>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.PoNumber }).IsUnique();

            entity.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue(Currencies.PHP);
            entity.Property(e => e.Status).HasMaxLength(30).IsRequired();

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

        modelBuilder.Entity<PurchaseOrderLine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);

            entity.Ignore(e => e.OutstandingQuantity);

            entity.HasOne(e => e.PurchaseOrder)
                .WithMany(p => p.Lines)
                .HasForeignKey(e => e.PurchaseOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

`PurchaseOrderLine` cascades from its order deliberately — a line has no meaning without it, unlike the tenant relationships. It carries no `TenantId` and no query filter of its own: it is only ever reached through its order, which is filtered. `UnitPrice` uses `HasPrecision(18, 2)`, matching `Asset.PurchasePrice`.

- [ ] **Step 6: Register the allocator**

In `src/AssetDesk.Api/Program.cs`:

```csharp
builder.Services.AddScoped<IPurchaseOrderNumberAllocator, PurchaseOrderNumberAllocator>();
```

- [ ] **Step 7: Generate the migration and check its SQL**

```bash
cd src/AssetDesk.Api && dotnet ef migrations add AddPurchaseOrders
dotnet ef migrations script --idempotent --no-build | grep -A 16 'CREATE TABLE "PurchaseOrders"'
```

Expected: a unique index over `("TenantId","PoNumber")`, `"UnitPrice" numeric(18,2)` on the lines table, and the lines FK cascading from `PurchaseOrders` while the tenant FK restricts.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 347 passing, 0 failing.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Entities/PurchaseOrder.cs src/AssetDesk.Api/Services/PurchaseOrderNumberAllocator.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Program.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs
git commit -m "feat(procurement): purchase orders and their lines

ReceivedQuantity is a running total on the line rather than a sum over receipts.
It is what the over-receipt guard reads inside the receive transaction, and a
derived sum would have to be recomputed under the same lock to be safe.

The number allocator mirrors TicketNumberAllocator, race included: max + 1 is a
read-then-write, and the unique index on (TenantId, PoNumber) is what actually
holds - a lost race becomes a failed insert rather than a duplicate number.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: The status transition table

**Files:**
- Modify: `src/AssetDesk.Api/Entities/PurchaseOrder.cs`
- Create: `tests/AssetDesk.Api.Tests/PurchaseOrderWorkflowTests.cs`

**Interfaces:**
- Produces: `PurchaseOrderWorkflow.CanTransition(string from, string to) -> bool`, `PurchaseOrderWorkflow.IsOpen(string status) -> bool`, `PurchaseOrderWorkflow.StatusFor(int totalOrdered, int totalReceived, string currentStatus) -> string`. Tasks 6 and 8 both consume these.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/PurchaseOrderWorkflowTests.cs`:

```csharp
using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The single source of truth for how an order may move. Shaped after TicketWorkflow, which is
/// the house pattern for this - a table plus a predicate, tested exhaustively rather than by
/// example, because an illegal transition nobody pinned is how a Received order gets reopened.
/// </summary>
public class PurchaseOrderWorkflowTests
{
    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Cancelled)]
    public void Legal_transitions_are_allowed(string from, string to)
    {
        Assert.True(PurchaseOrderWorkflow.CanTransition(from, to));
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Draft)]
    public void Illegal_transitions_are_refused(string from, string to)
    {
        Assert.False(PurchaseOrderWorkflow.CanTransition(from, to));
    }

    [Fact]
    public void Received_and_Cancelled_are_terminal()
    {
        foreach (var to in PurchaseOrderStatus.All)
        {
            Assert.False(PurchaseOrderWorkflow.CanTransition(PurchaseOrderStatus.Received, to));
            Assert.False(PurchaseOrderWorkflow.CanTransition(PurchaseOrderStatus.Cancelled, to));
        }
    }

    [Theory]
    [InlineData(10, 0, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Ordered)]
    [InlineData(10, 4, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(10, 10, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Received)]
    [InlineData(10, 10, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received)]
    [InlineData(10, 4, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.PartiallyReceived)]
    public void The_status_follows_the_quantities(
        int ordered, int received, string current, string expected)
    {
        Assert.Equal(expected, PurchaseOrderWorkflow.StatusFor(ordered, received, current));
    }

    [Fact]
    public void A_cancelled_order_is_not_dragged_forward_by_its_quantities()
    {
        // Receiving is refused against a cancelled order, so StatusFor should never be asked -
        // but if it is, it must not resurrect it.
        Assert.Equal(
            PurchaseOrderStatus.Cancelled,
            PurchaseOrderWorkflow.StatusFor(10, 10, PurchaseOrderStatus.Cancelled));
    }

    [Fact]
    public void Only_Ordered_and_PartiallyReceived_are_open_for_receiving()
    {
        Assert.True(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Ordered));
        Assert.True(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.PartiallyReceived));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Draft));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Received));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Cancelled));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~PurchaseOrderWorkflowTests"`

Expected: FAIL to compile — `The name 'PurchaseOrderWorkflow' does not exist`.

- [ ] **Step 3: Write the workflow**

Append to `src/AssetDesk.Api/Entities/PurchaseOrder.cs`:

```csharp
/// <summary>
/// The single source of truth for how a purchase order may move between statuses. Shaped after
/// TicketWorkflow, which is the house pattern.
///
/// PartiallyReceived and Received are produced by <see cref="StatusFor"/> from the quantities,
/// never set by hand: a status a user could set independently of what has arrived would
/// immediately disagree with it.
/// </summary>
public static class PurchaseOrderWorkflow
{
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [PurchaseOrderStatus.Draft] = [PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Cancelled],
        [PurchaseOrderStatus.Ordered] =
        [
            PurchaseOrderStatus.PartiallyReceived,
            PurchaseOrderStatus.Received,
            PurchaseOrderStatus.Cancelled
        ],
        [PurchaseOrderStatus.PartiallyReceived] =
        [
            PurchaseOrderStatus.Received,
            PurchaseOrderStatus.Cancelled
        ],
        [PurchaseOrderStatus.Received] = [],
        [PurchaseOrderStatus.Cancelled] = []
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>Goods can only be received against an order that has been placed and is not finished.</summary>
    public static bool IsOpen(string status) =>
        status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>
    /// The status the quantities imply. A terminal status is returned unchanged - receiving is
    /// refused against Cancelled anyway, but this must not resurrect one if it is ever asked.
    /// </summary>
    public static string StatusFor(int totalOrdered, int totalReceived, string currentStatus)
    {
        if (currentStatus is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Draft)
            return currentStatus;

        if (totalReceived <= 0) return PurchaseOrderStatus.Ordered;
        return totalReceived >= totalOrdered
            ? PurchaseOrderStatus.Received
            : PurchaseOrderStatus.PartiallyReceived;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~PurchaseOrderWorkflowTests"`

Expected: PASS, 22 test cases (3 facts plus 19 InlineData rows).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 369 passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Api/Entities/PurchaseOrder.cs tests/AssetDesk.Api.Tests/PurchaseOrderWorkflowTests.cs
git commit -m "feat(procurement): the purchase order status table

Shaped after TicketWorkflow and tested exhaustively rather than by example - an
illegal transition nobody pinned is how a Received order gets reopened.

StatusFor derives the status from the quantities so it cannot disagree with
them, and returns a terminal status unchanged so it can never resurrect a
cancelled order.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Permissions

**Files:**
- Modify: `src/AssetDesk.Api/Authorization/Permissions.cs`, `src/AssetDesk.Api/Program.cs`, `src/AssetDesk.Api/Controllers/SuppliersController.cs`
- Create: `src/AssetDesk.Api/Migrations/<ts>_GrantProcurementPermissions.cs`

**Interfaces:**
- Produces: `Permissions.ProcurementView` = `"iams:procurement:view"`, `Permissions.ProcurementManage` = `"iams:procurement:manage"`; policies `"CanViewProcurement"` and `"CanManageProcurement"`. Tasks 6, 8 and 11 gate on these.

**Warning:** `PermissionCatalogTests` and `RolePermissionSeedTests` may assert on the catalogue. Adding keys can legitimately break them — particularly `BuiltInRoles_HaveTheExpectedGrantCount`, because this task changes `Staff` and `Auditor` bundles. If one fails, fix it so it tests the new intended catalogue, never by deleting a case or weakening an assertion, and say exactly what you changed and why.

- [ ] **Step 1: Write the failing tests**

Append to `tests/AssetDesk.Api.Tests/SupplierApiTests.cs`:

```csharp
    [Fact]
    public void Both_procurement_keys_are_in_the_catalog_under_one_group()
    {
        var view = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.ProcurementView);
        var manage = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.ProcurementManage);

        Assert.Equal("iams:procurement:view", view.Key);
        Assert.Equal("iams:procurement:manage", manage.Key);
        Assert.Equal("Procurement", view.Group);
        Assert.Equal("Procurement", manage.Group);
    }

    [Fact]
    public void Staff_can_run_procurement_and_an_Auditor_can_only_read_it()
    {
        var staff = AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Staff);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementView, staff);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementManage, staff);

        var auditor = AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Auditor);
        Assert.Contains(AssetDesk.Api.Authorization.Permissions.ProcurementView, auditor);
        Assert.DoesNotContain(AssetDesk.Api.Authorization.Permissions.ProcurementManage, auditor);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~SupplierApiTests"`

Expected: FAIL to compile — `'Permissions' does not contain a definition for 'ProcurementView'`.

- [ ] **Step 3: Add the keys and descriptors**

In `src/AssetDesk.Api/Authorization/Permissions.cs`, after the `DepreciationManage` constant:

```csharp
    public const string ProcurementView = "iams:procurement:view";
    public const string ProcurementManage = "iams:procurement:manage";
```

and as the last entries of `All`:

```csharp
        new(ProcurementView, "Procurement", "View purchasing",
            "See suppliers, purchase orders and what has been received."),
        new(ProcurementManage, "Procurement", "Manage purchasing",
            "Create suppliers and purchase orders, and receive deliveries."),
```

- [ ] **Step 4: Grant them to the right roles**

In the same file's `DefaultsFor`, add `ProcurementView, ProcurementManage` to the `Roles.Staff` list — running the asset estate is what Staff is for, and a Staff user who cannot receive a delivery cannot do the job. Add `ProcurementView` only to the `Roles.Auditor` list: read-only oversight.

`Admin` and `SuperAdmin` both `return Array.AsReadOnly(Keys)`, so they pick both up with no change.

- [ ] **Step 5: Register the policies**

In `src/AssetDesk.Api/Program.cs`, beside the other `.RequirePermission(...)` calls:

```csharp
    .RequirePermission("CanViewProcurement", Permissions.ProcurementView)
    .RequirePermission("CanManageProcurement", Permissions.ProcurementManage)
```

- [ ] **Step 6: Gate the suppliers controller**

Replace the class-level attribute in `src/AssetDesk.Api/Controllers/SuppliersController.cs`:

```csharp
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewProcurement")]
```

and put `[Authorize(Policy = "CanManageProcurement")]` on `Create`, `Update` and `Delete` individually, so a viewer can list suppliers but not change them.

- [ ] **Step 7: Write the backfill migration**

```bash
cd src/AssetDesk.Api && dotnet ef migrations add GrantProcurementPermissions
```

Replace the generated empty bodies. This copies `20260912050134_GrantDepreciationManagePermission`, which copies `GrantAuditViewPermission` — read one of them alongside this step:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant
            // rows are the cross product of tenants and the built-in roles that should hold each
            // key - not a join on RoleId.
            //
            // The id comes from md5(...)::uuid rather than gen_random_uuid(): that function is
            // only a built-in from PostgreSQL 13, and the database here is supplied externally
            // with no version pinned. Being deterministic also makes the insert idempotent - the
            // NOT EXISTS guard and the id agree with each other on a re-run.
            //
            // Two literal blocks rather than one interpolated helper. They differ only in the key
            // and the role list, but a migration that builds SQL by string interpolation reads
            // like an injection site to everyone who meets it later, and the two migrations this
            // copies both spell their SQL out.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:procurement:view')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:procurement:view'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff', 'Auditor')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:procurement:view'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:procurement:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:procurement:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:procurement:manage'
                  );
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes each key entirely, including from any tenant that granted it to a custom
            // role after this migration ran. That is the correct reversal: rolling back to a
            // catalogue without these keys leaves those rows referencing a permission no policy
            // checks, which an admin would see as a phantom tick in the /admin/roles matrix.
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions""
                WHERE ""Permission"" IN ('iams:procurement:view', 'iams:procurement:manage');
            ");
        }
```

Note the role lists differ: `Auditor` receives **view only**, so it appears in the first block and not the second.

Add the class-level `<summary>` the precedent carries, explaining that backfilling unconditionally is safe **only** because both keys are brand new, so no tenant can have deliberately revoked them.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 371 passing, 0 failing. If `BuiltInRoles_HaveTheExpectedGrantCount` fails, see the warning at the top of this task — `Staff` gains 2 and `Auditor` gains 1.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Authorization/Permissions.cs src/AssetDesk.Api/Program.cs src/AssetDesk.Api/Controllers/SuppliersController.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/SupplierApiTests.cs
git commit -m "feat(procurement): view and manage permissions, backfilled

EnsureRolePermissionsAsync is gated on RolePermissionsSeededAt and never
revisits a tenant, so keys added today reach nobody provisioned yesterday.
Without the backfill every existing Admin would silently lack them.

Unconditional backfill is safe for one reason only: both keys are brand new, so
no tenant can have deliberately revoked them.

Staff gets both - running the asset estate is what the role is for, and a Staff
user who cannot receive a delivery cannot do the job. Auditor gets view only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Purchase order CRUD

**Files:**
- Create: `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`
- Modify: `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`
- Test: `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`

**Interfaces:**
- Consumes: `PurchaseOrder`, `PurchaseOrderLine`, `PurchaseOrderStatus`, `PurchaseOrderWorkflow` (Tasks 3–4), `IPurchaseOrderNumberAllocator` (Task 3), the procurement policies (Task 5).
- Produces: `PurchaseOrderDto`, `PurchaseOrderLineDto`, `CreatePurchaseOrderDto`, `PurchaseOrderLineInputDto`; `GET /api/purchaseorders`, `GET /api/purchaseorders/{id}`, `POST /api/purchaseorders`, `POST /api/purchaseorders/{id}/send`, `POST /api/purchaseorders/{id}/cancel`. Tasks 8, 9 and 11 consume them.

- [ ] **Step 1: Write the failing tests**

Append to `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`:

```csharp
    private static PurchaseOrdersController ControllerFor(
        AssetDesk.Api.Data.AppDbContext db, ITenantProvider tenants) =>
        new(db, tenants, new PurchaseOrderNumberAllocator(db), new LookupService(db));

    private static async Task<Supplier> SeedSupplierAsync(
        AssetDesk.Api.Data.AppDbContext db, Guid tenantId, string name = "Acme Computers")
    {
        var supplier = new Supplier { TenantId = tenantId, Name = name };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static CreatePurchaseOrderDto NewOrder(int supplierId, int quantity = 10) => new()
    {
        SupplierId = supplierId,
        Currency = Currencies.PHP,
        OrderDate = new DateTime(2026, 9, 12),
        Lines =
        [
            new PurchaseOrderLineInputDto
            {
                DeviceType = DeviceTypes.Laptop,
                Description = "Dell Latitude 5540",
                Quantity = quantity,
                UnitPrice = 50000m
            }
        ]
    };

    [Fact]
    public async Task Creating_an_order_allocates_the_next_number_and_starts_it_as_a_draft()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(NewOrder(supplier.Id));

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var saved = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(1, saved.PoNumber);
            Assert.Equal(PurchaseOrderStatus.Draft, saved.Status);
            Assert.Equal(10, Assert.Single(saved.Lines).Quantity);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_line_quantity_of_zero_or_less_is_rejected(int quantity)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId))
                .Create(NewOrder(supplier.Id, quantity));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.PurchaseOrders.ToListAsync());
        }
    }

    [Fact]
    public async Task An_order_with_no_lines_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);

            var dto = NewOrder(supplier.Id) with { Lines = [] };
            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).Create(dto);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_supplier_from_another_tenant_cannot_be_ordered_from()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var bSupplier = await SeedSupplierAsync(db, tenantB);

            var result = await ControllerFor(db, new FakeTenantProvider(tenantA))
                .Create(NewOrder(bSupplier.Id));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.PurchaseOrders.IgnoreQueryFilters().ToListAsync());
        }
    }

    [Fact]
    public async Task Sending_a_draft_makes_it_Ordered_and_sending_twice_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(NewOrder(supplier.Id));
            var order = await db.PurchaseOrders.SingleAsync();

            var first = await controller.Send(order.Id);
            Assert.IsType<OkObjectResult>(first.Result);
            Assert.Equal(PurchaseOrderStatus.Ordered,
                (await db.PurchaseOrders.SingleAsync()).Status);

            var second = await controller.Send(order.Id);
            Assert.IsType<BadRequestObjectResult>(second.Result);
        }
    }

    [Fact]
    public async Task A_received_order_cannot_be_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Received,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            });
            await db.SaveChangesAsync();
            var order = await db.PurchaseOrders.SingleAsync();

            var result = await ControllerFor(db, new FakeTenantProvider(tenantId)).Cancel(order.Id);

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(PurchaseOrderStatus.Received,
                (await db.PurchaseOrders.SingleAsync()).Status);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~PurchaseOrderApiTests"`

Expected: FAIL to compile — `The type or namespace name 'PurchaseOrdersController' could not be found`.

- [ ] **Step 3: Add the DTOs**

Append to `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`:

```csharp
public record PurchaseOrderLineInputDto
{
    [Required, StringLength(50)]
    public required string DeviceType { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    [Range(1, 100000, ErrorMessage = "Quantity must be at least 1")]
    public int Quantity { get; init; }

    [Range(0, 100000000, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; init; }
}

public record CreatePurchaseOrderDto
{
    public int SupplierId { get; init; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = "PHP";

    public DateTime OrderDate { get; init; } = DateTime.UtcNow;
    public DateTime? ExpectedDate { get; init; }
    public string? Notes { get; init; }

    public List<PurchaseOrderLineInputDto> Lines { get; init; } = [];
}

public record PurchaseOrderLineDto
{
    public int Id { get; init; }
    public required string DeviceType { get; init; }
    public string? Description { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public int ReceivedQuantity { get; init; }
    public int OutstandingQuantity { get; init; }
    public decimal LineTotal { get; init; }
}

public record PurchaseOrderDto
{
    public int Id { get; init; }
    public int PoNumber { get; init; }

    /// <summary>Rendered PO-0042 in the UI; the raw number is kept so callers can sort on it.</summary>
    public string Reference { get; init; } = "";

    public int SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public DateTime OrderDate { get; init; }
    public DateTime? ExpectedDate { get; init; }
    public string? Notes { get; init; }
    public decimal OrderTotal { get; init; }
    public List<PurchaseOrderLineDto> Lines { get; init; } = [];
}
```

- [ ] **Step 4: Create the controller**

Create `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs` with the five endpoints. Every query resolves the tenant explicitly, exactly as `SuppliersController` does:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewProcurement")]
public class PurchaseOrdersController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    IPurchaseOrderNumberAllocator numbers,
    ILookupService lookups) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PurchaseOrderDto>>>> GetAll()
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<PurchaseOrderDto>>.Fail("Select an organisation first."));

        var orders = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .OrderByDescending(p => p.PoNumber)
            .ToListAsync();

        return Ok(ApiResponse<List<PurchaseOrderDto>>.Ok(orders.Select(Map).ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> GetById(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        return order is null
            ? NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    [HttpPost]
    [Authorize(Policy = "CanManageProcurement")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Create(CreatePurchaseOrderDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        if (dto.Lines.Count == 0)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("A purchase order needs at least one line."));

        foreach (var line in dto.Lines)
        {
            if (line.Quantity <= 0)
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Every line needs a quantity of at least 1."));
            if (line.UnitPrice < 0)
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("A unit price cannot be negative."));
            if (!await lookups.IsActiveValueAsync(LookupTypes.DeviceType, line.DeviceType))
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail($"'{line.DeviceType}' is not an active device type."));
        }

        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency))
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail($"'{dto.Currency}' is not a valid currency."));

        // Explicit tenant filter, not the global one - a super admin's filter admits every
        // tenant's suppliers, and ordering from another organisation's supplier would be silent.
        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && s.TenantId == tenantId);
        if (supplier is null)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Supplier not found."));

        var order = new PurchaseOrder
        {
            TenantId = tenantId,
            PoNumber = await numbers.NextAsync(tenantId),
            SupplierId = supplier.Id,
            Currency = dto.Currency,
            Status = PurchaseOrderStatus.Draft,
            OrderDate = dto.OrderDate,
            ExpectedDate = dto.ExpectedDate,
            Notes = dto.Notes,
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""
        };

        foreach (var line in dto.Lines)
        {
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = line.DeviceType,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            });
        }

        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        await db.Entry(order).Reference(o => o.Supplier).LoadAsync();
        return CreatedAtAction(nameof(GetById), new { id = order.Id },
            ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    [HttpPost("{id:int}/send")]
    [Authorize(Policy = "CanManageProcurement")]
    public Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Send(int id) =>
        TransitionAsync(id, PurchaseOrderStatus.Ordered);

    [HttpPost("{id:int}/cancel")]
    [Authorize(Policy = "CanManageProcurement")]
    public Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Cancel(int id) =>
        TransitionAsync(id, PurchaseOrderStatus.Cancelled);

    /// <summary>
    /// The only statuses a user may set directly. PartiallyReceived and Received come from the
    /// quantities via PurchaseOrderWorkflow.StatusFor, never from here.
    /// </summary>
    private async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> TransitionAsync(int id, string to)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        if (order is null)
            return NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."));

        if (!PurchaseOrderWorkflow.CanTransition(order.Status, to))
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(
                $"A {order.Status} purchase order cannot become {to}."));

        order.Status = to;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    private static PurchaseOrderDto Map(PurchaseOrder p) => new()
    {
        Id = p.Id,
        PoNumber = p.PoNumber,
        Reference = $"PO-{p.PoNumber:D4}",
        SupplierId = p.SupplierId,
        SupplierName = p.Supplier?.Name,
        Currency = p.Currency,
        Status = p.Status,
        OrderDate = p.OrderDate,
        ExpectedDate = p.ExpectedDate,
        Notes = p.Notes,
        OrderTotal = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
        Lines = p.Lines.Select(l => new PurchaseOrderLineDto
        {
            Id = l.Id,
            DeviceType = l.DeviceType,
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            ReceivedQuantity = l.ReceivedQuantity,
            OutstandingQuantity = l.Quantity - l.ReceivedQuantity,
            LineTotal = l.Quantity * l.UnitPrice
        }).ToList()
    };
}
```

`OrderTotal` and `LineTotal` are in the order's own currency, not pesos. Task 11 must label them with that currency, never with a peso sign.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 378 passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs src/AssetDesk.Shared/DTOs/ProcurementDto.cs tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs
git commit -m "feat(procurement): create, send and cancel purchase orders

Send and cancel are the only status changes a user may make. PartiallyReceived
and Received come from the quantities through PurchaseOrderWorkflow.StatusFor,
so the status cannot disagree with what has actually arrived.

The supplier lookup filters on the tenant explicitly: a super admin's global
filter admits every organisation's suppliers, and ordering from the wrong one
would be silent.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: Goods receipts and the asset link

**Files:**
- Create: `src/AssetDesk.Api/Entities/GoodsReceipt.cs`
- Modify: `src/AssetDesk.Api/Entities/Asset.cs`, `src/AssetDesk.Api/Entities/PurchaseOrder.cs` (restore `Receipts`), `src/AssetDesk.Api/Data/AppDbContext.cs`
- Test: `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs`

**Interfaces:**
- Produces: `GoodsReceipt` (`PurchaseOrderId`, `ReceiptDate`, `ExchangeRate`, `ReceivedByUserId`, `Notes`, `Lines`), `GoodsReceiptLine` (`GoodsReceiptId`, `PurchaseOrderLineId`, `QuantityReceived`), `Asset.GoodsReceiptLineId` (nullable int). Task 8 writes all of them.

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class GoodsReceiptTests
{
    [Fact]
    public async Task An_asset_remembers_the_receipt_line_that_created_it()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
                Currency = Currencies.USD, Status = PurchaseOrderStatus.Ordered,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            };
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Laptop, Quantity = 10, UnitPrice = 1200m
            });
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            var receipt = new GoodsReceipt
            {
                TenantId = tenantId,
                PurchaseOrderId = order.Id,
                ReceiptDate = new DateTime(2026, 9, 12),
                ExchangeRate = 58.20m,
                ReceivedByUserId = "user-1"
            };
            receipt.Lines.Add(new GoodsReceiptLine
            {
                PurchaseOrderLineId = order.Lines.First().Id,
                QuantityReceived = 2
            });
            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync();

            var asset = new Asset
            {
                TenantId = tenantId,
                AssetTag = "LAP-0001",
                DeviceType = DeviceTypes.Laptop,
                Status = AssetStatus.Available,
                GoodsReceiptLineId = receipt.Lines.First().Id
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            var saved = await db.Assets.SingleAsync(a => a.AssetTag == "LAP-0001");
            Assert.Equal(receipt.Lines.First().Id, saved.GoodsReceiptLineId);

            // A hand-entered asset keeps it null - the system knows which assets it can
            // account for and which it cannot.
            var manual = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002");
            Assert.Null(manual.GoodsReceiptLineId);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~GoodsReceiptTests"`

Expected: FAIL to compile — `The type or namespace name 'GoodsReceipt' could not be found`.

- [ ] **Step 3: Create the entities**

Create `src/AssetDesk.Api/Entities/GoodsReceipt.cs`:

```csharp
namespace AssetDesk.Api.Entities;

/// <summary>
/// What actually arrived against a purchase order, on one occasion. The exchange rate lives
/// here rather than on the order because an invoice states the rate it was booked at and the
/// invoice arrives with the goods - and because two deliveries months apart against one USD
/// order genuinely cost different pesos.
/// </summary>
public class GoodsReceipt : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public DateTime ReceiptDate { get; set; } = DateTime.UtcNow;

    /// <summary>Pesos per one unit of the order's currency, as booked. Exactly 1 for PHP.</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    public required string ReceivedByUserId { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GoodsReceiptLine> Lines { get; set; } = [];
}

public class GoodsReceiptLine
{
    public int Id { get; set; }

    public int GoodsReceiptId { get; set; }
    public GoodsReceipt? GoodsReceipt { get; set; }

    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    public int QuantityReceived { get; set; }
}
```

- [ ] **Step 4: Add the asset link and restore the order's receipts**

In `src/AssetDesk.Api/Entities/Asset.cs`, after `LastVerifiedAt`:

```csharp
    /// <summary>
    /// The goods-receipt line that created this asset, when it was created by receiving a
    /// purchase order. Null for an asset entered by hand or imported - which is honest: the
    /// system then knows which assets it can account for and which it cannot.
    /// </summary>
    public int? GoodsReceiptLineId { get; set; }
    public GoodsReceiptLine? GoodsReceiptLine { get; set; }
```

In `src/AssetDesk.Api/Entities/PurchaseOrder.cs`, uncomment the `Receipts` collection commented out in Task 3.

- [ ] **Step 5: Configure them**

In `src/AssetDesk.Api/Data/AppDbContext.cs`:

```csharp
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
```

```csharp
        modelBuilder.Entity<GoodsReceipt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExchangeRate).HasPrecision(18, 6);

            entity.HasOne(e => e.PurchaseOrder)
                .WithMany(p => p.Receipts)
                .HasForeignKey(e => e.PurchaseOrderId)
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

        modelBuilder.Entity<GoodsReceiptLine>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.GoodsReceipt)
                .WithMany(r => r.Lines)
                .HasForeignKey(e => e.GoodsReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PurchaseOrderLine)
                .WithMany()
                .HasForeignKey(e => e.PurchaseOrderLineId)
                .OnDelete(DeleteBehavior.Restrict);
        });
```

And inside the existing `modelBuilder.Entity<Asset>` block:

```csharp
            entity.HasOne(e => e.GoodsReceiptLine)
                .WithMany()
                .HasForeignKey(e => e.GoodsReceiptLineId)
                .OnDelete(DeleteBehavior.Restrict);
```

`ExchangeRate` uses `HasPrecision(18, 6)`, matching `Asset.ExchangeRate` exactly — the rate copied onto created assets must survive the copy unchanged.

- [ ] **Step 6: Generate the migration and check its SQL**

```bash
cd src/AssetDesk.Api && dotnet ef migrations add AddGoodsReceipts
dotnet ef migrations script --idempotent --no-build | grep -E 'ExchangeRate|GoodsReceiptLineId' | head -6
```

Expected: `"ExchangeRate" numeric(18,6) NOT NULL DEFAULT 1.0` on `GoodsReceipts`, and `"GoodsReceiptLineId" integer NULL` added to `Assets`.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 379 passing, 0 failing.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Api/Entities/GoodsReceipt.cs src/AssetDesk.Api/Entities/Asset.cs src/AssetDesk.Api/Entities/PurchaseOrder.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs
git commit -m "feat(procurement): goods receipts and the asset provenance link

The exchange rate lives on the receipt rather than the order: an invoice states
the rate it was booked at and arrives with the goods, and two deliveries months
apart against one USD order genuinely cost different pesos.

Asset.GoodsReceiptLineId is nullable and stays null for anything entered by
hand or imported, so the system knows which assets it can account for.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Receiving

**Files:**
- Create: `src/AssetDesk.Api/Services/GoodsReceiptService.cs`
- Modify: `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`, `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`, `src/AssetDesk.Api/Program.cs`
- Test: `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1, 3, 4 and 7.
- Produces: `IGoodsReceiptService.ReceiveAsync(int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId, CancellationToken ct = default) -> Task<ServiceResult<int>>` (the new receipt's id); `ReceiveGoodsDto` (`ReceiptDate`, `ExchangeRate`, `Notes`, `List<ReceiveLineDto> Lines`), `ReceiveLineDto` (`PurchaseOrderLineId`, `QuantityReceived`); `POST /api/purchaseorders/{id}/receive`.

**This is the task the feature exists for.** Read `src/AssetDesk.Api/Services/TicketService.Fulfilment.cs` in full before writing anything — it is the pattern, and its comments explain why each part is shaped the way it is.

- [ ] **Step 1: Write the failing tests**

Append to `tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs`. These are the crux of the feature — they must all be present:

```csharp
    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Line)> SeedOrderedAsync(
        AssetDesk.Api.Data.AppDbContext db, Guid tenantId,
        string currency = "PHP", int quantity = 10, decimal unitPrice = 50000m)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
            Currency = currency, Status = PurchaseOrderStatus.Ordered,
            OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop,
            Description = "Dell Latitude 5540",
            Quantity = quantity,
            UnitPrice = unitPrice
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (order, order.Lines.First());
    }

    private static GoodsReceiptService ServiceFor(AssetDesk.Api.Data.AppDbContext db) =>
        new(db, new AssetTagGenerator(db), NullLogger<GoodsReceiptService>.Instance);

    private static ReceiveGoodsDto Receive(int lineId, int qty, decimal rate = 1m, DateTime? date = null) => new()
    {
        ReceiptDate = date ?? new DateTime(2026, 9, 12),
        ExchangeRate = rate,
        Lines = [new ReceiveLineDto { PurchaseOrderLineId = lineId, QuantityReceived = qty }]
    };

    [Fact]
    public async Task Receiving_part_of_a_line_leaves_the_order_partially_received()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");

            Assert.True(result.Success);
            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.PartiallyReceived, reloaded.Status);
            Assert.Equal(8, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(8, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task Receiving_the_rest_closes_the_order()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            var service = ServiceFor(db);

            await service.ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");
            var result = await service.ReceiveAsync(order.Id, Receive(line.Id, 2), "user-1");

            Assert.True(result.Success);
            var reloaded = await db.PurchaseOrders.Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.Received, reloaded.Status);
            Assert.Equal(10, reloaded.Lines.First().ReceivedQuantity);
            Assert.Equal(10, await db.Assets.CountAsync());
            Assert.Equal(2, await db.GoodsReceipts.CountAsync());
        }
    }

    [Fact]
    public async Task Receiving_more_than_was_ordered_is_refused_and_changes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 11), "user-1");

            Assert.False(result.Success);
            Assert.Contains("10", result.Message);
            Assert.Equal(0, await db.Assets.CountAsync());
            Assert.Equal(0, await db.GoodsReceipts.CountAsync());
            Assert.Equal(0, (await db.PurchaseOrders.Include(p => p.Lines).SingleAsync()).Lines.First().ReceivedQuantity);
        }
    }

    [Fact]
    public async Task Receiving_more_than_remains_after_a_partial_receipt_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            var service = ServiceFor(db);
            await service.ReceiveAsync(order.Id, Receive(line.Id, 8), "user-1");

            var result = await service.ReceiveAsync(order.Id, Receive(line.Id, 3), "user-1");

            Assert.False(result.Success);
            Assert.Equal(8, await db.Assets.CountAsync());
        }
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Received)]
    public async Task Receiving_against_a_closed_or_unsent_order_is_refused(string status)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);
            order.Status = status;
            await db.SaveChangesAsync();

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task A_quantity_of_zero_or_less_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(line.Id, 0), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task Each_created_asset_carries_the_line_price_the_order_currency_and_the_receipt_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId, Currencies.USD, 10, 1200m);

            await ServiceFor(db).ReceiveAsync(
                order.Id, Receive(line.Id, 2, rate: 58.20m, date: new DateTime(2026, 9, 12)), "user-1");

            var assets = await db.Assets.ToListAsync();
            Assert.Equal(2, assets.Count);
            foreach (var asset in assets)
            {
                Assert.Equal(1200m, asset.PurchasePrice);
                Assert.Equal(Currencies.USD, asset.Currency);
                Assert.Equal(58.20m, asset.ExchangeRate);
                Assert.Equal(new DateTime(2026, 9, 12), asset.PurchaseDate);
                Assert.Equal(DeviceTypes.Laptop, asset.DeviceType);
                Assert.Equal(AssetStatus.Available, asset.Status);
                Assert.NotNull(asset.GoodsReceiptLineId);
            }

            // Distinct tags, not ten copies of one.
            Assert.Equal(2, assets.Select(a => a.AssetTag).Distinct().Count());
        }
    }

    [Fact]
    public async Task Two_deliveries_at_different_rates_produce_assets_carrying_each_batchs_own_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, line) = await SeedOrderedAsync(db, tenantId, Currencies.USD, 10, 1200m);
            var service = ServiceFor(db);

            await service.ReceiveAsync(order.Id, Receive(line.Id, 8, rate: 58.20m), "user-1");
            await service.ReceiveAsync(order.Id, Receive(line.Id, 2, rate: 52.00m), "user-1");

            var rates = await db.Assets.GroupBy(a => a.ExchangeRate)
                .Select(g => new { Rate = g.Key, Count = g.Count() })
                .ToListAsync();

            Assert.Equal(8, Assert.Single(rates, r => r.Rate == 58.20m).Count);
            Assert.Equal(2, Assert.Single(rates, r => r.Rate == 52.00m).Count);
        }
    }

    [Fact]
    public async Task An_unknown_purchase_order_line_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (order, _) = await SeedOrderedAsync(db, tenantId);

            var result = await ServiceFor(db).ReceiveAsync(order.Id, Receive(99999, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }

    [Fact]
    public async Task A_line_belonging_to_a_different_order_is_refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var (orderA, _) = await SeedOrderedAsync(db, tenantId);

            var supplierB = new Supplier { TenantId = tenantId, Name = "Beta Supplies" };
            db.Suppliers.Add(supplierB);
            await db.SaveChangesAsync();
            var orderB = new PurchaseOrder
            {
                TenantId = tenantId, PoNumber = 2, SupplierId = supplierB.Id,
                Currency = Currencies.PHP, Status = PurchaseOrderStatus.Ordered,
                OrderDate = DateTime.UtcNow, CreatedByUserId = "user-1"
            };
            orderB.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = DeviceTypes.Monitor, Quantity = 5, UnitPrice = 9000m
            });
            db.PurchaseOrders.Add(orderB);
            await db.SaveChangesAsync();

            // Receiving order A but naming a line that belongs to order B.
            var result = await ServiceFor(db)
                .ReceiveAsync(orderA.Id, Receive(orderB.Lines.First().Id, 1), "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, await db.Assets.CountAsync());
        }
    }
```

Add `using AssetDesk.Api.Services;`, `using AssetDesk.Shared.DTOs;` and `using Microsoft.Extensions.Logging.Abstractions;` to the file's usings.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~GoodsReceiptTests"`

Expected: FAIL to compile — `The type or namespace name 'GoodsReceiptService' could not be found`.

- [ ] **Step 3: Add the DTOs**

Append to `src/AssetDesk.Shared/DTOs/ProcurementDto.cs`:

```csharp
public record ReceiveLineDto
{
    public int PurchaseOrderLineId { get; init; }

    [Range(1, 100000, ErrorMessage = "Quantity received must be at least 1")]
    public int QuantityReceived { get; init; }
}

public record ReceiveGoodsDto
{
    public DateTime ReceiptDate { get; init; } = DateTime.UtcNow;

    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal ExchangeRate { get; init; } = 1m;

    public string? Notes { get; init; }
    public List<ReceiveLineDto> Lines { get; init; } = [];
}
```

- [ ] **Step 4: Write the service**

Create `src/AssetDesk.Api/Services/GoodsReceiptService.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Services;

public interface IGoodsReceiptService
{
    Task<ServiceResult<int>> ReceiveAsync(
        int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId, CancellationToken ct = default);
}

public class GoodsReceiptService(
    AppDbContext db,
    IAssetTagGenerator tags,
    ILogger<GoodsReceiptService> logger) : IGoodsReceiptService
{
    /// <summary>
    /// Records a delivery against a purchase order and creates one asset per unit received.
    ///
    /// Receipt creation, asset creation, the line's received count and the order's status are
    /// one transaction. A partial success would create assets the order does not know it
    /// produced, or advance a received count without the assets to match - either leaves the
    /// register lying, which is the one thing this system exists to prevent.
    ///
    /// The transaction runs through the provider's execution strategy because production is
    /// Npgsql with EnableRetryOnFailure and EF Core refuses a user-initiated transaction under a
    /// retrying strategy. That means the delegate can run more than once, so it clears the
    /// change tracker on entry and re-reads everything: a retry must not see the failed
    /// attempt's mutations.
    /// </summary>
    public async Task<ServiceResult<int>> ReceiveAsync(
        int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId, CancellationToken ct = default)
    {
        if (dto.Lines.Count == 0)
            return ServiceResult<int>.Fail("Nothing was received.");

        if (dto.ExchangeRate <= 0m)
            return ServiceResult<int>.Fail("Exchange rate must be greater than zero.");

        if (dto.Lines.Any(l => l.QuantityReceived <= 0))
            return ServiceResult<int>.Fail("A received quantity must be at least 1.");

        if (dto.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != dto.Lines.Count)
            return ServiceResult<int>.Fail("The same line appears more than once.");

        var strategy = db.Database.CreateExecutionStrategy();

        // Captured by the successful attempt and used after the strategy is done. Nothing here
        // raises a notification, but if one is ever added it belongs out here: the delegate is
        // retryable, and a notification inside it would fire again on every replay.
        var receiptId = 0;

        var result = await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this delegate on the same DbContext, which still tracks the failed
            // attempt's mutations. Starting from a cleared tracker is what makes each attempt
            // independent: every entity below is loaded fresh and every guard re-evaluated.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var order = await db.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == purchaseOrderId, ct);

            if (order is null)
                return ServiceResult<int>.Fail("Purchase order not found.");

            if (!PurchaseOrderWorkflow.IsOpen(order.Status))
                return ServiceResult<int>.Fail(
                    $"A {order.Status} purchase order cannot receive goods.");

            var receipt = new GoodsReceipt
            {
                TenantId = order.TenantId,
                PurchaseOrderId = order.Id,
                ReceiptDate = dto.ReceiptDate,
                ExchangeRate = dto.ExchangeRate,
                ReceivedByUserId = actingUserId,
                Notes = dto.Notes
            };

            var tagCache = new Dictionary<string, int>();

            foreach (var incoming in dto.Lines)
            {
                var line = order.Lines.FirstOrDefault(l => l.Id == incoming.PurchaseOrderLineId);
                if (line is null)
                    return ServiceResult<int>.Fail("That line does not belong to this purchase order.");

                // The invariant, re-read inside the transaction. The caller may also have
                // pre-checked this for a friendlier message; that pre-check is not what holds.
                // Two concurrent receipts must not both claim the last unit.
                if (line.ReceivedQuantity + incoming.QuantityReceived > line.Quantity)
                    return ServiceResult<int>.Fail(
                        $"{line.DeviceType}: {line.Quantity - line.ReceivedQuantity} of {line.Quantity} outstanding, " +
                        $"cannot receive {incoming.QuantityReceived}.");

                var receiptLine = new GoodsReceiptLine
                {
                    PurchaseOrderLineId = line.Id,
                    QuantityReceived = incoming.QuantityReceived
                };
                receipt.Lines.Add(receiptLine);

                line.ReceivedQuantity += incoming.QuantityReceived;
            }

            db.GoodsReceipts.Add(receipt);

            // Saved before the assets so each receipt line has an id to point at.
            await db.SaveChangesAsync(ct);

            foreach (var receiptLine in receipt.Lines)
            {
                var line = order.Lines.First(l => l.Id == receiptLine.PurchaseOrderLineId);

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

            var totalOrdered = order.Lines.Sum(l => l.Quantity);
            var totalReceived = order.Lines.Sum(l => l.ReceivedQuantity);
            order.Status = PurchaseOrderWorkflow.StatusFor(totalOrdered, totalReceived, order.Status);
            order.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            receiptId = receipt.Id;

            logger.LogInformation(
                "Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s)",
                receipt.Lines.Count, order.PoNumber, receipt.Lines.Sum(l => l.QuantityReceived));

            return ServiceResult<int>.Ok(receipt.Id);
        });

        return result;
    }
}
```

- [ ] **Step 5: Register it and add the endpoint**

In `src/AssetDesk.Api/Program.cs`:

```csharp
builder.Services.AddScoped<IGoodsReceiptService, GoodsReceiptService>();
```

In `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`, inject `IGoodsReceiptService receipts` into the primary constructor and add:

```csharp
    [HttpPost("{id:int}/receive")]
    [Authorize(Policy = "CanManageProcurement")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Receive(int id, ReceiveGoodsDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        // Resolve through an explicit tenant filter before handing the id to the service, so a
        // caller cannot receive against another organisation's order.
        var exists = await db.PurchaseOrders
            .AnyAsync(p => p.Id == id && p.TenantId == tenantId);
        if (!exists)
            return NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."));

        var actingUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var result = await receipts.ReceiveAsync(id, dto, actingUserId);

        if (!result.Success)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(result.Message ?? "Receiving failed."));

        return await GetById(id);
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~GoodsReceiptTests"`

Expected: PASS, 12 test cases (9 facts plus the 3-case theory), plus the one from Task 7.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 391 passing, 0 failing.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Api/Services/GoodsReceiptService.cs src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs src/AssetDesk.Shared/DTOs/ProcurementDto.cs src/AssetDesk.Api/Program.cs tests/AssetDesk.Api.Tests/GoodsReceiptTests.cs
git commit -m "feat(procurement): receive goods and create the assets

Receipt, assets, received counts and the order's status are one transaction.
A partial success would create assets the order does not know it produced, or
advance a count without the assets to match - either leaves the register lying.

The transaction runs through the execution strategy because Npgsql retries mean
EF refuses a user-initiated transaction otherwise, so the delegate can replay
and clears the change tracker on entry.

The over-receipt guard is re-read inside the transaction rather than trusted
from a pre-check, so two concurrent receipts cannot both claim the last unit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: The purchase order PDF

**Files:**
- Modify: `src/AssetDesk.Api/Services/PdfReportService.cs`, `src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs`
- Test: `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`

**Interfaces:**
- Produces: `IPdfReportService.BuildPurchaseOrderPdf(PurchaseOrderDto order) -> byte[]`; `GET /api/purchaseorders/{id}/pdf`.

A purchase order exists to be sent to a supplier, so this is not an optional extra.

- [ ] **Step 1: Write the failing test**

Append to `tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs`:

```csharp
    [Fact]
    public async Task The_pdf_renders_and_is_a_real_pdf()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var supplier = await SeedSupplierAsync(db, tenantId);
            var controller = ControllerFor(db, new FakeTenantProvider(tenantId));
            await controller.Create(NewOrder(supplier.Id));
            var order = await db.PurchaseOrders.SingleAsync();

            var pdfController = new PurchaseOrdersController(
                db, new FakeTenantProvider(tenantId), new PurchaseOrderNumberAllocator(db),
                new LookupService(db), null!, new PdfReportService());

            var file = Assert.IsType<FileContentResult>(await pdfController.GetPdf(order.Id));

            Assert.Equal("application/pdf", file.ContentType);
            Assert.True(file.FileContents.Length > 1000);
            // %PDF- magic - QuestPDF would throw on a column mismatch before reaching here.
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(file.FileContents, 0, 5));
        }
    }
```

Match the controller's real constructor parameter order — read it rather than trusting this snippet, and adjust the `null!` placeholders to the right positions.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~The_pdf_renders"`

Expected: FAIL to compile — `'PurchaseOrdersController' does not contain a definition for 'GetPdf'`.

- [ ] **Step 3: Add the PDF builder**

In `src/AssetDesk.Api/Services/PdfReportService.cs`, add to the interface:

```csharp
    byte[] BuildPurchaseOrderPdf(PurchaseOrderDto order);
```

and implement it following `BuildAssetValuePdf`'s shape. Read that method first and match its helper names exactly — `AddHeaderRow(TableDescriptor, params string[])` takes every header at once and there is no `AddHeaderCell`:

```csharp
    public byte[] BuildPurchaseOrderPdf(PurchaseOrderDto order)
    {
        var filters = BuildFilterLine(
            ("Supplier", order.SupplierName),
            ("Order date", order.OrderDate.ToString("yyyy-MM-dd")),
            ("Status", order.Status));

        return BuildDocument($"Purchase Order {order.Reference}", filters, order.Lines.Count, content =>
        {
            content.Column(col =>
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2);   // device type
                        c.RelativeColumn(4);   // description
                        c.RelativeColumn(1);   // quantity
                        c.RelativeColumn(2);   // unit price
                        c.RelativeColumn(2);   // line total
                    });

                    AddHeaderRow(table, "Device Type", "Description", "Qty", "Unit Price", "Total");

                    foreach (var line in order.Lines)
                    {
                        AddBodyCell(table, line.DeviceType);
                        AddBodyCell(table, line.Description ?? "—");
                        AddBodyCell(table, line.Quantity.ToString());
                        AddBodyCell(table, FormatCurrency(line.UnitPrice, order.Currency));
                        AddBodyCell(table, FormatCurrency(line.LineTotal, order.Currency));
                    }
                });

                col.Item().PaddingTop(10).AlignRight().Text(
                    $"Order total  {FormatCurrency(order.OrderTotal, order.Currency)}");

                if (!string.IsNullOrWhiteSpace(order.Notes))
                    col.Item().PaddingTop(10).Text(order.Notes);
            });
        });
    }
```

Every figure goes through `FormatCurrency` with **the order's** currency — a purchase order in USD must not show peso signs.

- [ ] **Step 4: Add the endpoint**

In `PurchaseOrdersController`, inject `IPdfReportService pdf` and add:

```csharp
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> GetPdf(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        if (order is null)
            return NotFound(ApiResponse<object>.Fail("Purchase order not found."));

        var dto = Map(order);
        return File(pdf.BuildPurchaseOrderPdf(dto), "application/pdf", $"{dto.Reference}.pdf");
    }
```

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 392 passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Api/Services/PdfReportService.cs src/AssetDesk.Api/Controllers/PurchaseOrdersController.cs tests/AssetDesk.Api.Tests/PurchaseOrderApiTests.cs
git commit -m "feat(procurement): a purchase order PDF to send the supplier

The test asserts the %PDF- magic rather than only a non-empty byte array:
QuestPDF throws at render time on a column-count mismatch, and the depreciation
PDF went unrendered until the very end because nothing exercised it.

Every figure is formatted in the order's own currency - a USD purchase order
must not show peso signs.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 10: The suppliers screen

**Files:**
- Create: `src/AssetDesk.Web/Pages/Procurement/Suppliers.razor`
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs`, `src/AssetDesk.Web/Layout/MainLayout.razor`

There is no Blazor test project. Verify with `dotnet build` and by reading. Do not claim test coverage, and state in your report that the app was not run.

- [ ] **Step 1: Add the ApiClient methods**

In `src/AssetDesk.Web/Services/ApiClient.cs`, following the exact style of the neighbouring methods — read two of them first, especially their error handling:

```csharp
    public async Task<List<SupplierDto>> GetSuppliersAsync()
    public async Task<(bool Success, string? Error)> SaveSupplierAsync(int? id, UpsertSupplierDto dto)
    public async Task<(bool Success, string? Error)> DeactivateSupplierAsync(int id)
```

`SaveSupplierAsync` posts when `id` is null and puts when it is not.

**The error path matters.** `[ApiController]` model validation returns `ValidationProblemDetails`, not the `ApiResponse` shape, so reading the body as `ApiResponse<object>` yields a null message and the user sees a generic failure. The depreciation feature shipped exactly that bug. Read the body **once** into a string, then try `"message"` and fall back to the `"errors"` dictionary — `ReadErrorMessageAsync` in that file already does this; reuse it rather than writing a second copy.

- [ ] **Step 2: Build the page**

Create `src/AssetDesk.Web/Pages/Procurement/Suppliers.razor` at route `/suppliers`, following `src/AssetDesk.Web/Pages/Admin/Lookups.razor` for structure, modal pattern and error display.

It lists name, contact, email, phone and active state, with add, edit and deactivate. Gate the page body with `<PermissionView Permission="iams:procurement:view">` and the add/edit/deactivate controls with `iams:procurement:manage`, so a viewer sees the list without the buttons.

Reuse only Tailwind classes already present in the project. If you introduce a new one, run `npm --prefix src/AssetDesk.Web run build:css` and bump both the `?v=` in `wwwroot/index.html` and `cacheName` in `service-worker.published.js`. Say in your report which you did.

- [ ] **Step 3: Add the nav entry**

Add `/suppliers` to the navigation beside the other links, gated on `iams:procurement:view`. Find the nav component the existing links use and follow it exactly.

- [ ] **Step 4: Build and verify**

```bash
dotnet build
grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src
```

Expected: build succeeded with 0 errors; the grep returns only `CurrencyFormat.cs` and `EstateDashboard.razor`.

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: still 392 passing, 0 failing. A change here should not move it.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Web/
git commit -m "feat(procurement): the suppliers screen

Reuses ReadErrorMessageAsync so a model-validation failure shows the server's
actual message. Reading a ValidationProblemDetails body as ApiResponse yields a
null message and a generic error - the depreciation feature shipped that bug.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 11: Purchase orders in the UI

**Files:**
- Create: `src/AssetDesk.Web/Pages/Procurement/PurchaseOrders.razor`, `src/AssetDesk.Web/Pages/Procurement/PurchaseOrderDetail.razor`
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs`, `src/AssetDesk.Web/Layout/MainLayout.razor`, `src/AssetDesk.Web/Pages/Assets/View.razor`

No Blazor test project. Verify with `dotnet build` and by reading; state in your report that the app was not run.

- [ ] **Step 1: Add the ApiClient methods**

```csharp
    public async Task<List<PurchaseOrderDto>> GetPurchaseOrdersAsync()
    public async Task<PurchaseOrderDto?> GetPurchaseOrderAsync(int id)
    public async Task<(bool Success, string? Error)> CreatePurchaseOrderAsync(CreatePurchaseOrderDto dto)
    public async Task<(bool Success, string? Error)> SendPurchaseOrderAsync(int id)
    public async Task<(bool Success, string? Error)> CancelPurchaseOrderAsync(int id)
    public async Task<(bool Success, string? Error)> ReceiveGoodsAsync(int id, ReceiveGoodsDto dto)
```

Reuse `ReadErrorMessageAsync` on every failure path — receiving in particular returns messages the user needs to read verbatim, such as which line is over-received and by how much.

- [ ] **Step 2: Build the list page**

Create `PurchaseOrders.razor` at `/purchase-orders`: reference, supplier, order date, status, and order total formatted with **the order's own currency** via `CurrencyFormat.Format(order.OrderTotal, order.Currency)`. Never a hardcoded peso sign. Include a create action opening a form with supplier, currency, dates and repeatable lines (device type, description, quantity, unit price).

- [ ] **Step 3: Build the detail page**

Create `PurchaseOrderDetail.razor` at `/purchase-orders/{Id:int}`, showing the header, the lines with ordered / received / outstanding quantities, the receipts so far, and the actions.

The receive action opens a form pre-filled with each line's **outstanding** quantity, plus a receipt date and — **only when the order's currency is not PHP** — an exchange rate field labelled `Exchange Rate (₱ per 1 USD)` built through `CurrencyFormat.SymbolFor`, never a literal sign.

Send and Cancel appear only for statuses where `PurchaseOrderWorkflow` permits them; the server enforces this regardless, so the UI is hiding impossible actions, not guarding them.

The PDF button downloads `/api/purchaseorders/{id}/pdf` through the existing `downloadWithAuth` helper, the same way the reports do.

- [ ] **Step 4: Show provenance on the asset**

In `src/AssetDesk.Web/Pages/Assets/View.razor`, when the asset has a `GoodsReceiptLineId`, show which supplier and purchase order it came from. The asset DTO does not carry that today — add `SupplierName` and `PurchaseOrderReference` to `AssetDto`, populate them in `AssetsController`'s mapping via the `GoodsReceiptLine → GoodsReceipt → PurchaseOrder → Supplier` chain, and render them. If the mapping's query shape makes that awkward, say so in your report rather than adding a second endpoint.

- [ ] **Step 5: Add the nav entry**

Add `/purchase-orders` beside `/suppliers`, gated on `iams:procurement:view`.

- [ ] **Step 6: Build and verify**

```bash
dotnet build
grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src
```

Expected: build succeeded with 0 errors; the grep returns only `CurrencyFormat.cs` and `EstateDashboard.razor`.

- [ ] **Step 7: Run the suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: still 392 passing, 0 failing, unless Step 4 changed `AssetDto` in a way an existing test asserts — if so, update that test to the new intended shape and say what you changed.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Web/ src/AssetDesk.Shared/ src/AssetDesk.Api/Controllers/AssetsController.cs
git commit -m "feat(procurement): purchase orders in the UI

Order totals render in the order's own currency, not pesos - a USD purchase
order showing peso signs was the exact bug the multi-currency final review
caught on three screens.

The receive form pre-fills each line's outstanding quantity and asks for a rate
only when the order is not in pesos.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Done When

- `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj` reports **392 passing, 0 failing**.
- `dotnet build` succeeds with 0 errors.
- `grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src` returns only `CurrencyFormat.cs` and `EstateDashboard.razor`.
- `grep -rn "GenerateAssetTagAsync" src` returns nothing — the two private copies are gone.
- `dotnet ef migrations script --idempotent` shows a unique index on `("TenantId","PoNumber")` and `"ExchangeRate" numeric(18,6)` on `GoodsReceipts`.
- Receiving 8 of 10, then 2 of 10, leaves one order `Received`, two receipts, ten assets, and — if the two receipts carried different rates — assets carrying each batch's own rate.
- Every procurement controller has a test proving a super-admin caller cannot reach another tenant's rows.
