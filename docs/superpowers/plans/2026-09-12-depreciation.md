# Depreciation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show what the asset estate is actually worth today — net book value computed from a per-device-type depreciation policy — beside the existing report of what it cost.

**Architecture:** A tenant-scoped `DepreciationPolicy` (device type → useful life + residual percent) drives a **pure** `DepreciationCalculator` that takes cost basis, purchase date and policy and returns book value. Nothing is stored per period: book value is computed on demand, so a corrected price or policy simply changes the next read. Cost basis is `PurchasePrice * ExchangeRate`, reusing the multi-currency work, so a USD-invoiced asset depreciates against its peso cost.

**Tech Stack:** .NET 10, ASP.NET Core Web API, Blazor WebAssembly, EF Core + Npgsql, QuestPDF, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-12-depreciation-design.md`

## Global Constraints

- Build: `dotnet build`. Tests: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`. **Baseline is 280 passing, 0 failing.** Every task ends at 280 + the tests it added.
- **Stop any running API before `dotnet build`.** A running server holds a lock on the output DLLs and the build fails with MSB3027 "file is locked by AssetDesk.Api", which reads like a code error and is not.
- **Straight-line only. Do NOT add a `Method` column.** `Ticket.DueAt`/`BreachedAt` are the precedent: added early so the columns would exist, never populated, still displayed, overdue count permanently zero. A second method is an additive migration when it is actually built.
- **Nothing is stored per period.** No monthly entry rows, no period close, no journal export. Book value is always computed.
- **Cost basis is `PurchasePrice * ExchangeRate`, never the raw price.** Every money figure this feature produces is in pesos.
- **The calculator is pure — no `DbContext`, no `DateTime.UtcNow` inside it.** The as-of date is a parameter. This is what makes it exhaustively testable.
- Full-month convention: the purchase month counts as month one, hence the `+ 1` in elapsed months.
- **Rounding happens at presentation, never inside the calculation.** Intermediate rounding over 36 months compounds into a visible discrepancy against cost.
- `Retired` and `Lost` assets are excluded from the report, matching the three existing exclusions in `ReportsController`.
- Tests run on in-memory SQLite via `TestDb.Create()`, which calls `EnsureCreated()` and applies `HasData`. A green suite does **not** prove a migration applies on PostgreSQL — read the generated SQL by hand.
- There is **no test project for the Blazor assembly**. Tasks 7 and 8 are verified by `dotnet build` and by running the app. Never claim test coverage for Razor changes.
- Commit messages follow the repo's conventional-commit style and end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`

## File Structure

**Create:**
- `src/AssetDesk.Api/Entities/DepreciationPolicy.cs` — the policy row
- `src/AssetDesk.Api/Services/DepreciationCalculator.cs` — the pure calculation and its result type
- `src/AssetDesk.Api/Controllers/DepreciationPoliciesController.cs` — policy CRUD
- `src/AssetDesk.Api/Migrations/<ts>_AddDepreciationPolicy.cs` — table
- `src/AssetDesk.Api/Migrations/<ts>_GrantDepreciationManagePermission.cs` — permission backfill
- `src/AssetDesk.Shared/DTOs/DepreciationDto.cs` — policy and report DTOs
- `src/AssetDesk.Web/Pages/Admin/Depreciation.razor` — policy screen
- `tests/AssetDesk.Api.Tests/DepreciationCalculatorTests.cs`
- `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs`
- `tests/AssetDesk.Api.Tests/DepreciationReportTests.cs`

**Modify:**
- `src/AssetDesk.Api/Data/AppDbContext.cs` — `DbSet`, config, tenant query filter
- `src/AssetDesk.Api/Authorization/Permissions.cs` — the new key and its descriptor
- `src/AssetDesk.Api/Program.cs` — the `CanManageDepreciation` policy
- `src/AssetDesk.Api/Controllers/ReportsController.cs` — three new endpoints
- `src/AssetDesk.Api/Services/PdfReportService.cs` — the PDF builder
- `src/AssetDesk.Web/Services/ApiClient.cs` — client methods
- `src/AssetDesk.Web/Pages/Reports.razor` — the depreciation report tab
- `src/AssetDesk.Web/Pages/Assets/View.razor` — book value on the asset detail

---

### Task 1: The DepreciationPolicy table

**Files:**
- Create: `src/AssetDesk.Api/Entities/DepreciationPolicy.cs`
- Modify: `src/AssetDesk.Api/Data/AppDbContext.cs`
- Test: `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs` (created here, extended in Task 4)

**Interfaces:**
- Produces: `DepreciationPolicy` with `Id`, `TenantId`, `DeviceType`, `UsefulLifeMonths`, `ResidualPercent`, `CreatedAt`, `UpdatedAt`; `AppDbContext.DepreciationPolicies`. Tasks 2, 4 and 5 all consume it.

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The policy is per device type per organisation. It cannot live on the shared Currency-style
/// LookupValue rows, which carry no TenantId by deliberate design - useful life is each
/// organisation's own accounting policy, not shared vocabulary.
/// </summary>
public class DepreciationPolicyApiTests
{
    [Fact]
    public async Task A_policy_is_scoped_to_its_tenant_and_stamped_automatically()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                DeviceType = DeviceTypes.Laptop,
                UsefulLifeMonths = 36,
                ResidualPercent = 10m
            });
            await db.SaveChangesAsync();

            var saved = await db.DepreciationPolicies.SingleAsync();
            Assert.Equal(tenantId, saved.TenantId);
            Assert.Equal(DeviceTypes.Laptop, saved.DeviceType);
            Assert.Equal(36, saved.UsefulLifeMonths);
            Assert.Equal(10m, saved.ResidualPercent);
        }
    }

    [Fact]
    public async Task Two_tenants_may_each_have_their_own_policy_for_the_same_device_type()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);

            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantA, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });
            db.DepreciationPolicies.Add(new DepreciationPolicy
            {
                TenantId = tenantB, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 48, ResidualPercent = 0m
            });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.DepreciationPolicies.CountAsync());
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationPolicyApiTests"`

Expected: FAIL to compile — `The type or namespace name 'DepreciationPolicy' could not be found`.

- [ ] **Step 3: Create the entity**

Create `src/AssetDesk.Api/Entities/DepreciationPolicy.cs`:

```csharp
namespace AssetDesk.Api.Entities;

/// <summary>
/// How one device type depreciates, for one organisation. Straight-line is the only method, so
/// there is deliberately no Method column - see the plan's global constraints for why.
///
/// Tenant-scoped, and it has to be: LookupValue carries no TenantId because the owner wants one
/// shared vocabulary across every tenant, but useful life is each organisation's own accounting
/// policy rather than vocabulary.
/// </summary>
public class DepreciationPolicy : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Matches <see cref="Asset.DeviceType"/>. Not a foreign key: a device type is a lookup
    /// value stored as a raw string on the asset row, exactly as Asset.DeviceType stores it.
    /// </summary>
    public required string DeviceType { get; set; }

    /// <summary>Months, not years - the arithmetic is monthly and this avoids fractional years.</summary>
    public int UsefulLifeMonths { get; set; }

    /// <summary>
    /// Percent of cost basis retained at end of life, 0-100. A percentage rather than an amount
    /// because one policy row covers assets of very different cost.
    /// </summary>
    public decimal ResidualPercent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Add the DbSet**

In `src/AssetDesk.Api/Data/AppDbContext.cs`, beside the other `DbSet` declarations (they run from line 24):

```csharp
    public DbSet<DepreciationPolicy> DepreciationPolicies => Set<DepreciationPolicy>();
