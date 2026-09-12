# Procurement: suppliers, purchase orders, and goods receipt

Date: 2026-09-12

## Problem

An asset exists in AssetDesk only once somebody types it in or imports a spreadsheet.
Purchase price, purchase date and currency are fields filled in after the fact, and the
buying itself is invisible: there is no record of who was ordered from, what was ordered,
what arrived, or what is still outstanding.

Nothing models it today. `supplier`, `vendor`, `purchaseorder` and `requisition` return no
hits in `src` outside two comments about the peso-only decision and one doc comment on
`Asset.ExchangeRate`.

The cost is double entry and a register that is accurate only by discipline. Ten laptops
are ordered in an email, eight arrive, and somebody remembers to type eight asset records
with the right price and date - or does not.

This design covers **suppliers, purchase orders, and goods receipt creating assets**.
Requisitions and the approval chain are a separate sub-project; see Out of Scope.

## What it builds on

**Multi-currency** (`2026-09-11-multi-currency-design.md`) established that an amount is
recorded in the currency it was invoiced in, with the rate it was booked at, and that the
peso value is derived as `PurchasePrice * ExchangeRate`. A purchase order is placed in a
supplier's currency, so this feature is the second consumer of that decision.

**Ticket fulfilment** (`TicketService.Fulfilment.cs`) already closes an equipment `Request`
by issuing an **existing, Available** asset from stock. Procurement is its complement -
acquiring an asset that does not exist yet. The two are deliberately not wired together in
this slice; see Out of Scope.

`FulfilAsync` also sets the pattern this design's receive operation follows, for the same
reason it gives: ticket closure, assignment creation and the asset status change are one
transaction because "a partial success here would leave the assignment history lying, which
is the one thing this system exists to prevent."

## Entities

Five new, all `ITenantEntity` with the same global query filter shape every other tenant
entity uses.

**`Supplier`** - `Name` (required), `ContactName`, `Email`, `Phone`, `Address`, `Notes`,
`IsActive`. Unique on `(TenantId, Name)`.

**`PurchaseOrder`** - `PoNumber` (per-tenant sequential, rendered `PO-0042`), `SupplierId`,
`Currency`, `Status`, `OrderDate`, `ExpectedDate`, `Notes`, `CreatedByUserId`.

**`PurchaseOrderLine`** - `PurchaseOrderId`, `DeviceType`, `Description`, `Quantity`,
`UnitPrice`, `ReceivedQuantity`.

`ReceivedQuantity` is a running total maintained by the receive operation rather than
derived by summing receipt lines. It is the value the over-receipt guard reads inside the
transaction, and a derived sum would have to be recomputed under the same lock to be safe -
the denormalisation is what makes the guard cheap and correct.

**`GoodsReceipt`** - `PurchaseOrderId`, `ReceiptDate`, `ExchangeRate`, `ReceivedByUserId`,
`Notes`.

**`GoodsReceiptLine`** - `GoodsReceiptId`, `PurchaseOrderLineId`, `QuantityReceived`.

**`Asset`** gains one nullable `GoodsReceiptLineId`. Many assets point at one receipt line -
receiving ten units of a line creates ten assets, all pointing at it. A hand-entered or
imported asset keeps it null, which is honest: the system then knows which assets it can
account for and which it cannot.

`PoNumber` is allocated by a `PurchaseOrderNumberAllocator` following
`TicketNumberAllocator` exactly - `IgnoreQueryFilters()` plus an explicit tenant filter,
`MaxAsync(...) + 1`. That is a read-then-write and races under concurrency; as with tickets,
the unique index on `(TenantId, PoNumber)` is what actually holds, turning a lost race into
a failed insert rather than a duplicate number.

## Receiving

This is the feature. Everything else is data entry around it.

The whole operation runs inside `_db.Database.CreateExecutionStrategy().ExecuteAsync(...)`,
as `FulfilAsync` does and for the reason its comment gives: production is Npgsql with
`EnableRetryOnFailure`, and EF Core refuses a user-initiated transaction under a retrying
strategy, so the whole unit is handed to the strategy. The body can therefore run more than
once and must re-read everything from a cleared change tracker on each attempt.

In one transaction:

1. Create the `GoodsReceipt` with its date, rate and receiver.
2. For each line being received, create a `GoodsReceiptLine`.
3. For each **unit** received, create an `Asset`.
4. Increment each `PurchaseOrderLine.ReceivedQuantity`.
5. Recompute and set the `PurchaseOrder.Status`.

A partial success would create assets the order does not know it produced, or advance a
line's received count without the assets to match. Either leaves the register lying.

**The over-receipt guard is a conditional claim inside the transaction, not a pre-check.**
A friendly pre-check rejecting "you ordered 10 and have received 10" is good UX and is not
the invariant - the same distinction `FulfilAsync` draws between its availability message
and the conditional claim that actually holds. Two concurrent receipts must not both claim
the last unit.

Each created asset takes:

| Field | Value |
|---|---|
| `AssetTag` | generated (see below) |
| `DeviceType` | the PO line's |
| `Name` | the PO line's `Description` |
| `Status` | `Available` |
| `PurchasePrice` | the PO line's `UnitPrice` |
| `Currency` | the **purchase order's** currency |
| `ExchangeRate` | the **receipt's** rate |
| `PurchaseDate` | the receipt's date |
| `GoodsReceiptLineId` | the receipt line just created |

