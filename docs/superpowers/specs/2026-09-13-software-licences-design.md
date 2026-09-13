# Software licences: entitlements, seats, renewals and compliance

Date: 2026-09-13

## Problem

AssetDesk has no notion of a software licence. `Software` is one value of `DeviceType`, so a
licence can only be recorded as an asset: one row, one tag, one assignee. There is nowhere to
say "50 seats of Microsoft 365, 47 in use, renews 1 March", and so no way to answer the two
questions a licence register exists for - *are we compliant?* and *what is about to lapse?*

Procurement makes it actively wrong. Receiving a purchase order line creates one asset per unit,
so receiving 50 seats of a subscription creates 50 `SFT-` asset records, each carrying a share of
the price into the asset value report, depreciation and the dashboard totals.

This design adds a licence register distinct from the hardware estate: entitlements acquired,
seats assigned to people or devices, renewal status, and a compliance report reconciling the two.

## What it builds on

**Multi-currency** (`2026-09-11-multi-currency-design.md`): an amount is recorded in the currency
it was invoiced in with the rate it was booked at; the peso value is derived, never stored; and
`CurrencyRules.Validate` is the single home for the currency/rate rules. Entitlement cost follows
all three.

**Procurement** (`2026-09-12-procurement-design.md`): receiving runs in one transaction under the
execution strategy, with an idempotency marker, the order row locked first, and a conditional
claim per line. This design changes only what a claimed Software line produces.

## Decisions taken during design

- **In use is counted against seats recorded in AssetDesk.** There is no agent on any machine and
  no installed-software import, so over-deployment means *more seats assigned than owned*.
- **A licence is either per user or per device**, and its seats go only to that kind.
- **Licence keys are stored, masked everywhere, and revealed only through an audited action.**
  Not encrypted at rest - see Out of Scope.
- **Receiving a Software line adds to a licence** rather than creating assets.

## Entities

Three new, all `ITenantEntity` with the same global query filter every tenant entity carries.
Every controller and service read also applies an **explicit tenant predicate**. The global filter
has an `IsSuperAdmin()` bypass; the depreciation feature shipped a Critical for leaning on it, and
procurement had to be corrected for the same shape twice.

**`SoftwareLicence`**

| Field | Notes |
|---|---|
| `Name` | required, e.g. "Microsoft 365 Business Standard"; unique on `(TenantId, Name)` |
| `Publisher` | free text |
| `SupplierId` | nullable FK to `Supplier`, `Restrict` |
| `LicenceModel` | `PerUser` or `PerDevice` |
| `LicenceKey` | nullable; masked in every DTO; redacted in the audit log |
| `ExpiresAt` | nullable - null means perpetual |
| `Notes`, `IsActive`, `CreatedAt`, `UpdatedAt` | |

`LicenceModel` cannot change once any seat - active or released - exists. Changing it would leave
every recorded seat pointing at the wrong kind of target.

**Licences are deactivated, never deleted.** There is no delete endpoint: a licence carries
entitlements linked to purchase-order receipts and a seat history, and both are the record of what
was bought and who had it. Hence `Restrict` on both child tables' licence foreign key.

**`LicenceEntitlement`** - the ledger of seats acquired.

| Field | Notes |
|---|---|
| `SoftwareLicenceId` | required, `Restrict` |
| `SeatsAdded` | positive for a purchase, 0 for a renewal, negative for a manual reduction |
| `Cost` | total for this entry, in `Currency` |
| `Currency`, `ExchangeRate` | validated by `CurrencyRules.Validate`, no second copy of the rules |
| `EntitlementDate` | |
| `ExpiresAtAfter` | nullable; set when this entry renewed or first dated the licence |
| `GoodsReceiptLineId` | nullable; set when the entry came from receiving, null when entered by hand |
| `CreatedByUserId`, `Notes`, `CreatedAt` | |

**Seats owned** is `SUM(SeatsAdded)`. A single `SeatCount` column cannot express that 50 seats
bought in January at 58.20 and 10 in June at 56.90 cost different pesos, for the same reason
goods receipts carry their own rate.