```

- [ ] **Step 5: Configure the entity**

In the same file, add a configuration block alongside the other `modelBuilder.Entity<...>` blocks:

```csharp
        modelBuilder.Entity<DepreciationPolicy>(entity =>
        {
            entity.HasKey(e => e.Id);

            // One policy per device type per tenant.
            entity.HasIndex(e => new { e.TenantId, e.DeviceType }).IsUnique();

            entity.Property(e => e.DeviceType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ResidualPercent).HasPrecision(5, 2);

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Global query filter for tenant isolation - same shape as every other tenant entity.
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });
```

`ResidualPercent` uses `HasPrecision(5, 2)` to match the file's existing style (`PurchasePrice` is `HasPrecision(18, 2)`, `ExchangeRate` is `HasPrecision(18, 6)`), which allows 0.00 to 999.99 — ample for a percentage.

`SaveChangesAsync` already stamps `TenantId` on every `ITenantEntity` (see the loop over `ChangeTracker.Entries<ITenantEntity>()`), so the first test's assertion about automatic stamping passes without extra work.

- [ ] **Step 6: Generate the migration**

Run:
```bash
cd src/AssetDesk.Api && dotnet ef migrations add AddDepreciationPolicy
```

Expected: a `CreateTable` for `DepreciationPolicies` plus a unique index on `(TenantId, DeviceType)`.

- [ ] **Step 7: Check the generated PostgreSQL by hand**

Run:
```bash
cd src/AssetDesk.Api && dotnet ef migrations script --idempotent | grep -A 12 "CREATE TABLE \"DepreciationPolicies\""
```

Expected: `"ResidualPercent" numeric(5,2) NOT NULL`, `"DeviceType" character varying(50) NOT NULL`, and a unique index over `TenantId, DeviceType`. The suite runs on SQLite and cannot prove this.

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 282 passing, 0 failing.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Entities/DepreciationPolicy.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs
git commit -m "feat(depreciation): add the per-device-type policy table

Tenant-scoped, because useful life is each organisation's own accounting
policy rather than shared vocabulary - which is why it cannot ride on the
LookupValue rows, those deliberately carry no TenantId.

Life in months rather than years: the arithmetic is monthly and months avoid
fractional years. Residual as a percent because one row covers assets of very
different cost. No Method column - straight-line is the only method and a
column nothing populates is the DueAt mistake.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: The calculator

**Files:**
- Create: `src/AssetDesk.Api/Services/DepreciationCalculator.cs`, `tests/AssetDesk.Api.Tests/DepreciationCalculatorTests.cs`

**Interfaces:**
- Consumes: `DepreciationPolicy` from Task 1.
- Produces:
  - `DepreciationResult` — a `readonly record struct` with `bool IsDepreciable`, `string? NotDepreciableReason`, `decimal CostBasis`, `decimal AccumulatedDepreciation`, `decimal NetBookValue`, `int ElapsedMonths`, `bool IsFullyDepreciated`
  - `DepreciationCalculator.Calculate(decimal? purchasePrice, decimal exchangeRate, DateTime? purchaseDate, DepreciationPolicy? policy, DateTime asOf) -> DepreciationResult`
  - `NotDepreciableReasons.NoPurchasePrice` / `.NoPurchaseDate` / `.NoPolicy` / `.InvalidUsefulLife`

  Task 5 calls `Calculate` once per asset and groups by `NotDepreciableReason`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/DepreciationCalculatorTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The calculator is a pure function, so it can be pinned exhaustively here and the report in
/// Task 5 becomes a mapping exercise. Every case below is arithmetic with no database.
/// </summary>
public class DepreciationCalculatorTests
{
    private static DepreciationPolicy Policy(int months, decimal residualPercent) => new()
    {
        DeviceType = DeviceTypes.Laptop,
        UsefulLifeMonths = months,
        ResidualPercent = residualPercent
    };

    // 36 months, 10% residual, cost 60,000 -> residual 6,000, depreciable 54,000, 1,500/month.
    private static DepreciationResult Standard(DateTime purchase, DateTime asOf) =>
        DepreciationCalculator.Calculate(60000m, 1m, purchase, Policy(36, 10m), asOf);

    [Fact]
    public void The_purchase_month_counts_as_month_one()
    {
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2026, 3, 31));

        Assert.True(r.IsDepreciable);
        Assert.Equal(1, r.ElapsedMonths);
        Assert.Equal(1500m, r.AccumulatedDepreciation);
        Assert.Equal(58500m, r.NetBookValue);
        Assert.False(r.IsFullyDepreciated);
    }

    [Fact]
    public void The_day_of_the_month_does_not_matter()
    {
        var first = Standard(new DateTime(2026, 3, 1), new DateTime(2026, 3, 2));
        var last = Standard(new DateTime(2026, 3, 31), new DateTime(2026, 3, 31));

        Assert.Equal(first.ElapsedMonths, last.ElapsedMonths);
        Assert.Equal(first.NetBookValue, last.NetBookValue);
    }

    [Fact]
    public void Mid_life_accumulates_one_month_at_a_time()
    {
        // March 2026 is month 1, so March 2027 is month 13.
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2027, 3, 5));

        Assert.Equal(13, r.ElapsedMonths);
        Assert.Equal(19500m, r.AccumulatedDepreciation);
        Assert.Equal(40500m, r.NetBookValue);
    }

    [Fact]
    public void The_final_month_lands_exactly_on_the_residual()
    {
        // Month 36 of a 36-month life bought March 2026 is February 2029.
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2029, 2, 28));

        Assert.Equal(36, r.ElapsedMonths);
        Assert.Equal(54000m, r.AccumulatedDepreciation);
        Assert.Equal(6000m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void Past_end_of_life_the_book_value_holds_at_the_residual()
    {
        var r = Standard(new DateTime(2026, 3, 20), new DateTime(2035, 1, 1));

        Assert.Equal(54000m, r.AccumulatedDepreciation);
        Assert.Equal(6000m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void With_no_residual_the_asset_reaches_exactly_zero()
    {
        var r = DepreciationCalculator.Calculate(
            36000m, 1m, new DateTime(2026, 1, 10), Policy(36, 0m), new DateTime(2028, 12, 31));

        Assert.Equal(36000m, r.AccumulatedDepreciation);
        Assert.Equal(0m, r.NetBookValue);
        Assert.True(r.IsFullyDepreciated);
    }

    [Fact]
    public void A_future_purchase_date_clamps_to_zero_rather_than_exceeding_cost()
    {
        var r = Standard(new DateTime(2027, 6, 1), new DateTime(2026, 3, 20));

        Assert.Equal(0, r.ElapsedMonths);
        Assert.Equal(0m, r.AccumulatedDepreciation);
        Assert.Equal(60000m, r.NetBookValue);
        Assert.False(r.IsFullyDepreciated);
    }

    [Fact]
    public void Cost_basis_goes_through_the_exchange_rate()
    {
        // USD 1,200 at 58.20 = 69,840 pesos. 24 months, no residual -> 2,910/month.
        var r = DepreciationCalculator.Calculate(
            1200m, 58.20m, new DateTime(2026, 1, 5), Policy(24, 0m), new DateTime(2026, 2, 1));

        Assert.Equal(69840m, r.CostBasis);
        Assert.Equal(2, r.ElapsedMonths);
        Assert.Equal(5820m, r.AccumulatedDepreciation);
        Assert.Equal(64020m, r.NetBookValue);
    }

    [Fact]
    public void No_purchase_price_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            null, 1m, new DateTime(2026, 3, 1), Policy(36, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPurchasePrice, r.NotDepreciableReason);
        Assert.Equal(0m, r.NetBookValue);
    }

    [Fact]
    public void No_purchase_date_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, null, Policy(36, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPurchaseDate, r.NotDepreciableReason);
    }

    [Fact]
    public void No_policy_is_not_depreciable()
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), null, new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.NoPolicy, r.NotDepreciableReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void A_non_positive_useful_life_is_reported_not_divided_by(int months)
    {
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), Policy(months, 10m), new DateTime(2026, 6, 1));

        Assert.False(r.IsDepreciable);
        Assert.Equal(NotDepreciableReasons.InvalidUsefulLife, r.NotDepreciableReason);
    }

    [Fact]
    public void A_not_depreciable_asset_still_reports_its_cost_basis()
    {
        // The report totals cost basis across every asset, depreciable or not, so it must be
        // present even when book value cannot be computed.
        var r = DepreciationCalculator.Calculate(
            60000m, 1m, new DateTime(2026, 3, 1), null, new DateTime(2026, 6, 1));

        Assert.Equal(60000m, r.CostBasis);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationCalculatorTests"`