The rate comes from the receipt, not the order, because an invoice states the rate it was
booked at and the invoice arrives with the goods. It is also the only arrangement partial
receipt can express honestly: eight laptops in January and two in April against the same USD
order genuinely cost different pesos, and each batch's assets carry the rate actually booked.

## Status

A transition table in the shape of `TicketWorkflow`, which is the house pattern for this:

```
Draft            -> Ordered, Cancelled
Ordered          -> PartiallyReceived, Received, Cancelled
PartiallyReceived-> Received, Cancelled
Received         -> (terminal)
Cancelled        -> (terminal)
```

`PartiallyReceived` and `Received` are set **by the receive operation**, never by hand:
every line fully received means `Received`, any progress short of that means
`PartiallyReceived`. A status a user could set independently of the quantities would
immediately disagree with them.

Receiving is rejected against a `Draft` order (nothing has been ordered yet) and against a
`Cancelled` one.

## Asset tag generation

Tag generation exists **twice** today: a private `GenerateAssetTagAsync` in
`AssetsController.cs:480`, and a second in `AssetImportService.cs:179` that additionally
carries a sequence cache so a bulk import does not re-query per row.

Receiving creates many assets at once and would be the third copy. It gets one shared
`AssetTagGenerator` instead, extracted from the two existing implementations, with the
importer's cache behaviour as the base since that is the closer fit. Both existing callers
move onto it.

This is included deliberately rather than deferred. The multi-currency work shipped the same
aggregation in four places, two copies were missed, and the final review caught it as a
Critical. A third copy of tag generation is the same bet.

## Permissions

Two new keys, following the `UsersView`/`UsersManage` and `RolesView`/`RolesManage` pairs:

- `iams:procurement:view` - see suppliers, purchase orders and receipts
- `iams:procurement:manage` - create and edit them, and receive goods

One backfill migration covers both, copying `20260912050134_GrantDepreciationManagePermission`,
which in turn copies `GrantAuditViewPermission`: a cross join against `Tenants` because
built-in roles carry a null `TenantId`, and `md5(...)::uuid` rather than
`gen_random_uuid()` because the database is supplied externally with no version pinned and
determinism makes the insert idempotent.

Backfilling unconditionally is safe for the same single reason it was there: both keys are
brand new, so no tenant can have deliberately revoked them.

`Admin` and `SuperAdmin` receive both through `DefaultsFor`'s full-catalog bundle. `Staff`
gets `view` and `manage` explicitly - running the asset estate is what the role is for, and
a Staff user who cannot receive a delivery cannot do the job. `Auditor` gets `view` only:
read-only oversight.

## Surfaces

- **Supplier list and editor** at `/suppliers`.
- **Purchase order list** at `/purchase-orders`, and a detail page carrying the lines, their
  outstanding quantities, the receipts so far, and the receive action.
- **PO PDF** - the reason a purchase order exists is to be sent to a supplier. Built with
  `PdfReportService`'s existing helpers (`BuildDocument`, `AddHeaderRow`, `AddBodyCell`,
  `FormatCurrency`), the same way the five reports are.
- **Asset detail** gains a line naming the supplier and PO an asset came from, when it has one.

## Testing

The receive operation carries the weight:

- receiving part of a line leaves the order `PartiallyReceived` with the right outstanding
  quantity; receiving the rest closes it to `Received`
- receiving more than was ordered is rejected, including when it would pass a pre-check but
  not the in-transaction claim
- receiving against a `Draft` or `Cancelled` order is rejected
- the right number of assets is created, each with the line's price, the order's currency,
  the receipt's rate and the receipt's date
- a USD order received at two different rates produces assets carrying each batch's own rate
- a failure part-way creates nothing - no orphan assets, no advanced `ReceivedQuantity`

Plus: the status transition table (every legal and illegal move, as `TicketWorkflowTests`
does); `PoNumber` allocation per tenant; supplier uniqueness within a tenant and
independence across tenants; and that the extracted `AssetTagGenerator` produces the same
tags the two existing implementations did, so the refactor is behaviour-preserving.

Tenant isolation gets explicit tests on every controller, exercised through a super-admin
provider. The depreciation feature shipped a Critical precisely here: its policy controller
relied on the global query filter, which has an `IsSuperAdmin()` bypass, and all its tests
used a single tenant - so production code and tests shared the blind spot and a SuperAdmin
could silently rewrite another organisation's data.

The suite runs on in-memory SQLite via `TestDb.Create`, so a green run does not prove a
migration applies on PostgreSQL, and it does not prove transactional behaviour under a real
provider either. The generated SQL is read by hand.

## Out of Scope

**Requisitions and the approval chain.** Deferred to their own sub-project by decision.
Approval rules are the most organisation-specific part of procurement and the least reusable,
and the ordering loop is worth proving before governance is layered on it.

**Linking a `Request` ticket to a purchase.** The natural flow - a request that cannot be
filled from stock triggers an order, and receiving fulfils the request - needs the fulfilment
path to accept a not-yet-existing asset, which is a change to `FulfilAsync`'s invariants.
Separate spec.

**Supplier invoices, payments and credit terms.** A `GoodsReceipt` records what arrived, not
what was billed or paid. Three-way matching is an accounts-payable feature.

**Requests for quotation, and returns to supplier.** Never discussed, not modelled.

**Reversing a receipt.** Receiving ten laptops creates ten asset records in one action, and
the only undo is deleting them individually. A reversal that retires the created assets and
decrements the line is a coherent feature and is not in this slice.
