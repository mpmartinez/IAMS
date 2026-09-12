# Depreciation: net book value from a per-device-type policy

Date: 2026-09-12

## Problem

The asset value report sums what the estate cost, not what it is worth. A laptop bought
for ₱60,000 five years ago still contributes ₱60,000 to `GrandTotalValue`, so the figure
drifts further from reality every year an organisation keeps using AssetDesk.

Nothing in the codebase models this today: `deprec`, `usefullife`, `residual`, `salvage`
and `bookvalue` return no hits outside this document. There is no useful life anywhere, no
residual value, and no notion of an asset ageing.

This design adds a **computed** net book value, a per-device-type policy that drives it,
and a report that stands beside the existing value report. It deliberately stops short of
an accounting feed; see Out of Scope.

## What it builds on

The multi-currency work (`2026-09-11-multi-currency-design.md`) established that the peso
value of an asset is `PurchasePrice * ExchangeRate`, derived rather than stored. Cost basis
here is that same expression, so a USD-invoiced asset depreciates against its peso cost and
every figure this feature produces is in pesos, consistent with the rule that rows carry the
currency they were booked in and totals are pesos.

`ExchangeRate` is non-nullable and defaults to exactly 1, so cost basis is computable
wherever `PurchasePrice` is.

## Policy

A new tenant-scoped entity, `DepreciationPolicy`:

```csharp
public class DepreciationPolicy : ITenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Matches Asset.DeviceType. Not a foreign key - DeviceType is a lookup value
    /// stored as a raw string on the asset row, exactly as Asset.DeviceType is.</summary>
    public required string DeviceType { get; set; }

    public int UsefulLifeMonths { get; set; }

    /// <summary>Percent of cost retained at end of life, 0-100.</summary>
    public decimal ResidualPercent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

Unique index on `(TenantId, DeviceType)` - one policy per device type per organisation.

**Tenant-scoped, and it has to be.** `LookupValue` carries no `TenantId` and no query filter
by deliberate design - its doc comment states the owner wants one shared vocabulary across
every tenant, managed centrally by a SuperAdmin. Useful life is not vocabulary, it is each
organisation's own accounting policy, so it cannot ride on the device-type lookup rows.

**Life in months, not years.** The arithmetic is monthly, so months avoids fractional years
and the rounding argument that follows them. A three-year laptop is 36.

**Residual as a percentage, not an amount.** One policy row covers every asset of that type,
and those assets differ in cost. A flat peso residual would be wrong for all but one of them.

## The calculation

A pure function with no `DbContext`, so it can be tested exhaustively and the report simply
maps over it:

```
costBasis    = PurchasePrice * ExchangeRate
residual     = costBasis * ResidualPercent / 100
monthly      = (costBasis - residual) / UsefulLifeMonths
elapsed      = max(0, (yNow - yBuy) * 12 + (mNow - mBuy) + 1)
accumulated  = min(monthly * elapsed, costBasis - residual)
netBookValue = costBasis - accumulated
```

Three details carry the design:

**The `+ 1` is the full-month convention.** The month of purchase counts as month one, so an
asset bought on any day in March depreciates for the whole of March. A 36-month laptop bought
20 March 2026 is fully depreciated at the end of February 2029. The visible consequence is
that an asset entered today shows one month of depreciation immediately - correct for this
convention, and surprising the first time it is seen.

**`max(0, ...)` handles a future purchase date.** Nothing stops an admin typing 2027, and
without the clamp `elapsed` goes negative and book value exceeds cost. Clamped, a
future-dated asset sits at full cost until its month arrives.

**`min(..., costBasis - residual)` is the floor.** Past end of life the asset holds at its
residual and never drifts to zero or below. This is what makes the report safe to leave
running for years without anyone checking it.

Rounding is applied at presentation, not inside the calculation - intermediate rounding over
36 months compounds into a visible discrepancy against cost.

A `UsefulLifeMonths` of zero or less is treated by the calculator as **not depreciable**, the
same as a missing policy, rather than divided by. Validation on the policy screen rejects it
first, so this is the second line of defence, not the first - but the calculator is a pure
function that anything may call, and a `DivideByZeroException` surfacing inside a report is a
worse failure than a row reported as undepreciable.

## What is deliberately not depreciated

An asset is **not depreciable** when any of these holds:

- `PurchasePrice` is null - both money columns are nullable, and plenty of real rows have no price
- `PurchaseDate` is null - likewise nullable
- No `DepreciationPolicy` row exists for its `DeviceType`

The third is not an edge case. `DeviceType` is in `LookupTypes.Editable`, so an admin can add
a device type at any time, and every asset of that new type is undepreciable until someone
writes a policy for it.

`Retired` and `Lost` assets are excluded from the report entirely, matching the three existing
exclusions in `ReportsController` (`a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost`).

**The report states the excluded count rather than silently absorbing it.** The response
carries `NotDepreciableCount` broken down by reason, and the UI shows it beside the total:

> 12 of 60 assets not depreciable — 9 missing a purchase date, 3 with no policy for their device type.

A book-value total that quietly omits a fifth of the estate is worse than no total, because it
looks authoritative. Naming the gap turns it into a task somebody can close.

## Surfaces

**`GET /api/reports/depreciation`**, with `/export` (CSV) and `/pdf` siblings. Every one of the
four existing reports has all three; a fifth without exports would be the odd one out, and
`PdfReportService` already has the builder shape to copy.

Per row: asset tag, device type, cost basis, purchase date, elapsed months, useful life,
accumulated depreciation, net book value. Summary: total cost basis, total accumulated
depreciation, total net book value, asset count, and the not-depreciable breakdown.

**Asset detail page** gains a book-value line for a depreciable asset, and says why not for
one that is not.

**`/admin/depreciation`** manages the policy - one row per device type, joining the existing
`/admin/audit`, `/admin/email-settings`, `/admin/lookups`, `/admin/roles` and `/admin/tenants`.

## Permission

The report reuses `iams:reports:view`; it is a report and belongs with the others.

The policy screen needs a new key, `iams:depreciation:manage`, in a new "Depreciation" group
in the `/admin/roles` matrix. No existing key fits: `iams:assets:edit` is about editing an
asset record, not setting organisation-wide accounting policy, and reusing it would hand
policy control to every Staff user.

`Permissions.DefaultsFor` returns the whole catalog for `Admin` and `SuperAdmin`, so new
tenants pick the key up for free. Existing tenants do not:
`SeedData.EnsureRolePermissionsAsync` is gated on `Tenant.RolePermissionsSeededAt` and never
revisits a tenant, so a key added today reaches nobody provisioned yesterday. The migration
backfills with raw SQL, copying `20260906031321_GrantAuditViewPermission` - including its two
deliberate details: the cross join against `Tenants` because built-in roles carry a null
`TenantId`, and `md5(...)::uuid` rather than `gen_random_uuid()` because the database is
supplied externally with no version pinned and being deterministic makes the insert idempotent.

Backfilling unconditionally is safe here for the same reason it was there, and only that
reason: the key is brand new, so no tenant can have deliberately revoked it.

## Testing

The calculator is a pure function, so it carries the bulk of the coverage:

- month one (an asset bought this month), mid-life, the exact final month, and past end of life
- the residual floor holds past end of life, and at 0% residual the asset reaches exactly zero
- a future purchase date clamps to zero elapsed and full cost
- a USD asset depreciates against `PurchasePrice * ExchangeRate`, not the raw price
- `UsefulLifeMonths` of zero or negative is rejected by validation, never divided by

Report tests cover the `Retired`/`Lost` exclusion, each not-depreciable reason and its count,
and a mixed-currency estate totalling in pesos.

Policy tests cover the unique constraint on `(TenantId, DeviceType)`, tenant isolation (one
tenant's policy never affects another's assets), and validation of life and residual bounds.

The permission backfill gets a test alongside the existing `RolePermissionSeedTests`.

The suite runs on in-memory SQLite via `TestDb.Create`, so as with the currency migration a
green run does not prove the migration applies on PostgreSQL. The generated SQL is read by hand.

## Out of Scope

Each excluded by a decision taken during design, not by oversight.

**Stored depreciation entries, period close and journal export.** Book value is computed on
demand. There are no monthly posting rows, no frozen periods, and no accounting-system feed,
so a correction to a price or a policy simply changes what the next read returns. That is the
right behaviour for a management report and the wrong one for a general ledger; wiring this to
accounting is a separate spec, and the calculator would be reused rather than rebuilt.

**Declining balance and every other method.** Straight-line only, and **no `Method` column**.
`Ticket.DueAt` and `BreachedAt` are the cautionary precedent: added early so the columns would
exist before there was history to lose, never populated, still displayed, and the overdue count
reads zero permanently. A second method is an additive migration when it is actually built.

**Per-asset overrides of life or residual.** Policy is per device type. An asset that genuinely
depreciates differently is not representable, which is the accepted cost of keeping two extra
nullable columns off `Asset` and two extra fields off the asset form.

**Revaluation, impairment, and disposal gain or loss.** Never discussed, not modelled.