**Seat reductions.** A subscription renewed at 40 seats when 50 were owned genuinely drops to 40.
A manual entry may therefore carry a negative `SeatsAdded`, requires a note, and is refused if it
would take seats owned below zero. Receiving never produces a negative entry.

**`LicenceSeatAssignment`**

| Field | Notes |
|---|---|
| `SoftwareLicenceId` | required, `Restrict` |
| `UserId` | nullable FK to `ApplicationUser`, `Restrict` |
| `AssetId` | nullable FK to `Asset`, **`Cascade`** |
| `AssignedAt`, `ReleasedAt` | active while `ReleasedAt` is null |
| `AssignedByUserId`, `ReleasedByUserId`, `Notes` | |

- **Exactly one of `UserId` and `AssetId` is set**, and it must match the licence's model. The API
  enforces the model match; a database check constraint enforces exactly-one, so a bad row cannot
  exist regardless of which code path wrote it.
- **One active seat per target per licence**: partial unique indexes on
  `(SoftwareLicenceId, UserId) WHERE "ReleasedAt" IS NULL AND "UserId" IS NOT NULL` and the
  `AssetId` equivalent. Releasing a seat frees the target to be assigned again.
- **Deleting an asset cascades its device seats away**, as `AssetsController.DeleteAsset` already
  cascades the asset's assignment history. Retiring is the path that keeps history.
- **Users are never hard-deleted** - `UsersController.DeleteUser` sets `IsActive = false` - so a
  person's seats cannot silently vanish.

### Derived states

- **Over-assigned** - active seats exceed seats owned. **Allowed, never blocked.** Refusing the
  51st assignment does not stop the 51st install; it only stops it being recorded. The licence
  shows `51 of 50 - 1 over`, and the report counts it.
- **Reclaimable** - an active seat held by a deactivated user, or by an asset whose status is
  `Retired` or `Lost`. Still counted as assigned (it is, until released) but flagged, because it is
  where the saving usually is.
- **Renewal status** - `Perpetual` when `ExpiresAt` is null; `Expired` when `ExpiresAt` is before
  today; `Due` within 90 days of it, the same window `WarrantyCheckService` uses; otherwise `Active`.

Existing `SFT-` assets are not converted. Which asset corresponds to which licence cannot be
inferred, and a wrong guess is worse than none.

## Receiving a Software line

Detection is `PurchaseOrderLine.DeviceType == DeviceTypes.Software`. That is safe to branch on
although `DeviceType` is an `Editable` lookup type: `LookupsController` updates only a value's
`Label` and `IsActive`, never its `Value`, so a rename cannot break the match. Deactivating the
value only stops new Software lines being ordered.

`GoodsReceiptService.ReceiveAsync` keeps everything it does today - the execution strategy, the
`RequestId` marker and `verifySucceeded`, the order row lock taken first, the conditional claim per
line, `CurrencyRules.Validate`, the status recomputation. After a line is claimed:

- **Hardware line** - unchanged: one asset per unit.
- **Software line** - no assets. One `LicenceEntitlement` in the same transaction:

| Field | Value |
|---|---|
| `SeatsAdded` | quantity received, or **0** for a renewal |
| `Cost` | line `UnitPrice` x quantity received |
| `Currency` | the **order's** currency |
| `ExchangeRate` | the **receipt's** rate |
| `EntitlementDate` | the receipt date |
| `ExpiresAtAfter` | the new expiry, when given |
| `GoodsReceiptLineId` | the receipt line just created |

**Only a renewal moves an existing expiry, and only forward.** Adding seats may date a licence that
has no expiry yet - a first subscription purchase, or a licence created by this receipt - but never
changes one that is already dated: add-on seats take the licence's existing term. Otherwise a quote for
ten add-on seats ending in March would silently shorten a licence renewed last month to September, a
backdated receipt booked after a renewal would undo it, and a buyer with procurement rights alone would
be editing a licence's term. Correcting an expiry by hand stays possible through the licence page's
manual entry, which needs `iams:licences:manage`. The licence editor can change the expiry too, but only deliberately: it sends the
expiry it was opened with, leaves the stored value alone when the field was not touched - so a notes
edit made on a page loaded before a renewal cannot roll that renewal back - and refuses a deliberate
change when the stored expiry has moved since the form opened.