Expected: FAIL to compile — `The name 'DepreciationCalculator' does not exist`.

- [ ] **Step 3: Write the calculator**

Create `src/AssetDesk.Api/Services/DepreciationCalculator.cs`:

```csharp
using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Services;

/// <summary>Why an asset has no book value. Grouped and counted by the depreciation report.</summary>
public static class NotDepreciableReasons
{
    public const string NoPurchasePrice = "No purchase price";
    public const string NoPurchaseDate = "No purchase date";
    public const string NoPolicy = "No depreciation policy for this device type";
    public const string InvalidUsefulLife = "Depreciation policy has a useful life of zero or less";
}

/// <param name="IsDepreciable">False when <paramref name="NotDepreciableReason"/> explains why not.</param>
/// <param name="CostBasis">Peso cost - purchase price times exchange rate. Populated even when not depreciable.</param>
public readonly record struct DepreciationResult(
    bool IsDepreciable,
    string? NotDepreciableReason,
    decimal CostBasis,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    int ElapsedMonths,
    bool IsFullyDepreciated);

/// <summary>
/// Straight-line depreciation, full-month convention. Deliberately pure: no DbContext and no
/// DateTime.UtcNow - the as-of date is a parameter, which is what lets every case be pinned by
/// arithmetic alone.
///
/// Rounding is left to the caller. Rounding here would compound over a 36-month life into a
/// visible discrepancy between accumulated depreciation and cost.
/// </summary>
public static class DepreciationCalculator
{
    public static DepreciationResult Calculate(
        decimal? purchasePrice,
        decimal exchangeRate,
        DateTime? purchaseDate,
        DepreciationPolicy? policy,
        DateTime asOf)
    {
        var costBasis = (purchasePrice ?? 0m) * exchangeRate;

        if (purchasePrice is null)
            return NotDepreciable(NotDepreciableReasons.NoPurchasePrice, costBasis);

        if (purchaseDate is null)
            return NotDepreciable(NotDepreciableReasons.NoPurchaseDate, costBasis);

        if (policy is null)
            return NotDepreciable(NotDepreciableReasons.NoPolicy, costBasis);

        // Second line of defence: the policy screen rejects this first. A pure function can be
        // called by anything, and a DivideByZeroException surfacing inside a report is a worse
        // failure than a row reported as undepreciable.
        if (policy.UsefulLifeMonths <= 0)
            return NotDepreciable(NotDepreciableReasons.InvalidUsefulLife, costBasis);

        var residual = costBasis * policy.ResidualPercent / 100m;
        var depreciable = costBasis - residual;
        var monthly = depreciable / policy.UsefulLifeMonths;

        // The + 1 is the full-month convention: the purchase month is month one, so an asset
        // bought on any day in March depreciates for the whole of March. Clamped at zero because
        // nothing stops an admin typing a future purchase date, and a negative elapsed count
        // would put book value above cost.
        var elapsed = Math.Max(0,
            (asOf.Year - purchaseDate.Value.Year) * 12
            + (asOf.Month - purchaseDate.Value.Month)
            + 1);

        var accumulated = Math.Min(monthly * elapsed, depreciable);

        return new DepreciationResult(
            IsDepreciable: true,
            NotDepreciableReason: null,
            CostBasis: costBasis,
            AccumulatedDepreciation: accumulated,
            NetBookValue: costBasis - accumulated,
            ElapsedMonths: elapsed,
            IsFullyDepreciated: elapsed >= policy.UsefulLifeMonths);
    }

    private static DepreciationResult NotDepreciable(string reason, decimal costBasis) =>
        new(IsDepreciable: false,
            NotDepreciableReason: reason,
            CostBasis: costBasis,
            AccumulatedDepreciation: 0m,
            NetBookValue: 0m,
            ElapsedMonths: 0,
            IsFullyDepreciated: false);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationCalculatorTests"`

Expected: PASS, 14 tests (12 facts plus the 2-case theory).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 296 passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Api/Services/DepreciationCalculator.cs tests/AssetDesk.Api.Tests/DepreciationCalculatorTests.cs
git commit -m "feat(depreciation): the straight-line calculation, as a pure function

No DbContext and no DateTime.UtcNow inside it - the as-of date is a parameter,
which is what lets the full-month convention, the residual floor and the
future-date clamp each be pinned by arithmetic alone.

Three details carry it. The + 1 makes the purchase month count as month one.
Math.Max(0, ...) stops a mistyped future purchase date putting book value above
cost. Math.Min(..., depreciable) holds the asset at its residual past end of
life rather than drifting to zero.

A non-positive useful life is reported as undepreciable rather than divided by:
validation rejects it first, but a pure function can be called by anything and
a DivideByZeroException inside a report is the worse failure.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: The permission and its backfill

**Files:**
- Modify: `src/AssetDesk.Api/Authorization/Permissions.cs`, `src/AssetDesk.Api/Program.cs`
- Create: `src/AssetDesk.Api/Migrations/<ts>_GrantDepreciationManagePermission.cs`

**Interfaces:**
- Produces: `Permissions.DepreciationManage` = `"iams:depreciation:manage"`; the authorization policy named `"CanManageDepreciation"`. Task 4 gates its controller on that policy name.

**Warning before you start:** `PermissionCatalogTests` and `RolePermissionSeedTests` already exist and may assert on the catalog. Adding a key can legitimately break them. If one fails, fix it so it tests the new intended catalog — never by deleting a case or weakening an assertion — and say exactly what you changed and why in your report.

- [ ] **Step 1: Write the failing test**

Append to `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs`:

```csharp
    [Fact]
    public void The_depreciation_key_is_in_the_catalog_under_its_own_group()
    {
        var descriptor = Assert.Single(
            AssetDesk.Api.Authorization.Permissions.All,
            p => p.Key == AssetDesk.Api.Authorization.Permissions.DepreciationManage);

        Assert.Equal("iams:depreciation:manage", descriptor.Key);
        Assert.Equal("Depreciation", descriptor.Group);
    }

    [Fact]
    public void Admin_and_SuperAdmin_get_the_depreciation_key_by_default()
    {
        Assert.Contains(
            AssetDesk.Api.Authorization.Permissions.DepreciationManage,
            AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.Admin));
        Assert.Contains(
            AssetDesk.Api.Authorization.Permissions.DepreciationManage,
            AssetDesk.Api.Authorization.Permissions.DefaultsFor(Roles.SuperAdmin));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationPolicyApiTests"`

Expected: FAIL to compile — `'Permissions' does not contain a definition for 'DepreciationManage'`.

- [ ] **Step 3: Add the key and its descriptor**

