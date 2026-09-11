# Multi-Currency Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an asset be recorded in the currency it was actually invoiced in (PHP or USD) with the exchange rate that was booked, while every report total stays in pesos.

**Architecture:** `Asset` gains a single `ExchangeRate` column; the peso value stays derived as `PurchasePrice * ExchangeRate` rather than stored, so it cannot drift from the rate beside it. Currency validation is already data-driven through `LookupService.IsActiveValueAsync`, so activating the dormant `USD` lookup row is what enables the currency — no validation code changes to *permit* it. A new `CurrencyFormat` helper in `AssetDesk.Shared` replaces the four hardcoded peso signs, because `AssetDesk.Web` cannot see `Currencies` (it lives in the API project).

**Tech Stack:** .NET 10, ASP.NET Core Web API, Blazor WebAssembly, EF Core + Npgsql, ClosedXML, QuestPDF, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-11-multi-currency-design.md`

## Global Constraints

- Build: `dotnet build`. Tests: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`. **Baseline is 246 passing, 0 failing.** Every task must end at 246 + the tests it added.
- **Stop any running API before `dotnet build`.** A running server holds a lock on the output DLLs and the build fails with MSB3027 "file is locked by AssetDesk.Api", which reads like a code error and is not.
- **`CurrencyFormat` goes in `AssetDesk.Shared`, never in `AssetDesk.Api`.** `AssetDesk.Web` references only `AssetDesk.Shared`; it cannot see `Currencies`, which is why `Reports.razor` hardcodes `₱` today.
- **Only `PHP` and `USD` are activated.** `EUR`, `GBP`, `JPY`, `CAD`, `AUD` rows stay `IsActive = false`. Do not "finish the set" — `JPY` is zero-decimal and needs its own formatting path.
- **`Currency` stays in `LookupTypes.Locked`.** Do not move it to `Editable`.
- **Never add `ExchangeRate` to `AssetImportService.ExpectedHeaders`.** Headers listed there are required and the service fails the whole file on a missing one; adding it would reject every spreadsheet built against the current template.
- Rate rules, applied identically in the controller and the importer: `> 0`; exactly `1` when currency is `PHP`; required when currency is not `PHP`.
- There is **no test project for the Blazor assembly**. Task 6 is verified by `dotnet build` and by running the app. Never claim test coverage for Razor changes.
- Tests run on in-memory SQLite via `TestDb.Create()`, which calls `EnsureCreated()`. EF applies `HasData` seed rows during schema creation, so test databases carry the same `LookupValues` rows as production.
- Commit messages follow the repo's conventional-commit style and end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`

## File Structure

**Create:**
- `src/AssetDesk.Shared/CurrencyFormat.cs` — symbol lookup and amount formatting, shared by API and Web
- `src/AssetDesk.Api/Migrations/<timestamp>_AddExchangeRate.cs` — column + USD activation
- `tests/AssetDesk.Api.Tests/CurrencyFormatTests.cs`
- `tests/AssetDesk.Api.Tests/AssetCurrencyValidationTests.cs`
- `tests/AssetDesk.Api.Tests/AssetImportCurrencyTests.cs`
- `tests/AssetDesk.Api.Tests/MixedCurrencyReportTests.cs`

**Modify:**
- `src/AssetDesk.Api/Entities/Asset.cs` — `Currencies.All`/`Retired`, drop `Symbol`, add `Asset.ExchangeRate`
- `src/AssetDesk.Api/Entities/LookupValue.cs` — `LockedReason(Currency)` text
- `src/AssetDesk.Api/Data/LookupValueSeed.cs:39` — USD row becomes active
- `src/AssetDesk.Shared/DTOs/AssetDto.cs` — `ExchangeRate` on three DTOs
- `src/AssetDesk.Api/Controllers/AssetsController.cs` — rate validation, persist and map the field
- `src/AssetDesk.Api/Services/AssetImportService.cs` — optional rate column
- `src/AssetDesk.Api/Controllers/ReportsController.cs` — four sums become rate-aware
- `src/AssetDesk.Api/Controllers/DashboardController.cs` — two sums become rate-aware
- `src/AssetDesk.Api/Services/PdfReportService.cs:296-300` — consult the currency code
- `src/AssetDesk.Web/Pages/Reports.razor:546-547` — use `CurrencyFormat`
- `src/AssetDesk.Web/Pages/Assets/Edit.razor:176` — currency selector + conditional rate input

---

### Task 1: Shared currency formatting and the two-currency vocabulary

**Files:**
- Create: `src/AssetDesk.Shared/CurrencyFormat.cs`, `tests/AssetDesk.Api.Tests/CurrencyFormatTests.cs`
- Modify: `src/AssetDesk.Api/Entities/Asset.cs`, `src/AssetDesk.Api/Entities/LookupValue.cs`, `src/AssetDesk.Api/Data/LookupValueSeed.cs:39`

