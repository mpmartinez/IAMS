# Multi-currency: capture in the invoiced currency, report in pesos

Date: 2026-09-11

## Problem

`20260905020549_PesoOnlyCurrency` made AssetDesk peso-only. It re-denominated every
`Asset.Currency` to `PHP`, deactivated the `USD`, `EUR`, `GBP`, `JPY`, `CAD` and `AUD`
rows in `LookupValues`, and narrowed `Currencies.All` to a single code. The migration
is explicit about what it did *not* do:

> This relabels the code ONLY - PurchasePrice is untouched, because converting the
> figures would need an FX rate and an as-of date the schema does not record.

That sentence is the whole of this design. An organisation buying equipment on a USD
invoice has to hand-convert before entry, which destroys both the invoiced figure and
the rate used, so the stored number can never be reconciled against the supplier's
paperwork. Recording the rate alongside the amount is what the schema is missing.

This design covers capture only. The reporting currency stays pesos, there is no rate
table and no historical rate lookup, and only `USD` is reactivated. Those exclusions
are listed with their reasons under Out of Scope.

## What already works

Three things mean this is smaller than it looks.

`AssetsController` does not validate currency against a hardcoded array. Both the
create and update paths call `lookups.IsActiveValueAsync(LookupTypes.Currency, ...)`
(`AssetsController.cs:109` and `:183`), so the set of acceptable currencies is already
data-driven. Activating the dormant `USD` row is sufficient to make the API accept it;
no validation code changes to *permit* the currency.

`Asset.Currency` survived the peso-only migration as `character varying(3)` with a
`PHP` default, and every asset still carries it. The column does not need adding.

`Asset` is already in `AuditSaveChangesInterceptor.AuditedTypes`, so currency and rate
changes get field-level before/after audit rows with no work.

## Data model

`Asset` gains one field:

```csharp
/// <summary>Pesos per one unit of <see cref="Currency"/>, as booked. Exactly 1 for PHP.</summary>
public decimal ExchangeRate { get; set; } = 1m;
```

`numeric(18,6)`, NOT NULL, default `1.0`. Six decimal places because a rate like
58.2345 is normal and rounding it at two would introduce error at the fourth digit of
a six-figure purchase.

The peso value is **derived, never stored**: `PurchasePrice * ExchangeRate`. A
persisted peso column is the obvious alternative and is rejected deliberately - it can
drift out of agreement with the rate sitting on the same row, and there is no way to
tell afterwards which of the two is wrong. The multiplication is free at the scale this
reports over.

The rate is per record rather than per date because the requirement is to reconcile
against an invoice, and an invoice states the rate that was actually booked. A central
effective-dated rate table answers a different question - "what was the rate in March" -
which nothing here asks.

## Migration

Three statements.

Add the column with its default, which backfills every existing row to `1.0`. That is
correct rather than merely convenient: `PesoOnlyCurrency` already forced every row to
`PHP`, so a rate of 1 is true for all of them.

Reactivate the USD lookup row:

```sql
UPDATE "LookupValues" SET "IsActive" = true, "UpdatedAt" = now()
WHERE "LookupType" = 'Currency' AND "Value" = 'USD';
```

Matched on `(LookupType, Value)` rather than on `Id`. `PesoOnlyCurrency` used
`UpdateData` against hardcoded ids, and those ids do not follow currency order -
`LookupValueSeed` assigns PHP 15, USD 12, EUR 13, GBP 14, JPY 16, CAD 17, AUD 18. Anyone
repeating that pattern has to look up which scrambled id means USD and gets a silently
wrong row if they guess. `Value` is documented as immutable on `LookupValue`, which makes
it the stable key.

Setting `UpdatedAt` is a small deliberate departure: `PesoOnlyCurrency` changed `IsActive`
and `SortOrder` without touching it, leaving rows whose last-modified stamp disagrees with
their contents. The field exists to record exactly this.

`SortOrder` needs no change. The seed already gives PHP 0 and USD 1, so the selector
renders them in that order once USD is active.

`Down` reverses both: deactivate `USD`, drop the column. It does not attempt to convert
any USD amounts back, for the same reason `PesoOnlyCurrency` did not - the rate is
knowable per row, but a `Down` that silently rewrites money is worse than one that
leaves it.

## Validation

Activating USD is not enough on its own, because a currency without a sane rate is how
the figures get quietly wrong. Three rules, enforced in `AssetsController` on create and
update, and in `AssetImportService` per row:

- Rate must be greater than zero. Zero or negative silently zeroes or inverts the asset's
  contribution to every total.
- Rate must be exactly `1` when the currency is `PHP`. Without this, "PHP at 58.20" is
  accepted and multiplies a peso figure by 58.
- Rate is required when the currency is not `PHP`. There is no sensible default and
  guessing one would fabricate a number that looks booked.

`Currencies.All` becomes `[PHP, USD]`. `Currencies.Retired` loses `USD` and keeps the
rest.

## The lookup stays locked

`Currency` remains in `LookupTypes.Locked`, so `LookupsController` continues to reject
writes against it. The *reason* has to change, because the current one stops being true:

> AssetDesk is peso-only. Every amount in the UI, the PDF reports and the CSV exports is
> rendered with the peso symbol without consulting this table, so re-activating another
> currency here would mislabel the figures rather than convert them.