In `src/AssetDesk.Api/Authorization/Permissions.cs`, after the `AuditView` constant:

```csharp
    public const string DepreciationManage = "iams:depreciation:manage";
```

And as the last entry of the `All` array, after the `AuditView` descriptor:

```csharp
        new(DepreciationManage, "Depreciation", "Manage depreciation policy",
            "Set the useful life and residual value used to calculate book value, per device type."),
```

`DefaultsFor` returns `Array.AsReadOnly(Keys)` for both `Admin` and `SuperAdmin`, so both pick the key up with no further change — which is what the second test pins.

- [ ] **Step 4: Register the policy**

In `src/AssetDesk.Api/Program.cs`, beside the other `.RequirePermission(...)` calls (they run from line 123):

```csharp
    .RequirePermission("CanManageDepreciation", Permissions.DepreciationManage)
```

- [ ] **Step 5: Write the backfill migration**

Run:
```bash
cd src/AssetDesk.Api && dotnet ef migrations add GrantDepreciationManagePermission
```

Then replace the generated (empty) `Up`/`Down` bodies. This copies `20260906031321_GrantAuditViewPermission` deliberately — read that file alongside this step:

```csharp
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant
            // rows are the cross product of tenants and the built-in roles that should hold this
            // key - not a join on RoleId.
            //
            // The id comes from md5(...)::uuid rather than gen_random_uuid(): that function is
            // only a built-in from PostgreSQL 13, and the database here is supplied externally
            // with no version pinned. Being deterministic also makes the insert idempotent - the
            // NOT EXISTS guard and the id agree with each other on a re-run.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:depreciation:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:depreciation:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:depreciation:manage'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions"" WHERE ""Permission"" = 'iams:depreciation:manage';
            ");
        }
```

Note the role list is `('Admin', 'SuperAdmin')` — **not** the audit migration's three. `Auditor` is read-only oversight and setting accounting policy is a write, so it does not get this key.

Add the same class-level `<summary>` the audit migration carries, explaining that backfilling unconditionally is safe **only** because the key is brand new, so no tenant can have deliberately revoked it.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 298 passing, 0 failing. If `PermissionCatalogTests` or `RolePermissionSeedTests` fails, see the warning at the top of this task.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Authorization/Permissions.cs src/AssetDesk.Api/Program.cs src/AssetDesk.Api/Migrations/ tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs
git commit -m "feat(depreciation): add the policy permission and backfill it

EnsureRolePermissionsAsync is gated on RolePermissionsSeededAt and never
revisits a tenant, so a key added today reaches nobody provisioned yesterday.
Without the backfill every existing Admin would silently lack it and the new
screen would 403 for everyone.

Unconditional backfill is safe for one reason only: the key is brand new, so no
tenant can have deliberately revoked it. Admin and SuperAdmin only - Auditor is
read-only oversight and setting accounting policy is a write.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Policy CRUD

**Files:**
- Create: `src/AssetDesk.Api/Controllers/DepreciationPoliciesController.cs`, `src/AssetDesk.Shared/DTOs/DepreciationDto.cs`
- Test: `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs`

**Interfaces:**
- Consumes: `DepreciationPolicy` (Task 1), the `"CanManageDepreciation"` policy (Task 3).
- Produces: `DepreciationPolicyDto` (`Id`, `DeviceType`, `UsefulLifeMonths`, `ResidualPercent`) and `UpsertDepreciationPolicyDto` (`DeviceType`, `UsefulLifeMonths`, `ResidualPercent`) in `AssetDesk.Shared.DTOs`; endpoints `GET /api/depreciationpolicies`, `PUT /api/depreciationpolicies`, `DELETE /api/depreciationpolicies/{deviceType}`. Task 7 calls all three.

- [ ] **Step 1: Write the failing tests**

Append to `tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs`:

```csharp
    private static DepreciationPoliciesController ControllerFor(AssetDesk.Api.Data.AppDbContext db) =>
        new(db, new LookupService(db));

    [Fact]
    public async Task Upsert_creates_a_policy_then_updates_it_in_place()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
            });
            await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 48, ResidualPercent = 5m
            });

            var saved = Assert.Single(await db.DepreciationPolicies.ToListAsync());
            Assert.Equal(48, saved.UsefulLifeMonths);
            Assert.Equal(5m, saved.ResidualPercent);
            Assert.NotNull(saved.UpdatedAt);
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(36, -1)]
    [InlineData(36, 101)]
    public async Task Invalid_life_or_residual_is_rejected(int months, decimal residual)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            var result = await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = months, ResidualPercent = residual
            });

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await db.DepreciationPolicies.ToListAsync());
        }
    }

    [Fact]
    public async Task An_unknown_device_type_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);

            var result = await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = "Submarine", UsefulLifeMonths = 36, ResidualPercent = 0m
            });

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task Delete_removes_the_policy_for_that_device_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var controller = ControllerFor(db);
            await controller.Upsert(new UpsertDepreciationPolicyDto
            {
                DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 0m
            });

            await controller.Delete(DeviceTypes.Laptop);

            Assert.Empty(await db.DepreciationPolicies.ToListAsync());
        }
    }
```

Add `using AssetDesk.Api.Controllers;`, `using AssetDesk.Api.Services;`, `using AssetDesk.Shared.DTOs;` and `using Microsoft.AspNetCore.Mvc;` to the top of the file.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationPolicyApiTests"`

Expected: FAIL to compile — `The type or namespace name 'DepreciationPoliciesController' could not be found`.

- [ ] **Step 3: Create the DTOs**

Create `src/AssetDesk.Shared/DTOs/DepreciationDto.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record DepreciationPolicyDto
{
    public int Id { get; init; }
    public required string DeviceType { get; init; }
    public int UsefulLifeMonths { get; init; }
    public decimal ResidualPercent { get; init; }
}

public record UpsertDepreciationPolicyDto
{
    [Required]
    public required string DeviceType { get; init; }

    [Range(1, 1200, ErrorMessage = "Useful life must be between 1 and 1200 months")]
    public int UsefulLifeMonths { get; init; }

    [Range(0, 100, ErrorMessage = "Residual must be between 0 and 100 percent")]
    public decimal ResidualPercent { get; init; }
}
```

- [ ] **Step 4: Create the controller**

Create `src/AssetDesk.Api/Controllers/DepreciationPoliciesController.cs`:

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