**Interfaces:**
- Produces: `AssetDesk.Shared.CurrencyFormat.SymbolFor(string? code) -> string`, `CurrencyFormat.Format(decimal? amount, string? code) -> string`, `CurrencyFormat.Php`/`Usd` constants. Task 5 and Task 6 both call `Format`.

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/CurrencyFormatTests.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The peso sign used to be a constant every call site pasted in. These cover the thing that
/// replaced it, plus the two vocabulary changes that decide which currencies exist at all.
/// </summary>
public class CurrencyFormatTests
{
    [Fact]
    public void Peso_is_the_symbol_for_PHP_and_the_fallback_for_anything_unknown()
    {
        Assert.Equal("₱", CurrencyFormat.SymbolFor(CurrencyFormat.Php));
        Assert.Equal("₱", CurrencyFormat.SymbolFor(null));
        Assert.Equal("₱", CurrencyFormat.SymbolFor("ZZZ"));
    }

    [Fact]
    public void Dollar_is_the_symbol_for_USD()
    {
        Assert.Equal("$", CurrencyFormat.SymbolFor(CurrencyFormat.Usd));
    }

    [Fact]
    public void Amounts_render_with_two_decimals_and_thousands_separators()
    {
        Assert.Equal("₱1,234.50", CurrencyFormat.Format(1234.5m, CurrencyFormat.Php));
        Assert.Equal("$1,200.00", CurrencyFormat.Format(1200m, CurrencyFormat.Usd));
    }

    [Fact]
    public void A_null_amount_renders_as_an_em_dash()
    {
        Assert.Equal("—", CurrencyFormat.Format(null, CurrencyFormat.Php));
    }

    [Fact]
    public void USD_joins_PHP_as_a_supported_currency_and_leaves_the_retired_list()
    {
        Assert.Equal([Currencies.PHP, Currencies.USD], Currencies.All);
        Assert.DoesNotContain(Currencies.USD, Currencies.Retired);
        Assert.Contains(Currencies.EUR, Currencies.Retired);
    }

    [Fact]
    public void The_seeded_USD_lookup_row_is_active_and_the_other_five_are_not()
    {
        var currencyRows = LookupValueSeed.Rows
            .Where(r => r.LookupType == LookupTypes.Currency)
            .ToDictionary(r => r.Value, r => r.IsActive);

        Assert.True(currencyRows[Currencies.PHP]);
        Assert.True(currencyRows[Currencies.USD]);
        Assert.False(currencyRows[Currencies.EUR]);
        Assert.False(currencyRows[Currencies.GBP]);
        Assert.False(currencyRows[Currencies.JPY]);
        Assert.False(currencyRows[Currencies.CAD]);
        Assert.False(currencyRows[Currencies.AUD]);
    }