For an existing licence the move is a conditional update - `ExpiresAt` set only while it is still null
(adding seats) or earlier than the new date (renewing) - rather than an assignment to the tracked row.
The licence is read before the order lock, and receipts against different orders do not serialise, so
two renewals landing together could otherwise leave the earlier date. A conditional update that matches
no row refuses the receipt and rolls it back.

The line's `ReceivedQuantity` advances for both choices, so an order for "50 seats, 2027 renewal"
closes at 50 of 50 received.

**The receive request** gains, per line, optional `SoftwareLicenceId`, `NewLicence`
(`Name`, `Publisher`, `LicenceModel`), `Mode` (`AddSeats` default, or `Renew`) and `ExpiresAt`.

**Refused, before anything is written:**

- a Software line with neither `SoftwareLicenceId` nor `NewLicence`, or with both
- a licence id, a new licence, a `Renew` mode or an expiry on a hardware line
- a licence not in the caller's tenant (explicit predicate), or deactivated
- `Renew` without `ExpiresAt`, or with `ExpiresAt` on or before the receipt date, or not later than the licence's current expiry
- `AddSeats` with `ExpiresAt` against a licence that already has an expiry
- `NewLicence` whose name already exists in the tenant

A licence created through `NewLicence` is inserted inside the same transaction, so a refusal on a
later line leaves no stray licence.

**Permissions for receiving.** Receiving stays gated on `iams:procurement:manage`. Supplying
`NewLicence` additionally requires `iams:licences:manage`, so a buyer cannot create licences as a
side effect of a delivery. Adding an entitlement to an *existing* licence through a receipt needs
only the procurement permission - the receipt is the record of that purchase.

**Surfaces.** The PO detail's delivery history names the destination:
`50 x Microsoft 365 -> added to Microsoft 365 Business Standard`, or `-> renewed to 1 Mar 2028`.
The licence page lists the entitlement with a link to its purchase order.

**Consequence for existing reports.** Software received after this ships no longer appears in the
asset value report, depreciation, or dashboard totals, because it is no longer an asset. That is
the intended correction: those totals were counting seats as hardware. Licence spend is reported by
the compliance report below.

## Licence keys

- Every DTO that carries a licence returns `MaskedKey` only - the last four characters, e.g.
  `****-X7Q2` - or null. No list, detail, report or export contains the full key.
- The full key is returned only by `POST /api/licences/{id}/key/reveal`, gated on
  `iams:licences:reveal`. POST rather than GET so the key does not land in browser history or
  intermediary logs.
- Each successful reveal writes an `AuditLog` row of its own: who, which licence, when.
- **The automatic change log redacts `LicenceKey`.** `AuditSaveChangesInterceptor` gains a set of
  redacted property names: a change to one is recorded as changed, with no before or after value.
  `ApplicationUser` is kept out of `AuditedTypes` for the same underlying reason - a secret
  serialised into a table that only grows.

## Permissions

Three new keys in a new "Licences" group:

| Key | Admin / SuperAdmin | Staff | Auditor |
|---|---|---|---|
| `iams:licences:view` | yes | yes | yes |
| `iams:licences:manage` | yes | yes | - |
| `iams:licences:reveal` | yes | yes | - |

`manage` covers creating and editing licences, adding entitlements by hand, and assigning and
releasing seats. Staff get `reveal` because installing the software is their job; every reveal
is logged, and a tenant can revoke it per role at `/admin/roles`.

One backfill migration covers all three, copying `20260912150845_GrantProcurementPermissions`:
the cross join against `Tenants` because built-in roles carry a null `TenantId`, and
`md5(...)::uuid` rather than `gen_random_uuid()` for determinism and idempotency. Backfilling
unconditionally is safe for the single reason it was there - every key is brand new, so no tenant
can have revoked it.

The compliance report reuses `iams:reports:view`.

## Audit

`SoftwareLicence`, `LicenceEntitlement` and `LicenceSeatAssignment` join
`AuditSaveChangesInterceptor.AuditedTypes`, with `LicenceKey` redacted as above.

## Compliance report