That text describes the precondition this design satisfies. Once rendering consults the
currency, re-activating a row no longer mislabels anything. `LockedReason(Currency)` is
rewritten to the reason that survives: every supported currency needs a symbol and a
decimal rule defined in code, so the vocabulary stays code-owned even though the grants
live in data. An admin activating `XYZ` through the lookups screen would produce a
currency with no symbol and no formatting, which is precisely the class of breakage the
Locked/Editable split exists to prevent.

## Formatting

Four sites render the peso sign today, two per project:

- `Entities/Asset.cs:99` - the `Currencies.Symbol` constant itself
- `Services/PdfReportService.cs:299` - `$"{Currencies.Symbol}{value:N2}"`
- `Pages/Assets/Edit.razor:176` - the literal label `Purchase Price (₱)`
- `Pages/Reports.razor:547` - `$"₱{value:N2}"`

`Currencies.Symbol` is replaced by `Currencies.SymbolFor(string code)`, and a single
`CurrencyFormat.Format(decimal? amount, string code)` helper lands in `AssetDesk.Shared`
so the API and the Blazor client format identically. Both call sites in each project use
it; the Razor pages stop building the string themselves.

`Assets/Edit.razor` gains a currency selector bound to the active Currency lookup values,
and a rate input rendered only when the selection is not `PHP`. The price label takes the
selected currency's symbol rather than a hardcoded peso.

## Reports

This is where peso-only is actually load-bearing, and where the bug would otherwise land.

`ReportsController.GetAssetValueReport` already projects `a.Currency` into its anonymous
type and already declares a `PrimaryCurrency`, but then sums raw prices in four places -
the grand total, the per-device-type total, the per-device-type average and the per-status
total. Each becomes a sum of `price * rate`. Left alone, a USD 1,200 laptop would add
1,200 to a peso total.

`DashboardController` has the same shape: `TotalAssetValue` and the per-device-type
`TotalValue` inside `DeviceTypeCountDto`.

The presentation rule across every surface is that **a row shows the amount in the
currency it was booked in, and every total is in pesos.** A row reading `USD 1,200` above
a peso grand total is honest and needs no footnote; a row silently converted to pesos
loses the invoice figure again, which is the problem this design exists to fix. The
inventory report, its CSV export and its PDF all carry the original amount and its
currency per row.

`AssetValueSummaryDto.PrimaryCurrency` already exists and already carries `PHP`. It keeps
that meaning: the currency the totals are expressed in.

## Excel import

`AssetImportService.ExpectedHeaders` is **not** extended. A header listed there is
required - the service collects missing ones and fails the whole file - so adding
`ExchangeRate` to it would reject every spreadsheet built against the current template.
Instead the column is read optionally through `headerMap`, which `ReadDecimal` already
handles by returning null when the column is absent.

Per row: blank rate with `PHP` means `1.0`; blank rate with any other currency is a row
error, consistent with the validation rule above. The existing error text for an
unrecognised currency (`"AssetDesk is peso-only - leave the column blank or use 'PHP'"`)
is rewritten to list the active currencies.

A file exported from the current template, with no `ExchangeRate` column at all, still
imports unchanged. That is the acceptance criterion for this section.

## Testing

`tests/AssetDesk.Api.Tests`, following the existing pattern of exercising controllers
against a real `AppDbContext` rather than mocks.

- Rate validation: zero, negative, PHP with a rate other than 1, non-PHP with no rate.
- Asset value report over a mixed PHP and USD estate - grand total, per-device-type
  total and average, per-status total.
- Dashboard `TotalAssetValue` and per-device-type totals over the same mixed estate.
- Import: blank rate with PHP defaults to 1; blank rate with USD is a row error; a
  workbook with no `ExchangeRate` column imports as it does today.
- Formatting: `₱1,234.56` for PHP, `$1,234.56` for USD, through the shared helper.
- Migration: the USD lookup row is active afterwards.

CLAUDE.md notes the suite runs on in-memory SQLite, so a green run does not prove the
migration applies on PostgreSQL. The generated SQL for `numeric(18,6)` is checked by hand
before this is considered done.

## Out of Scope

Each of these is excluded by a decision taken during design, not by oversight.

**Reporting in a currency other than pesos.** Making every report and the PDF builder
currency-aware roughly doubles the reporting work and needs rate coverage across all
historical dates. Capture is what reconciles against an invoice; switchable reporting is
a separate want.

**A rate table with effective dates.** Superseded by storing the rate per record. Revisit
only if a requirement appears that genuinely asks "what was the rate on this date"
independently of a transaction.

**`EUR`, `GBP`, `JPY`, `CAD`, `AUD`.** Their rows stay deactivated. Reactivating one later
is a one-line migration plus a symbol entry. `JPY` additionally needs a zero-decimal
formatting path, which is a reason to add it deliberately rather than in bulk now.

**Converting existing figures.** Every existing row is genuinely a peso amount, so a rate
of 1 is not a placeholder but the truth.

**Currency on tickets, assignments, attachments.** None of them carry money. `Ticket`
records no amount, and the future procurement, licence and depreciation work will each
carry their own money fields - those are separate specs, and each will reuse
`ExchangeRate` and `CurrencyFormat` rather than reinventing them.