/// <summary>
/// The per-device-type depreciation policy. Upsert rather than create/update: there is at most
/// one policy per device type per tenant, so the device type is the identity a caller knows and
/// a separate create-vs-update decision would only be a way to get it wrong.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageDepreciation")]
public class DepreciationPoliciesController(AppDbContext db, ILookupService lookups) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DepreciationPolicyDto>>>> GetAll()
    {
        var policies = await db.DepreciationPolicies
            .OrderBy(p => p.DeviceType)
            .Select(p => new DepreciationPolicyDto
            {
                Id = p.Id,
                DeviceType = p.DeviceType,
                UsefulLifeMonths = p.UsefulLifeMonths,
                ResidualPercent = p.ResidualPercent
            })
            .ToListAsync();

        return Ok(ApiResponse<List<DepreciationPolicyDto>>.Ok(policies));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<DepreciationPolicyDto>>> Upsert(UpsertDepreciationPolicyDto dto)
    {
        if (dto.UsefulLifeMonths <= 0)
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                "Useful life must be at least 1 month."));

        if (dto.ResidualPercent is < 0m or > 100m)
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                "Residual must be between 0 and 100 percent."));

        // Device types are admin-editable, so validate against the live lookup rather than the
        // DeviceTypes constant - the same call AssetsController makes.
        if (!await lookups.IsActiveValueAsync(LookupTypes.DeviceType, dto.DeviceType))
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                $"'{dto.DeviceType}' is not an active device type."));

        var policy = await db.DepreciationPolicies
            .FirstOrDefaultAsync(p => p.DeviceType == dto.DeviceType);

        if (policy is null)
        {
            policy = new DepreciationPolicy { DeviceType = dto.DeviceType };
            db.DepreciationPolicies.Add(policy);
        }
        else
        {
            policy.UpdatedAt = DateTime.UtcNow;
        }

        policy.UsefulLifeMonths = dto.UsefulLifeMonths;
        policy.ResidualPercent = dto.ResidualPercent;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<DepreciationPolicyDto>.Ok(new DepreciationPolicyDto
        {
            Id = policy.Id,
            DeviceType = policy.DeviceType,
            UsefulLifeMonths = policy.UsefulLifeMonths,
            ResidualPercent = policy.ResidualPercent
        }));
    }

    [HttpDelete("{deviceType}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(string deviceType)
    {
        var policy = await db.DepreciationPolicies
            .FirstOrDefaultAsync(p => p.DeviceType == deviceType);

        if (policy is null)
            return NotFound(ApiResponse<object>.Fail($"No depreciation policy for '{deviceType}'."));

        db.DepreciationPolicies.Remove(policy);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new object()));
    }
}
```

If `ApiResponse<T>.Ok`/`.Fail` have different names in this codebase, match what the other controllers use — read `AssetsController` and copy its exact style.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationPolicyApiTests"`

Expected: PASS.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 305 passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Controllers/DepreciationPoliciesController.cs src/AssetDesk.Shared/DTOs/DepreciationDto.cs tests/AssetDesk.Api.Tests/DepreciationPolicyApiTests.cs
git commit -m "feat(depreciation): manage the policy through the API

Upsert rather than create-then-update: there is at most one policy per device
type per tenant, so the device type is the identity a caller already knows and
a separate create-vs-update decision would only be a way to get it wrong.

The device type is validated against the live lookup rather than the
DeviceTypes constant, because device types are admin-editable - the same call
AssetsController already makes.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: The depreciation report

**Files:**
- Modify: `src/AssetDesk.Api/Controllers/ReportsController.cs`, `src/AssetDesk.Shared/DTOs/DepreciationDto.cs`
- Create: `tests/AssetDesk.Api.Tests/DepreciationReportTests.cs`

**Interfaces:**
- Consumes: `DepreciationCalculator.Calculate` (Task 2), `DepreciationPolicy` (Task 1).
- Produces: `DepreciationReportRow`, `NotDepreciableReasonCount`, `DepreciationSummaryDto`; `GET /api/reports/depreciation`. Task 6 reuses all three for the exports, Task 8 renders them.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/DepreciationReportTests.cs`:

```csharp
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The report's job is to be trustworthy about what it could not compute. An asset with no
/// price, no purchase date, or no policy for its device type is counted and named, never
/// silently folded in as zero.
/// </summary>
public class DepreciationReportTests
{
    private static async Task SeedAsync(AppDbContext db, Guid tenantId)
    {
        db.DepreciationPolicies.Add(new DepreciationPolicy
        {
            TenantId = tenantId, DeviceType = DeviceTypes.Laptop, UsefulLifeMonths = 36, ResidualPercent = 10m
        });

        // Depreciable: 60,000 pesos, bought 36 months before the as-of date, so fully depreciated
        // and holding at its 6,000 residual.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0001", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 60000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2023, 1, 15)
        });

        // Depreciable and USD: 1,200 at 58.20 = 69,840 pesos.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0002", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 1200m, Currency = Currencies.USD,
            ExchangeRate = 58.20m, PurchaseDate = new DateTime(2026, 1, 10)
        });

        // Not depreciable - no purchase date.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0003", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available, PurchasePrice = 40000m, Currency = Currencies.PHP,
            ExchangeRate = 1m
        });

        // Not depreciable - Monitor has no policy row.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "MON-0001", DeviceType = DeviceTypes.Monitor,
            Status = AssetStatus.Available, PurchasePrice = 15000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2025, 6, 1)
        });

        // Excluded entirely - Retired, matching the existing value report's exclusions.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId, AssetTag = "LAP-0099", DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Retired, PurchasePrice = 99000m, Currency = Currencies.PHP,
            ExchangeRate = 1m, PurchaseDate = new DateTime(2020, 1, 1)
        });

        await db.SaveChangesAsync();
    }

    private static DepreciationSummaryDto Unwrap(ActionResult<ApiResponse<DepreciationSummaryDto>> r) =>
        Assert.IsType<DepreciationSummaryDto>(
            Assert.IsType<ApiResponse<DepreciationSummaryDto>>(
                Assert.IsType<OkObjectResult>(r.Result).Value).Data);

    [Fact]
    public async Task Retired_assets_are_excluded_and_the_rest_are_reported()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            Assert.Equal(4, summary.Rows.Count);
            Assert.DoesNotContain(summary.Rows, r => r.AssetTag == "LAP-0099");
        }
    }

    [Fact]
    public async Task The_not_depreciable_assets_are_counted_and_named()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            Assert.Equal(2, summary.DepreciableCount);
            Assert.Equal(2, summary.NotDepreciableCount);

            Assert.Equal(1, Assert.Single(summary.NotDepreciableByReason,
                x => x.Reason == NotDepreciableReasons.NoPurchaseDate).Count);
            Assert.Equal(1, Assert.Single(summary.NotDepreciableByReason,
                x => x.Reason == NotDepreciableReasons.NoPolicy).Count);
        }
    }

    [Fact]
    public async Task Totals_are_in_pesos_and_only_depreciable_assets_carry_book_value()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            // Cost basis spans every reported asset: 60,000 + 69,840 + 40,000 + 15,000.
            Assert.Equal(184840m, summary.TotalCostBasis);

            // LAP-0001 bought Jan 2023 is month 38 of 36 - fully depreciated, holding at its
            // 6,000 residual, so 54,000 accumulated.
            // LAP-0002 bought Jan 2026, as-of Feb 2026, is month 2 of 36 on 69,840 with 10%
            // residual: (69,840 - 6,984) / 36 * 2 = 3,492.
            Assert.Equal(57492m, summary.TotalAccumulatedDepreciation);

            // Book value counts only the two depreciable assets: 6,000 + (69,840 - 3,492).
            Assert.Equal(72348m, summary.TotalNetBookValue);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);
        }
    }

    [Fact]
    public async Task A_fully_depreciated_asset_is_flagged_as_such()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var summary = Unwrap(await new ReportsController(db, null!)
                .GetDepreciationReport(new DateTime(2026, 2, 1)));

            var old = Assert.Single(summary.Rows, r => r.AssetTag == "LAP-0001");
            Assert.True(old.IsFullyDepreciated);
            Assert.Equal(6000m, old.NetBookValue);

            var newer = Assert.Single(summary.Rows, r => r.AssetTag == "LAP-0002");
            Assert.False(newer.IsFullyDepreciated);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationReportTests"`

Expected: FAIL to compile — `'ReportsController' does not contain a definition for 'GetDepreciationReport'`.

- [ ] **Step 3: Add the report DTOs**

Append to `src/AssetDesk.Shared/DTOs/DepreciationDto.cs`:

```csharp
public record DepreciationReportRow
{
    public required string AssetTag { get; init; }
    public required string DeviceType { get; init; }
    public string? Name { get; init; }
    public DateTime? PurchaseDate { get; init; }

    /// <summary>Peso cost - purchase price times exchange rate. Present even when not depreciable.</summary>
    public decimal CostBasis { get; init; }

    /// <summary>The currency the asset was invoiced in, so the row can show what was actually paid.</summary>
    public string? Currency { get; init; }
    public decimal? PurchasePrice { get; init; }

    public int? UsefulLifeMonths { get; init; }
    public int? ElapsedMonths { get; init; }
    public decimal? AccumulatedDepreciation { get; init; }
    public decimal? NetBookValue { get; init; }
    public bool IsDepreciable { get; init; }
    public bool IsFullyDepreciated { get; init; }
    public string? NotDepreciableReason { get; init; }
}

public record NotDepreciableReasonCount
{
    public required string Reason { get; init; }
    public int Count { get; init; }
}

public record DepreciationSummaryDto
{
    public decimal TotalCostBasis { get; init; }
    public decimal TotalAccumulatedDepreciation { get; init; }
    public decimal TotalNetBookValue { get; init; }
    public int DepreciableCount { get; init; }
    public int NotDepreciableCount { get; init; }
    public List<NotDepreciableReasonCount> NotDepreciableByReason { get; init; } = [];

    /// <summary>Always PHP. Every total this system produces is in pesos.</summary>
    public string PrimaryCurrency { get; init; } = "PHP";

    public DateTime AsOf { get; init; }
    public List<DepreciationReportRow> Rows { get; init; } = [];
}
```

- [ ] **Step 4: Add the endpoint**

In `src/AssetDesk.Api/Controllers/ReportsController.cs`, add a `using AssetDesk.Api.Services;` if absent, then:

```csharp
    /// <summary>
    /// Net book value across the estate, as of a date. Retired and Lost assets are excluded, and
    /// anything that cannot be depreciated is reported with the reason rather than folded in as
    /// zero - a total that quietly omits a fifth of the estate looks authoritative and is not.
    /// </summary>
    [HttpGet("depreciation")]
    public async Task<ActionResult<ApiResponse<DepreciationSummaryDto>>> GetDepreciationReport(
        [FromQuery] DateTime? asOf = null)
    {
        var effectiveAsOf = asOf ?? DateTime.UtcNow;

        var assets = await db.Assets
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .OrderBy(a => a.AssetTag)
            .Select(a => new
            {
                a.AssetTag,
                a.DeviceType,
                a.Name,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate,
                a.PurchaseDate
            })
            .ToListAsync();

        var policies = await db.DepreciationPolicies
            .ToDictionaryAsync(p => p.DeviceType);

        var rows = new List<DepreciationReportRow>(assets.Count);

        foreach (var a in assets)
        {
            policies.TryGetValue(a.DeviceType, out var policy);

            var d = DepreciationCalculator.Calculate(
                a.PurchasePrice, a.ExchangeRate, a.PurchaseDate, policy, effectiveAsOf);

            rows.Add(new DepreciationReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Name = a.Name,
                PurchaseDate = a.PurchaseDate,
                CostBasis = d.CostBasis,
                Currency = a.Currency,
                PurchasePrice = a.PurchasePrice,
                UsefulLifeMonths = policy?.UsefulLifeMonths,
                ElapsedMonths = d.IsDepreciable ? d.ElapsedMonths : null,
                AccumulatedDepreciation = d.IsDepreciable ? d.AccumulatedDepreciation : null,
                NetBookValue = d.IsDepreciable ? d.NetBookValue : null,
                IsDepreciable = d.IsDepreciable,
                IsFullyDepreciated = d.IsFullyDepreciated,
                NotDepreciableReason = d.NotDepreciableReason
            });
        }

        var summary = new DepreciationSummaryDto
        {
            // Cost basis spans every reported asset; book value only the depreciable ones.
            TotalCostBasis = rows.Sum(r => r.CostBasis),
            TotalAccumulatedDepreciation = rows.Sum(r => r.AccumulatedDepreciation ?? 0m),
            TotalNetBookValue = rows.Sum(r => r.NetBookValue ?? 0m),
            DepreciableCount = rows.Count(r => r.IsDepreciable),
            NotDepreciableCount = rows.Count(r => !r.IsDepreciable),
            NotDepreciableByReason = rows
                .Where(r => r.NotDepreciableReason is not null)
                .GroupBy(r => r.NotDepreciableReason!)
                .Select(g => new NotDepreciableReasonCount { Reason = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList(),
            PrimaryCurrency = Currencies.PHP,
            AsOf = effectiveAsOf,
            Rows = rows
        };

        return Ok(ApiResponse<DepreciationSummaryDto>.Ok(summary));
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~DepreciationReportTests"`

Expected: PASS, 4 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 309 passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Controllers/ReportsController.cs src/AssetDesk.Shared/DTOs/DepreciationDto.cs tests/AssetDesk.Api.Tests/DepreciationReportTests.cs
git commit -m "feat(depreciation): report net book value across the estate

Cost basis spans every reported asset; book value only the ones that can
actually be depreciated. The difference is the point: assets missing a price, a
purchase date, or a policy for their device type are counted and named rather
than folded in as zero, because a total that quietly omits a fifth of the
estate looks authoritative and is not.

Retired and Lost are excluded, matching the existing value report.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: CSV and PDF exports

**Files:**
- Modify: `src/AssetDesk.Api/Controllers/ReportsController.cs`, `src/AssetDesk.Api/Services/PdfReportService.cs`
- Test: `tests/AssetDesk.Api.Tests/DepreciationReportTests.cs`

**Interfaces:**
- Consumes: `DepreciationSummaryDto` (Task 5).
- Produces: `GET /api/reports/depreciation/export`, `GET /api/reports/depreciation/pdf`, and `IPdfReportService.BuildDepreciationPdf(DepreciationSummaryDto summary) -> byte[]`.

Each of the four existing reports has a JSON, an `/export` and a `/pdf` endpoint — a fifth without them would be the odd one out.

- [ ] **Step 1: Write the failing test**

Append to `tests/AssetDesk.Api.Tests/DepreciationReportTests.cs`:

```csharp
    [Fact]
    public async Task The_CSV_export_names_the_undepreciable_assets_too()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedAsync(db, tenantId);

            var file = Assert.IsType<FileContentResult>(
                await new ReportsController(db, null!).ExportDepreciationReport(new DateTime(2026, 2, 1)));

            var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

            Assert.Contains("LAP-0001", csv);
            Assert.Contains("MON-0001", csv);
            Assert.Contains(NotDepreciableReasons.NoPolicy, csv);
            Assert.DoesNotContain("LAP-0099", csv);   // Retired, excluded
            Assert.Equal("text/csv", file.ContentType);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~The_CSV_export_names"`

Expected: FAIL to compile — `'ReportsController' does not contain a definition for 'ExportDepreciationReport'`.

- [ ] **Step 3: Extract the shared build, then add the CSV endpoint**

`GetDepreciationReport` and both exports need the same summary. Extract the body of Task 5's endpoint into a private method and have all three call it:

```csharp
    private async Task<DepreciationSummaryDto> BuildDepreciationSummaryAsync(DateTime asOf)
    {
        // ...the body of GetDepreciationReport from Task 5, returning `summary`...
    }
```

`GetDepreciationReport` becomes:

```csharp
    [HttpGet("depreciation")]
    public async Task<ActionResult<ApiResponse<DepreciationSummaryDto>>> GetDepreciationReport(
        [FromQuery] DateTime? asOf = null) =>
        Ok(ApiResponse<DepreciationSummaryDto>.Ok(
            await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow)));
```

Then the CSV endpoint:

```csharp
    [HttpGet("depreciation/export")]
    public async Task<IActionResult> ExportDepreciationReport([FromQuery] DateTime? asOf = null)
    {
        var summary = await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow);

        var sb = new StringBuilder();
        sb.AppendLine("Asset Tag,Device Type,Name,Purchase Date,Purchase Price,Currency,Cost Basis (PHP),Useful Life (months),Elapsed Months,Accumulated Depreciation (PHP),Net Book Value (PHP),Status");

        foreach (var r in summary.Rows)
        {
            var status = r.IsDepreciable
                ? (r.IsFullyDepreciated ? "Fully depreciated" : "Depreciating")
                : r.NotDepreciableReason;

            sb.AppendLine(string.Join(',',
                Csv(r.AssetTag),
                Csv(r.DeviceType),
                Csv(r.Name),
                r.PurchaseDate?.ToString("yyyy-MM-dd") ?? "",
                r.PurchasePrice?.ToString("F2") ?? "",
                Csv(r.Currency),
                r.CostBasis.ToString("F2"),
                r.UsefulLifeMonths?.ToString() ?? "",
                r.ElapsedMonths?.ToString() ?? "",
                r.AccumulatedDepreciation?.ToString("F2") ?? "",
                r.NetBookValue?.ToString("F2") ?? "",
                Csv(status)));
        }

        var fileName = $"Depreciation {summary.AsOf:yyyy-MM-dd}.csv";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
    }

    /// <summary>Quotes a CSV field only when it needs it, and doubles any embedded quote.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
```

If `ReportsController` already has an equivalent CSV-quoting helper, use that one instead of adding a second — check before writing `Csv`.

- [ ] **Step 4: Add the PDF builder**

In `src/AssetDesk.Api/Services/PdfReportService.cs`, add to the `IPdfReportService` interface:

```csharp
    byte[] BuildDepreciationPdf(DepreciationSummaryDto summary);
```

And the implementation, following the shape of `BuildAssetValuePdf`:

```csharp
    public byte[] BuildDepreciationPdf(DepreciationSummaryDto summary)
    {
        var filters = BuildFilterLine(("As of", summary.AsOf.ToString("yyyy-MM-dd")));

        return BuildDocument("Depreciation Report", filters, summary.Rows.Count, content =>
        {
            content.Column(col =>
            {
                col.Item().Text(
                    $"Cost basis {FormatCurrency(summary.TotalCostBasis, summary.PrimaryCurrency)}   •   " +
                    $"Accumulated {FormatCurrency(summary.TotalAccumulatedDepreciation, summary.PrimaryCurrency)}   •   " +
                    $"Net book value {FormatCurrency(summary.TotalNetBookValue, summary.PrimaryCurrency)}");

                if (summary.NotDepreciableCount > 0)
                {
                    var reasons = string.Join("; ",
                        summary.NotDepreciableByReason.Select(x => $"{x.Count} {x.Reason.ToLowerInvariant()}"));
                    col.Item().PaddingTop(4).Text(
                        $"{summary.NotDepreciableCount} of {summary.Rows.Count} assets not depreciable — {reasons}");
                }

                col.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2);   // asset tag
                        c.RelativeColumn(2);   // device type
                        c.RelativeColumn(2);   // cost basis
                        c.RelativeColumn(2);   // accumulated
                        c.RelativeColumn(2);   // net book value
                        c.RelativeColumn(3);   // status
                    });

                    // AddHeaderRow takes every header at once - there is no AddHeaderCell.
                    AddHeaderRow(table,
                        "Asset Tag", "Device Type", "Cost Basis",
                        "Accumulated", "Net Book Value", "Status");

                    foreach (var row in summary.Rows)
                    {
                        AddBodyCell(table, row.AssetTag);
                        AddBodyCell(table, row.DeviceType);
                        AddBodyCell(table, FormatCurrency(row.CostBasis, summary.PrimaryCurrency));
                        AddBodyCell(table, FormatCurrency(row.AccumulatedDepreciation, summary.PrimaryCurrency));
                        AddBodyCell(table, FormatCurrency(row.NetBookValue, summary.PrimaryCurrency));
                        AddBodyCell(table, row.IsDepreciable
                            ? (row.IsFullyDepreciated ? "Fully depreciated" : "Depreciating")
                            : row.NotDepreciableReason ?? "");
                    }
                });
            });
        });
    }
```

`AddHeaderRow` and `AddBodyCell` already exist in that file — read `BuildAssetValuePdf` and match its exact helper names and call style rather than assuming these.

- [ ] **Step 5: Add the PDF endpoint**

```csharp
    [HttpGet("depreciation/pdf")]
    public async Task<IActionResult> ExportDepreciationPdf([FromQuery] DateTime? asOf = null)
    {
        var summary = await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow);
        var bytes = pdf.BuildDepreciationPdf(summary);
        var fileName = $"Depreciation {summary.AsOf:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 310 passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Controllers/ReportsController.cs src/AssetDesk.Api/Services/PdfReportService.cs tests/AssetDesk.Api.Tests/DepreciationReportTests.cs
git commit -m "feat(depreciation): CSV and PDF exports for the report

Every other report here has all three forms, so a fifth without them would be
the odd one out. The three endpoints share one BuildDepreciationSummaryAsync
rather than each rebuilding the projection - the currency work shipped the same
aggregation four times and two of the copies were missed.

Both exports carry the undepreciable rows and their reason, so an exported
total can be reconciled against what it excluded.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: The policy admin screen

**Files:**
- Create: `src/AssetDesk.Web/Pages/Admin/Depreciation.razor`
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs`

**Interfaces:**
- Consumes: `GET/PUT/DELETE /api/depreciationpolicies` (Task 4), `DepreciationPolicyDto`, `UpsertDepreciationPolicyDto`.

There is no Blazor test project. Verify with `dotnet build` and by reading. Do not claim test coverage.

- [ ] **Step 1: Add the ApiClient methods**

In `src/AssetDesk.Web/Services/ApiClient.cs`, following the exact style of the neighbouring methods (read two of them first — error handling here is a house pattern, not a free choice):

```csharp
    public async Task<List<DepreciationPolicyDto>> GetDepreciationPoliciesAsync()
    public async Task<(bool Success, string? Error)> SaveDepreciationPolicyAsync(UpsertDepreciationPolicyDto dto)
    public async Task<(bool Success, string? Error)> DeleteDepreciationPolicyAsync(string deviceType)
```

Implement each with the same request/response and error handling the surrounding methods use.

- [ ] **Step 2: Build the page**

Create `src/AssetDesk.Web/Pages/Admin/Depreciation.razor` at route `/admin/depreciation`, following `src/AssetDesk.Web/Pages/Admin/Lookups.razor` for structure, layout and component usage.

The page lists **every active device type**, not only the ones carrying a policy — an undepreciated type should read as a gap to close, not an absence nobody notices. The core of it:

```razor
@code {
    private List<string> _deviceTypes = [];
    private Dictionary<string, DepreciationPolicyDto> _policies = new();
    private string? _error;
    private string? _savingType;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _deviceTypes = await Api.GetDeviceTypesAsync();
        var policies = await Api.GetDepreciationPoliciesAsync();
        _policies = policies.ToDictionary(p => p.DeviceType);
    }

    private int MonthsFor(string deviceType) =>
        _policies.TryGetValue(deviceType, out var p) ? p.UsefulLifeMonths : 0;

    private decimal ResidualFor(string deviceType) =>
        _policies.TryGetValue(deviceType, out var p) ? p.ResidualPercent : 0m;

    private async Task SaveAsync(string deviceType, int months, decimal residual)
    {
        _error = null;
        _savingType = deviceType;

        var (success, error) = await Api.SaveDepreciationPolicyAsync(new UpsertDepreciationPolicyDto
        {
            DeviceType = deviceType,
            UsefulLifeMonths = months,
            ResidualPercent = residual
        });

        _savingType = null;

        // Surface the API's message rather than inventing one - it is the authority on the rules.
        if (!success) { _error = error; return; }
        await LoadAsync();
    }

    private async Task RemoveAsync(string deviceType)
    {
        _error = null;
        var (success, error) = await Api.DeleteDepreciationPolicyAsync(deviceType);
        if (!success) { _error = error; return; }
        await LoadAsync();
    }
}
```