    [Fact]
    public void The_locked_reason_no_longer_claims_the_app_is_peso_only()
    {
        var reason = LookupTypes.LockedReason(LookupTypes.Currency);

        Assert.NotNull(reason);
        Assert.DoesNotContain("peso-only", reason);
        Assert.Contains("symbol", reason);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~CurrencyFormatTests"`

Expected: FAIL to compile — `The type or namespace name 'CurrencyFormat' does not exist in the namespace 'AssetDesk.Shared'`.

- [ ] **Step 3: Create the shared formatter**

Create `src/AssetDesk.Shared/CurrencyFormat.cs`:

```csharp
namespace AssetDesk.Shared;

/// <summary>
/// How an amount is rendered, for both the API (PDF and CSV output) and the Blazor client.
/// Lives in Shared rather than beside <c>Currencies</c> in the API project because
/// AssetDesk.Web references only this assembly - which is why Reports.razor used to paste in
/// its own peso sign.
///
/// A currency is only supported once it has a symbol here, which is the reason the Currency
/// lookup type stays locked: an admin activating a row for a code this switch has never heard
/// of would render every amount for it with a peso sign.
/// </summary>
public static class CurrencyFormat
{
    public const string Php = "PHP";
    public const string Usd = "USD";

    /// <summary>Falls back to the peso sign: every row predating multi-currency is a peso row.</summary>
    public static string SymbolFor(string? code) => code switch
    {
        Usd => "$",
        _ => "₱"
    };

    /// <summary>Renders an amount, or an em dash when there is no amount to render.</summary>
    public static string Format(decimal? amount, string? code) =>
        amount.HasValue ? $"{SymbolFor(code)}{amount.Value:N2}" : "—";
}
```

- [ ] **Step 4: Widen the currency vocabulary**

In `src/AssetDesk.Api/Entities/Asset.cs`, replace the whole `Currencies` class with:

```csharp
/// <summary>
/// The currencies an asset may be recorded in. <see cref="All"/> is what
/// LookupTypes.FallbackValues hands the validator, and mirrors the active rows in the
/// Currency lookup.
///
/// <see cref="Retired"/> holds codes this app used to offer and no longer activates. They are
/// kept as constants because LookupValueSeed still carries their rows: a lookup row is never
/// deleted, only deactivated, so history that references them stays readable. Activating one
/// means giving it a symbol in <see cref="CurrencyFormat"/> as well - JPY additionally needs a
/// zero-decimal path, which is why the set was widened one currency at a time.
/// </summary>
public static class Currencies
{
    public const string PHP = "PHP";
    public const string USD = "USD";

    public const string EUR = "EUR";
    public const string GBP = "GBP";
    public const string JPY = "JPY";
    public const string CAD = "CAD";
    public const string AUD = "AUD";

    public static readonly string[] All = [PHP, USD];

    public static readonly string[] Retired = [EUR, GBP, JPY, CAD, AUD];
}
```

Note the `Symbol` constant is gone. Any call site still referencing `Currencies.Symbol` will now fail to compile — that is deliberate, and Task 5 fixes the one in `PdfReportService`.

- [ ] **Step 5: Activate the seeded USD row**

In `src/AssetDesk.Api/Data/LookupValueSeed.cs:39`, change:

```csharp
        Row(12, LookupTypes.Currency, Currencies.USD, "USD", 1, isActive: false),
```

to:

```csharp
        Row(12, LookupTypes.Currency, Currencies.USD, "USD", 1),
```

`Row`'s `isActive` parameter defaults to true. This is required for more than production: `TestDb.Create()` uses `EnsureCreated()`, which applies `HasData`, so without this change every test wanting a USD asset would have to flip the row by hand.

- [ ] **Step 6: Rewrite the locked reason**

In `src/AssetDesk.Api/Entities/LookupValue.cs`, replace the `Currency` arm of `LockedReason`:

```csharp
        Currency =>
            "Every supported currency needs a symbol and a decimal rule defined in code - see " +
            "CurrencyFormat. Activating a row here for a code that has neither would render its " +
            "amounts with a peso sign rather than its own.",
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~CurrencyFormatTests"`

Expected: PASS, 7 tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: one compile error only, in `PdfReportService.cs` — `'Currencies' does not contain a definition for 'Symbol'`. Fix it now by changing `FormatCurrency` at `src/AssetDesk.Api/Services/PdfReportService.cs:296-300` to:

```csharp
    private static string FormatCurrency(decimal? value, string? currency) =>
        CurrencyFormat.Format(value, currency);
```

and adding `using AssetDesk.Shared;` to the top of that file. Re-run. Expected: 253 passing, 0 failing.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Shared/CurrencyFormat.cs src/AssetDesk.Api/Entities/Asset.cs src/AssetDesk.Api/Entities/LookupValue.cs src/AssetDesk.Api/Data/LookupValueSeed.cs src/AssetDesk.Api/Services/PdfReportService.cs tests/AssetDesk.Api.Tests/CurrencyFormatTests.cs
git commit -m "feat(currency): add a shared formatter and activate USD

The peso sign was a constant four call sites pasted in, and AssetDesk.Web
could not reach it at all - Reports.razor kept its own copy. CurrencyFormat
lands in Shared so both projects format identically.

Currencies.All gains USD and the seeded lookup row is activated. The seed
change matters for tests too: TestDb.Create uses EnsureCreated, which applies
HasData, so without it every USD test would flip the row by hand.

LockedReason(Currency) said re-activating a currency would mislabel figures
because rendering ignores the column. That was the precondition this work
removes, so the reason is now the one that survives: a currency needs a symbol
defined in code.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: The ExchangeRate column and its migration

**Files:**
- Modify: `src/AssetDesk.Api/Entities/Asset.cs`, `src/AssetDesk.Shared/DTOs/AssetDto.cs`
- Create: `src/AssetDesk.Api/Migrations/<timestamp>_AddExchangeRate.cs` (generated, then hand-edited)

**Interfaces:**
- Consumes: `Currencies.All` from Task 1.
- Produces: `Asset.ExchangeRate` (decimal, default `1m`); `AssetDto.ExchangeRate` (decimal), `CreateAssetDto.ExchangeRate` (decimal, default `1m`), `UpdateAssetDto.ExchangeRate` (decimal?). Tasks 3, 4 and 5 all read these.

- [ ] **Step 1: Write the failing test**

Append to `tests/AssetDesk.Api.Tests/CurrencyFormatTests.cs`:

```csharp
    [Fact]
    public async Task A_new_asset_defaults_to_a_rate_of_exactly_one()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");

            Assert.Equal(1m, asset.ExchangeRate);
            Assert.Equal(Currencies.PHP, asset.Currency);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~A_new_asset_defaults_to_a_rate"`

Expected: FAIL to compile — `'Asset' does not contain a definition for 'ExchangeRate'`.

- [ ] **Step 3: Add the entity field**

In `src/AssetDesk.Api/Entities/Asset.cs`, immediately after the `Currency` property:

```csharp
    /// <summary>
    /// Pesos per one unit of <see cref="Currency"/>, as booked against the supplier invoice.
    /// Exactly 1 for PHP. The peso value is derived - PurchasePrice * ExchangeRate - rather
    /// than stored, so it cannot drift out of agreement with the rate on this same row.
    /// </summary>
    public decimal ExchangeRate { get; set; } = 1m;
```

- [ ] **Step 4: Add it to the three DTOs**

In `src/AssetDesk.Shared/DTOs/AssetDto.cs`, add after each existing `Currency` member:

`AssetDto` (after line 30):
```csharp
    public decimal ExchangeRate { get; init; } = 1m;
```

`CreateAssetDto` (after line 100):
```csharp
    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal ExchangeRate { get; init; } = 1m;
```

`UpdateAssetDto` (after line 148):
```csharp
    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal? ExchangeRate { get; init; }
```

- [ ] **Step 5: Configure precision**

In `src/AssetDesk.Api/Data/AppDbContext.cs`, inside the `Asset` entity configuration block, add:

```csharp
            entity.Property(e => e.ExchangeRate)
                  .HasColumnType("numeric(18,6)")
                  .HasDefaultValue(1m);
```

Six decimal places because a rate like 58.2345 is ordinary and rounding at two introduces error in the fourth digit of a six-figure purchase.

- [ ] **Step 6: Generate the migration**

Run:
```bash
cd src/AssetDesk.Api && dotnet ef migrations add AddExchangeRate
```

Expected: creates `Migrations/<timestamp>_AddExchangeRate.cs` containing an `AddColumn` for `ExchangeRate` and an `UpdateData` flipping `IsActive` on `LookupValues` id 12.

- [ ] **Step 7: Replace the generated UpdateData with value-matched SQL**

In the generated migration, delete the `UpdateData` call targeting `keyValue: 12` and put this at the end of `Up`:

```csharp
            // Matched on (LookupType, Value), not Id. LookupValueSeed assigns the currency rows
            // out of order - PHP 15, USD 12, JPY 16, AUD 18 - so an id here is a magic number
            // whose correctness nobody can see at the call site. Value is documented immutable
            // on LookupValue, which makes it the stable key.
            migrationBuilder.Sql(
                """
                UPDATE "LookupValues"
                SET "IsActive" = true, "UpdatedAt" = now()
                WHERE "LookupType" = 'Currency' AND "Value" = 'USD';
                """);
```

And the mirror in `Down`, replacing the generated reversal:

```csharp
            migrationBuilder.Sql(
                """
                UPDATE "LookupValues"
                SET "IsActive" = false, "UpdatedAt" = now()
                WHERE "LookupType" = 'Currency' AND "Value" = 'USD';
                """);
```

Leave the `AddColumn`/`DropColumn` pair as generated. `Down` does not convert any USD amounts back to pesos — a `Down` that silently rewrites money is worse than one that leaves it, which is the same call `PesoOnlyCurrency` made.

- [ ] **Step 8: Check the generated PostgreSQL type by hand**

Run:
```bash
cd src/AssetDesk.Api && dotnet ef migrations script --idempotent | grep -i "ExchangeRate"
```

Expected: a line containing `numeric(18,6) NOT NULL DEFAULT 1.0`. The suite runs on SQLite, so a green run does not prove this — read the emitted SQL.

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 254 passing, 0 failing.

- [ ] **Step 10: Commit**

```bash
git add src/AssetDesk.Api/Entities/Asset.cs src/AssetDesk.Shared/DTOs/AssetDto.cs src/AssetDesk.Api/Data/AppDbContext.cs src/AssetDesk.Api/Migrations/
git commit -m "feat(currency): record the rate an asset was booked at

PesoOnlyCurrency relabelled every asset to PHP but left the amounts alone,
because converting them needed a rate and an as-of date the schema did not
record. This is that rate.

Stored per record rather than in an effective-dated table: the requirement is
reconciling against a supplier invoice, and an invoice states the rate it was
booked at. The peso value stays derived so it cannot disagree with the rate
beside it.

The USD lookup activation is matched on (LookupType, Value) rather than the
generated id - the seed assigns currency rows out of order, so an id here
would be a magic number.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Rate validation in the API

**Files:**
- Modify: `src/AssetDesk.Api/Controllers/AssetsController.cs`
- Create: `tests/AssetDesk.Api.Tests/AssetCurrencyValidationTests.cs`

**Interfaces:**
- Consumes: `Asset.ExchangeRate`, `CreateAssetDto.ExchangeRate`, `UpdateAssetDto.ExchangeRate` from Task 2.
- Produces: `public static string? AssetsController.ValidateRate(string currency, decimal rate)` — returns null when valid, otherwise the message. **Public, not internal or private:** the solution has no `InternalsVisibleTo`, so `AssetDesk.Api.Tests` can only reach public members, and the test below calls it directly. Task 4 mirrors these rules in the importer but does not call this method.

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/AssetCurrencyValidationTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// A currency without a sane rate is how the figures get quietly wrong: a rate of zero
/// removes the asset from every total, and "PHP at 58.20" multiplies a peso figure by 58.
/// </summary>
public class AssetCurrencyValidationTests
{
    private static CreateAssetDto NewAsset(string currency, decimal rate) => new()
    {
        Name = "Test Laptop",
        DeviceType = DeviceTypes.Laptop,
        Status = AssetStatus.Available,
        PurchasePrice = 1200m,
        Currency = currency,
        ExchangeRate = rate
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_rate_of_zero_or_less_is_rejected(decimal rate)
    {
        var error = AssetsController.ValidateRate(Currencies.USD, rate);
        Assert.NotNull(error);
        Assert.Contains("greater than zero", error);
    }

    [Fact]
    public void PHP_must_be_booked_at_exactly_one()
    {
        var error = AssetsController.ValidateRate(Currencies.PHP, 58.20m);
        Assert.NotNull(error);
        Assert.Contains("PHP", error);
        Assert.Contains("1", error);
    }

    [Fact]
    public void PHP_at_one_is_accepted()
    {
        Assert.Null(AssetsController.ValidateRate(Currencies.PHP, 1m));
    }

    [Fact]
    public void USD_at_a_real_rate_is_accepted()
    {
        Assert.Null(AssetsController.ValidateRate(Currencies.USD, 58.20m));
    }

    [Fact]
    public void USD_left_at_the_default_rate_of_one_is_rejected()
    {
        var error = AssetsController.ValidateRate(Currencies.USD, 1m);
        Assert.NotNull(error);
        Assert.Contains("rate is required", error);
    }
}
```

Note `USD at exactly 1` is rejected: the DTO defaults `ExchangeRate` to `1m`, so a client that sends a USD asset and forgets the rate would otherwise book dollars as pesos silently. A genuine 1.0000 USD→PHP rate does not exist, so nothing legitimate is lost.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~AssetCurrencyValidationTests"`

Expected: FAIL to compile — `'AssetsController' does not contain a definition for 'ValidateRate'`.

- [ ] **Step 3: Add the validator**

In `src/AssetDesk.Api/Controllers/AssetsController.cs`, add as a public static method on the class:

```csharp
    /// <summary>
    /// The three rate rules, shared by create and update. Returns null when the pair is valid,
    /// otherwise the message to hand back. AssetImportService enforces the same rules per row
    /// against its own error type.
    ///
    /// Public rather than private so the test project can call it: the solution declares no
    /// InternalsVisibleTo, so internal would be unreachable from AssetDesk.Api.Tests.
    /// </summary>
    public static string? ValidateRate(string currency, decimal rate)
    {
        if (rate <= 0)
            return "Exchange rate must be greater than zero.";

        if (currency == Currencies.PHP && rate != 1m)
            return $"{Currencies.PHP} is the reporting currency, so its exchange rate must be exactly 1.";

        if (currency != Currencies.PHP && rate == 1m)
            return $"An exchange rate is required for {currency} - pesos per 1 {currency}.";

        return null;
    }
```

- [ ] **Step 4: Call it from create**

In `CreateAsset`, replace the currency check at `AssetsController.cs:107-111` with:

```csharp
        // The set of acceptable currencies is data-driven: whichever Currency lookup rows are
        // active. Activating a row is what enables a currency, not a change here.
        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency))
            return BadRequest(ApiResponse<AssetDto>.Fail(
                $"'{dto.Currency}' is not a valid currency. Supported: {string.Join(", ", Currencies.All)}."));

        var rateError = ValidateRate(dto.Currency, dto.ExchangeRate);
        if (rateError is not null)
            return BadRequest(ApiResponse<AssetDto>.Fail(rateError));
```

And in the `new Asset { ... }` initialiser, after `Currency = dto.Currency,`:

```csharp
            ExchangeRate = dto.ExchangeRate,
```

- [ ] **Step 5: Call it from update**

In `UpdateAsset`, replace the currency check at `AssetsController.cs:183-185` with:

```csharp
        if (dto.Currency is not null && !await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency))
            return BadRequest(ApiResponse<AssetDto>.Fail(
                $"'{dto.Currency}' is not a valid currency. Supported: {string.Join(", ", Currencies.All)}."));

        // Validate the pair that will be stored, not just the half that was sent - changing
        // currency without a rate, or a rate without a currency, both land here.
        var effectiveCurrency = dto.Currency ?? asset.Currency;
        var effectiveRate = dto.ExchangeRate ?? asset.ExchangeRate;
        var rateError = ValidateRate(effectiveCurrency, effectiveRate);
        if (rateError is not null)
            return BadRequest(ApiResponse<AssetDto>.Fail(rateError));
```

And in the field-update block, after `if (dto.Currency is not null) asset.Currency = dto.Currency;`:

```csharp
        if (dto.ExchangeRate.HasValue) asset.ExchangeRate = dto.ExchangeRate.Value;
```

- [ ] **Step 6: Map it on the way out**

At `AssetsController.cs:514` in the asset-to-DTO mapping, after `Currency = asset.Currency,`:

```csharp
        ExchangeRate = asset.ExchangeRate,
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~AssetCurrencyValidationTests"`

Expected: PASS, 6 tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 260 passing, 0 failing.

- [ ] **Step 9: Commit**

```bash
git add src/AssetDesk.Api/Controllers/AssetsController.cs tests/AssetDesk.Api.Tests/AssetCurrencyValidationTests.cs
git commit -m "feat(currency): validate the currency and rate as a pair

Three rules: positive, exactly 1 for PHP, and required for anything else.
The last one rejects USD at exactly 1.0 because CreateAssetDto defaults the
rate to 1 - without it, a client that sends a USD asset and forgets the rate
books dollars as pesos and nothing complains. No real USD-PHP rate is 1.0000,
so nothing legitimate is refused.

Update validates the pair that will be stored rather than the half that was
sent, so changing currency without a rate is caught.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Optional ExchangeRate column in the Excel importer

**Files:**
- Modify: `src/AssetDesk.Api/Services/AssetImportService.cs`
- Create: `tests/AssetDesk.Api.Tests/AssetImportCurrencyTests.cs`

**Interfaces:**
- Consumes: `Asset.ExchangeRate` from Task 2; the rate rules from Task 3 (re-expressed against `ImportRowException`, not by calling `ValidateRate`, because the importer throws rather than returning messages).

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/AssetImportCurrencyTests.cs`:

```csharp
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The importer's contract with existing customers: a workbook built against the current
/// template, which has no ExchangeRate column at all, must keep importing untouched.
/// </summary>
public class AssetImportCurrencyTests
{
    private static readonly string[] LegacyHeaders =
    [
        "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
        "SerialNumber", "PurchasePrice", "Currency", "PurchaseDate",
        "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
    ];

    private static MemoryStream Workbook(string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var sheet = wb.Worksheets.Add("Assets");

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static AssetImportService ServiceFor(AppDbContext db) =>
        new(db, NullLogger<AssetImportService>.Instance, new LookupService(db));

    private static string[] LegacyRow(string currency, string price) =>
        ["Test Laptop", DeviceTypes.Laptop, AssetStatus.Available, "Dell", "XPS", "2024",
         "SN-1", price, currency, "", "", "", "", "Manila", ""];

    [Fact]
    public async Task A_workbook_with_no_ExchangeRate_column_still_imports()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            using var stream = Workbook(LegacyHeaders, LegacyRow(Currencies.PHP, "50000"));

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.ImportedCount);
            Assert.Equal(1m, db.Assets.Single().ExchangeRate);
        }
    }

    [Fact]
    public async Task A_blank_rate_on_a_USD_row_is_a_row_error()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            using var stream = Workbook(LegacyHeaders, LegacyRow(Currencies.USD, "1200"));

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Single(result.Errors);
            Assert.Contains("exchange rate is required", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, result.ImportedCount);
        }
    }

    [Fact]
    public async Task A_USD_row_with_a_rate_imports_and_keeps_both_numbers()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            string[] headers = [.. LegacyHeaders, "ExchangeRate"];
            string[] row = [.. LegacyRow(Currencies.USD, "1200"), "58.20"];
            using var stream = Workbook(headers, row);

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Empty(result.Errors);
            var asset = db.Assets.Single();
            Assert.Equal(1200m, asset.PurchasePrice);
            Assert.Equal(Currencies.USD, asset.Currency);
            Assert.Equal(58.20m, asset.ExchangeRate);
        }
    }

    [Fact]
    public async Task PHP_with_a_rate_other_than_one_is_a_row_error()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            string[] headers = [.. LegacyHeaders, "ExchangeRate"];
            string[] row = [.. LegacyRow(Currencies.PHP, "50000"), "58.20"];
            using var stream = Workbook(headers, row);

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Single(result.Errors);
            Assert.Contains("must be exactly 1", result.Errors[0].Message);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~AssetImportCurrencyTests"`

Expected: FAIL — the USD rows import with rate 1 instead of erroring, and the PHP-at-58.20 row is accepted.

- [ ] **Step 3: Read and validate the rate per row**

In `src/AssetDesk.Api/Services/AssetImportService.cs`, replace the currency block (the `var currency = ...` section around line 125):

```csharp
        var currency = ReadString(row, headerMap, "Currency");
        if (string.IsNullOrWhiteSpace(currency))
            currency = Currencies.PHP;
        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, currency, ct))
            throw new ImportRowException(
                $"Invalid Currency '{currency}'. Supported: {string.Join(", ", Currencies.All)}.");

        // Deliberately NOT in ExpectedHeaders: a header listed there is required, and adding
        // this one would reject every workbook built against the template customers already
        // have. ReadDecimal returns null when the column is absent.
        var exchangeRate = ReadDecimal(row, headerMap, "ExchangeRate") ?? 1m;
        if (exchangeRate <= 0)
            throw new ImportRowException("ExchangeRate must be greater than zero.");
        if (currency == Currencies.PHP && exchangeRate != 1m)
            throw new ImportRowException(
                $"ExchangeRate must be exactly 1 for {Currencies.PHP} - it is the reporting currency.");
        if (currency != Currencies.PHP && exchangeRate == 1m)
            throw new ImportRowException(
                $"An exchange rate is required for {currency} - add an ExchangeRate column with pesos per 1 {currency}.");
```

- [ ] **Step 4: Persist it**

In the same file, in the `new Asset { ... }` initialiser, after `Currency = currency,`:

```csharp
                    ExchangeRate = exchangeRate,
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~AssetImportCurrencyTests"`

Expected: PASS, 4 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 264 passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Api/Services/AssetImportService.cs tests/AssetDesk.Api.Tests/AssetImportCurrencyTests.cs
git commit -m "feat(currency): read an optional ExchangeRate column on import

ExchangeRate is read through headerMap rather than added to ExpectedHeaders.
Headers listed there are required and the service fails the whole file on a
missing one, so listing it would reject every spreadsheet built against the
template customers already hold. A legacy workbook with no such column still
imports, which is what the first test pins down.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Make every total rate-aware

**Files:**
- Modify: `src/AssetDesk.Api/Controllers/ReportsController.cs`, `src/AssetDesk.Api/Controllers/DashboardController.cs`
- Create: `tests/AssetDesk.Api.Tests/MixedCurrencyReportTests.cs`

**Interfaces:**
- Consumes: `Asset.ExchangeRate` from Task 2.
- Produces: no new API surface. `AssetValueSummaryDto.PrimaryCurrency` and `DashboardDto.PrimaryCurrency` keep their existing meaning — the currency the totals are expressed in, always `PHP`.

- [ ] **Step 1: Write the failing test**

Create `tests/AssetDesk.Api.Tests/MixedCurrencyReportTests.cs`:

```csharp
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The bug this whole feature would otherwise introduce: a USD 1,200 laptop adding 1,200 to a
/// peso total. Rows carry the currency they were booked in; totals are always pesos.
/// </summary>
public class MixedCurrencyReportTests
{
    private static async Task SeedMixedEstateAsync(AssetDesk.Api.Data.AppDbContext db, Guid tenantId)
    {
        // 50,000 pesos outright.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId,
            AssetTag = "LAP-0001",
            DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available,
            PurchasePrice = 50000m,
            Currency = Currencies.PHP,
            ExchangeRate = 1m
        });

        // USD 1,200 at 58.20 = 69,840 pesos.
        db.Assets.Add(new Asset
        {
            TenantId = tenantId,
            AssetTag = "LAP-0002",
            DeviceType = DeviceTypes.Laptop,
            Status = AssetStatus.Available,
            PurchasePrice = 1200m,
            Currency = Currencies.USD,
            ExchangeRate = 58.20m
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_asset_value_report_totals_in_pesos_not_in_mixed_units()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedMixedEstateAsync(db, tenantId);

            var controller = new ReportsController(db, null!);
            var result = await controller.GetAssetValueReport();

            var summary = Assert.IsType<AssetValueSummaryDto>(
                Assert.IsType<ApiResponse<AssetValueSummaryDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value).Data);

            // 50,000 + (1,200 * 58.20) = 119,840. Naive summing would give 51,200.
            Assert.Equal(119840m, summary.GrandTotalValue);
            Assert.Equal(2, summary.TotalAssetCount);
            Assert.Equal(59920m, summary.AverageAssetValue);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);

            var laptops = Assert.Single(summary.ByDeviceType);
            Assert.Equal(119840m, laptops.TotalValue);
            Assert.Equal(59920m, laptops.AverageValue);

            var available = Assert.Single(summary.ByStatus);
            Assert.Equal(119840m, available.TotalValue);
        }
    }

    [Fact]
    public async Task The_dashboard_total_is_in_pesos_too()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedMixedEstateAsync(db, tenantId);

            var controller = new DashboardController(db);
            var result = await controller.GetDashboard();

            var dashboard = Assert.IsType<DashboardDto>(
                Assert.IsType<ApiResponse<DashboardDto>>(
                    Assert.IsType<OkObjectResult>(result.Result).Value).Data);

            Assert.Equal(119840m, dashboard.TotalAssetValue);
            Assert.Equal(119840m, Assert.Single(dashboard.AssetsByType).TotalValue);
        }
    }
}
```

`ReportsController` is `(AppDbContext db, IPdfReportService pdf)` and `DashboardController` is `(AppDbContext db)`, so the constructions above are correct as written. `null!` is safe for the PDF service because none of these tests reach a PDF path.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~MixedCurrencyReportTests"`

Expected: FAIL — `Assert.Equal() Failure: Expected: 119840, Actual: 51200`.

- [ ] **Step 3: Make the value report rate-aware**

In `src/AssetDesk.Api/Controllers/ReportsController.cs`, `GetAssetValueReport`: add `a.ExchangeRate` to the anonymous projection, then replace all four aggregations.

Projection:
```csharp
            .Select(a => new
            {
                a.DeviceType,
                a.Status,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate
            })
```

Grand total:
```csharp
        // Every total is in pesos; a row keeps the currency it was booked in. Summing the raw
        // price would add a USD figure to a peso figure.
        var totalValue = assets.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate);
```

Per device type:
```csharp
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate),
                AverageValue = g.Count() > 0 ? g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate) / g.Count() : 0,
```

Per status:
```csharp
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate),
```

- [ ] **Step 4: Make the dashboard rate-aware**

In `src/AssetDesk.Api/Controllers/DashboardController.cs`, add `a.ExchangeRate` to the asset projection, then:

```csharp
        var totalValue = assets.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate);
```

and inside the `DeviceTypeCountDto` projection:

```csharp
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate)
```

- [ ] **Step 5: Confirm the per-row currency needs no work**

No code change here — verify and move on. `GetInventoryReport` already projects `Currency = a.Currency` into `AssetInventoryReportRow`, the CSV export already carries the column, and `PdfReportService` already passes `row.Currency` to `FormatCurrency` at every call site (lines 54, 89, 167, 168) and `summary.PrimaryCurrency` at the total sites (145, 147, 190).

The PDF was written currency-aware and then deliberately neutered by `_ = currency;` when the app went peso-only. Task 1 removed that line, so every PDF row renders in its booked currency from that point on, with no further change. Confirm by reading those call sites rather than assuming.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter "FullyQualifiedName~MixedCurrencyReportTests"`

Expected: PASS, 2 tests.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 266 passing, 0 failing.

- [ ] **Step 8: Commit**

```bash
git add src/AssetDesk.Api/Controllers/ReportsController.cs src/AssetDesk.Api/Controllers/DashboardController.cs tests/AssetDesk.Api.Tests/MixedCurrencyReportTests.cs
git commit -m "feat(currency): total in pesos across a mixed estate

GetAssetValueReport already projected Currency and already declared a
PrimaryCurrency, then summed raw prices in four places. A USD 1,200 laptop
added 1,200 to a peso total. Same shape in DashboardController.

Rows keep the currency they were booked in and totals are always pesos, so a
USD 1,200 line above a peso grand total needs no footnote.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: The Blazor surface

**Files:**
- Modify: `src/AssetDesk.Web/Pages/Assets/Edit.razor:176`, `src/AssetDesk.Web/Pages/Reports.razor:546-547`

**Interfaces:**
- Consumes: `CurrencyFormat.Format` from Task 1; `CreateAssetDto.ExchangeRate` / `UpdateAssetDto.ExchangeRate` from Task 2.

There is no Blazor test project. This task is verified by `dotnet build` and by running the app.

- [ ] **Step 1: Replace the hand-rolled formatter in Reports.razor**

At `src/AssetDesk.Web/Pages/Reports.razor:546-547`, replace:

```csharp
    // Peso-only app - the stored currency code is not consulted.
    private static string FormatCurrency(decimal? value) =>
        value.HasValue ? $"₱{value.Value:N2}" : "—";
```

with:

```csharp
    // Totals are pesos; a row renders in whatever it was booked in. Callers passing a row
    // amount supply that row's currency, callers passing a total supply PrimaryCurrency.
    private static string FormatCurrency(decimal? value, string? currency = CurrencyFormat.Php) =>
        CurrencyFormat.Format(value, currency);
```

Add `@using AssetDesk.Shared` to the top of the file if it is not already there. Existing single-argument call sites keep compiling because the parameter is optional; update the ones rendering a per-asset amount to pass that asset's `Currency`.

- [ ] **Step 2: Add the currency selector and rate input to Edit.razor**

At `src/AssetDesk.Web/Pages/Assets/Edit.razor:176`, replace the single price field:

```razor
                    <FormField LabelText="Purchase Price (₱)">
                        <Input Type="number" @bind-Value="_model.PurchasePrice" Placeholder="129900.00" />
                    </FormField>
```

with:

```razor
                    <FormField LabelText="@($"Purchase Price ({CurrencyFormat.SymbolFor(_model.Currency)})")">
                        <Input Type="number" @bind-Value="_model.PurchasePrice" Placeholder="129900.00" />
                    </FormField>
                    <FormField LabelText="Currency">
                        <select class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                                @bind="_model.Currency">
                            @foreach (var code in _currencies)
                            {
                                <option value="@code">@code</option>
                            }
                        </select>
                    </FormField>
                    @if (_model.Currency != CurrencyFormat.Php)
                    {
                        <FormField LabelText="@($"Exchange Rate (₱ per 1 {_model.Currency})")">
                            <Input Type="number" @bind-Value="_model.ExchangeRate" Placeholder="58.20" />
                        </FormField>
                    }
```

- [ ] **Step 3: Add the backing field and reset the rate when the currency changes**

In the `@code` block of `Edit.razor`, add:

```csharp
    // Mirrors the active Currency lookup rows. Hardcoded rather than fetched because the
    // Currency lookup is locked - the set only changes when a migration activates a row.
    private static readonly string[] _currencies = [CurrencyFormat.Php, CurrencyFormat.Usd];
```

and ensure `_model.ExchangeRate` is reset to `1m` whenever `Currency` returns to `PHP`, so a stale rate is not submitted and rejected:

```csharp
    private void OnCurrencyChanged()
    {
        if (_model.Currency == CurrencyFormat.Php)
            _model.ExchangeRate = 1m;
    }
```

Wire it with `@bind:after="OnCurrencyChanged"` on the select.

- [ ] **Step 4: Rebuild the stylesheet**

The `<select>` above uses only utilities already present elsewhere in the app, so no new Tailwind class is introduced. If any new arbitrary value is added, run:

```bash
npm --prefix src/AssetDesk.Web run build:css
```

and bump the `?v=` on the stylesheet link in `wwwroot/index.html` plus `cacheName` in `service-worker.published.js`.

- [ ] **Step 5: Build**

Run: `dotnet build`

Expected: build succeeded, 0 errors.

- [ ] **Step 6: Verify in the running app**

Start the API and the Web project, sign in, open an asset for editing. Confirm: the currency selector shows PHP and USD only; choosing USD reveals the rate field; the price label switches from ₱ to $; saving USD 1,200 at 58.20 succeeds; saving USD with the rate left at 1 is rejected with "An exchange rate is required for USD"; the Reports page shows a peso grand total of 119,840 for the mixed estate.

- [ ] **Step 7: Commit**

```bash
git add src/AssetDesk.Web/Pages/Assets/Edit.razor src/AssetDesk.Web/Pages/Reports.razor
git commit -m "feat(currency): pick a currency and rate on the asset form

The price label takes the selected currency's symbol, and the rate input only
appears when there is a rate to give. Returning the selection to PHP resets
the rate to 1 so a stale rate is never submitted and bounced.

Reports.razor stops pasting in its own peso sign - the currency parameter is
optional so existing total call sites are unchanged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Done When

- `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj` reports **266 passing, 0 failing**.
- `dotnet build` succeeds with 0 errors.
- `dotnet ef migrations script --idempotent | grep ExchangeRate` shows `numeric(18,6) NOT NULL DEFAULT 1.0`.
- A workbook exported from the pre-existing import template, with no `ExchangeRate` column, imports unchanged.
- No occurrence of `₱` remains in a C# or Razor file outside `CurrencyFormat.cs`. Check with:
  `grep -rn "₱" --include=*.cs --include=*.razor src | grep -v CurrencyFormat.cs`