`GET /api/reports/licences`, with `/export` (CSV) and `/pdf`, matching the other reports and built
from one summary method shared by all three, as `BuildDepreciationSummaryAsync` is.

Per licence: name, publisher, model, seats owned, seats assigned, over-assigned, reclaimable,
expiry, renewal status, total spend in pesos (`SUM(Cost * ExchangeRate)` over its entitlements).

Summary: licences, total seats owned and assigned, licences over-assigned and by how many seats,
reclaimable seats, renewals due and expired, total spend in pesos.

Deactivated licences are excluded, as `Retired` and `Lost` assets are excluded from the asset
reports.

## Surfaces

- **`/licences`** - list with seats owned/assigned, an over-assigned badge, and renewal status.
- **`/licences/{id}`** - details and masked key with Reveal; entitlement history with links to
  purchase orders and an "Add entitlement" form (add seats, renew, or reduce); seats with Assign
  and Release, released seats kept as history.
- **Nav** - a Licences entry in its own nav group, gated on `iams:licences:view`, with a badge counting licences `Due` or `Expired`. Renewal status is computed on read - no
  background job and no stored alert row, so renewing clears it and it cannot go stale.
- **Asset detail** - a read-only "Licences on this device" section for per-device seats.
- **Receive dialog** - licence choice, mode and expiry for Software lines.

## Testing

**Receiving** (extends `GoodsReceiptTests`):

- a Software line creates no assets and exactly one entitlement with the line's cost, the order's
  currency and the receipt's rate
- `Renew` adds zero seats and moves the licence's expiry; missing or non-future expiry is refused
- each refusal listed above writes nothing - no receipt, no entitlement, no licence, no advanced
  `ReceivedQuantity`
- `NewLicence` on one line followed by a refusal on a later line leaves no licence behind
- a replayed software receipt records one entitlement
- a mixed receipt creates assets for the hardware line and an entitlement for the software line
- `NewLicence` without `iams:licences:manage` is refused

**Ledger:** seats owned sums entitlements including a reduction; a reduction below zero is refused;
over-assigned and reclaimable count correctly, reclaimable covering a deactivated user, a `Retired`
asset and a `Lost` asset; the seat target must match the model; a second active seat for the same
target is refused and allowed again after release; `LicenceModel` cannot change once a seat exists.

**Keys:** no list, detail or report response contains the full key; reveal requires the permission;
a reveal writes an audit row; editing a key writes an audit row recording the change with no value.

**Isolation:** every new controller has a test with a super-admin caller whose current tenant is A
targeting tenant B's rows, constructed so it would pass only because of the explicit predicate.

**Report:** peso totals across mixed currencies; deactivated licences excluded; CSV columns align;
the PDF begins with `%PDF-`.

**Permissions:** the role defaults grant all three keys as tabled, in `PermissionCatalogTests` - which also
requires every key to have exactly three colon-separated parts, hence `iams:licences:reveal` rather than a
fourth segment. The backfill migration's SQL is PostgreSQL-only (`md5(...)::uuid`) and cannot run on the
SQLite suite, so it is verified against a PostgreSQL database holding a pre-existing tenant before merge.

The suite runs on in-memory SQLite, which proves neither the check constraint nor the partial
unique indexes on PostgreSQL. The generated SQL is read by hand, and the branch is exercised against
a local PostgreSQL database before merge: migrations, a mixed hardware and software receipt, a
second active seat for one target, and a key reveal.

## Out of Scope

**Installed-software discovery or import.** Over-deployment is counted against recorded seats. An
import of installed software per device (Intune, SCCM, PDQ exports) is a coherent follow-up and
roughly doubles this work, mostly in matching product names.

**Concurrent or floating licences, and usage metering.** There is no usage data to meter.

**Renewal emails or in-app notifications.** Renewals surface on the badge and the list, as
warranties do today.

**Converting existing `SFT-` assets** into licences.

**Encrypting keys at rest.** ASP.NET Data Protection would need its key ring persisted: in the same
database it protects little against a dump, and on a volume, losing the volume makes every stored
key permanently unreadable. The masked-and-audited approach is honest about what it protects.

**Assigning seats from the user or asset pages.** Assignment happens on the licence page; the asset
page shows device seats read-only.