And one row per device type, showing the policy or the gap:

```razor
@foreach (var deviceType in _deviceTypes)
{
    var hasPolicy = _policies.ContainsKey(deviceType);
    <TableRow>
        <TableCell Class="font-medium">@deviceType</TableCell>
        <TableCell>
            @if (hasPolicy)
            {
                <span>@MonthsFor(deviceType) months</span>
            }
            else
            {
                <span class="text-muted-foreground">Not depreciated</span>
            }
        </TableCell>
        <TableCell>@(hasPolicy ? $"{ResidualFor(deviceType):0.##}%" : "—")</TableCell>
        <TableCell Class="text-right">
            <Button Size="sm" Variant="outline" OnClick="() => OpenEditor(deviceType)">
                @(hasPolicy ? "Edit" : "Set policy")
            </Button>
            @if (hasPolicy)
            {
                <Button Size="sm" Variant="ghost" OnClick="() => RemoveAsync(deviceType)">Remove</Button>
            }
        </TableCell>
    </TableRow>
}
```

Wire `OpenEditor` to whatever modal or inline-edit pattern `Lookups.razor` already uses — do not invent a third. Gate the page with `<PermissionView Permission="iams:depreciation:manage">`, matching the other admin pages, and render `_error` wherever those pages render theirs.

If `Api.GetDeviceTypesAsync()` does not exist under that exact name, use whatever method already fetches active device types for the asset form — do not add a second one.

Reuse only Tailwind classes already present in the project. If you introduce a new one — especially an arbitrary value like `w-[130px]` — you must run `npm --prefix src/AssetDesk.Web run build:css` and bump both the `?v=` on the stylesheet link in `wwwroot/index.html` and `cacheName` in `service-worker.published.js`, because `wwwroot/css/app.css` is a committed artifact that `dotnet build` does not regenerate.

- [ ] **Step 3: Add the nav entry**

Add `/admin/depreciation` to the admin navigation beside the other admin links, gated on the same permission. Find the nav component the existing admin links use and follow it exactly.

- [ ] **Step 4: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run the suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: still 310 passing, 0 failing. A change here should not move it; if it does, something is wrong.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Web/
git commit -m "feat(depreciation): manage the policy from the admin area

Lists every active device type, not only the ones with a policy, so an
undepreciated type is visible as a gap to close rather than an absence nobody
notices.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Book value on the reports page and the asset detail

**Files:**
- Modify: `src/AssetDesk.Web/Pages/Reports.razor`, `src/AssetDesk.Web/Pages/Assets/View.razor`, `src/AssetDesk.Web/Services/ApiClient.cs`

**Interfaces:**
- Consumes: `GET /api/reports/depreciation` and its two exports (Tasks 5 and 6), `DepreciationSummaryDto`.

No Blazor test project. Verify with `dotnet build` and by reading.

- [ ] **Step 1: Add the ApiClient method**

In `src/AssetDesk.Web/Services/ApiClient.cs`:

```csharp
    public async Task<DepreciationSummaryDto?> GetDepreciationReportAsync(DateTime? asOf = null)
```

Follow the style of the existing report methods in the same file.

- [ ] **Step 2: Add the report to Reports.razor**

Add a depreciation section beside the existing four reports, following exactly how they are structured — the same tab or card pattern, the same CSV and PDF download buttons wired to `/api/reports/depreciation/export` and `/api/reports/depreciation/pdf` through the existing `downloadWithAuth` helper.

Render:
- the three totals: cost basis, accumulated depreciation, net book value
- **the not-depreciable line whenever `NotDepreciableCount > 0`**, reading like: `12 of 60 assets not depreciable — 9 no purchase date, 3 no depreciation policy for this device type`. This is the point of the feature's honesty and must not be dropped for space.
- a row per asset: tag, device type, cost basis, accumulated, net book value, and either its depreciation state or the reason it has none

Format every money figure with `CurrencyFormat.Format(value, summary.PrimaryCurrency)`. Do **not** write a peso sign as a literal or as the escape `₱` — the multi-currency work removed all of those, and three escaped ones hid from a grep for the literal character and shipped a bug.

- [ ] **Step 3: Add book value to the asset detail page**

In `src/AssetDesk.Web/Pages/Assets/View.razor`, add a book-value line to the purchase information area.

There is deliberately no per-asset depreciation endpoint — do **not** add one, and **never compute depreciation in the Blazor client**, because a second implementation of the calculation would drift from the server's. Instead call the report and take this asset's row:

```csharp
    private DepreciationReportRow? _depreciation;

    private async Task LoadDepreciationAsync()
    {
        if (_asset is null) return;

        // The report returns every asset; we want one row. Wasteful for a large estate, and
        // deliberately accepted rather than adding a second calculation path that could drift
        // from DepreciationCalculator. If it becomes a problem, the fix is an assetTag filter
        // on the existing endpoint, not a client-side formula.
        var summary = await Api.GetDepreciationReportAsync();
        _depreciation = summary?.Rows.FirstOrDefault(r => r.AssetTag == _asset.AssetTag);
    }
```

Call it after the asset loads. Render:

```razor
@if (_depreciation is not null)
{
    <div class="flex justify-between text-sm">
        <span class="text-muted-foreground">Book value</span>
        @if (_depreciation.IsDepreciable)
        {
            <span class="font-medium text-foreground">
                @CurrencyFormat.Format(_depreciation.NetBookValue, "PHP")
                @if (_depreciation.IsFullyDepreciated)
                {
                    <span class="text-muted-foreground">(fully depreciated)</span>
                }
            </span>
        }
        else
        {
            <span class="text-muted-foreground">@_depreciation.NotDepreciableReason</span>
        }
    </div>
}
```

Match the surrounding rows' markup in that file rather than this snippet's classes if they differ — the point is the branching, not the styling.

- [ ] **Step 4: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Verify no peso literal crept in**

Run: `grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src`

Expected: exactly two hits — `CurrencyFormat.cs` (the definition) and `EstateDashboard.razor` (peso totals). Anything else is a bug.

- [ ] **Step 6: Run the suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: still 310 passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Web/
git commit -m "feat(depreciation): show book value in reports and on the asset

The not-depreciable line is rendered whenever there is one. A book-value total
that silently omits the assets it could not compute looks authoritative and is
not, so the gap is shown beside the number with its reasons.

Book value is read from the server's calculation, never recomputed in the
client - a second implementation would drift from the first.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Done When

- `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj` reports **310 passing, 0 failing**.
- `dotnet build` succeeds with 0 errors.
- `dotnet ef migrations script --idempotent | grep -A 12 'CREATE TABLE "DepreciationPolicies"'` shows `numeric(5,2)` for `ResidualPercent` and a unique index over `(TenantId, DeviceType)`.
- `grep -rn "u20B1\|u20b1\|₱" --include=*.cs --include=*.razor src` returns only `CurrencyFormat.cs` and `EstateDashboard.razor`.
- No `Method` column exists on `DepreciationPolicy`.
- The depreciation report renders its not-depreciable count and reasons whenever any asset cannot be depreciated.
