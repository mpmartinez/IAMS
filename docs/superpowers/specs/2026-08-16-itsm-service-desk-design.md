# AssetDesk Service Desk — Design

**Date:** 2026-08-16
**Status:** Approved for planning
**Mockups:** https://claude.ai/code/artifact/c78bd3a7-91a8-4a6e-bd43-a79e90740088

## Context

AssetDesk today tracks assets, assignments, warranties and maintenance for multi-tenant
customers. The target market is IT officers at manning (crewing) agencies — offices of
roughly 50–500 shore staff with IT departments of two to five people. They have no
existing ITSM tool, and both ISO 9001 and ISO/IEC 27001 certification are active drivers
for them.

This design adds a service desk to AssetDesk: a ticket queue for IT staff and a
report-a-problem channel for office users. The value is not ticketing in the abstract —
it is that a ticket in AssetDesk arrives already linked to a known asset, because the
requester scanned its QR sticker.

## Goal

Ship a service desk that a three-person IT department will actually use, built by
generalising the `Maintenance` entity rather than adding a parallel subsystem.

## Scope

**In scope**

- A `Ticket` entity generalising `Maintenance`, with three types: Incident, Request,
  SecurityEvent.
- Public/internal comment threads on tickets.
- An `Employee` role: office users who can file and follow their own tickets, excluded
  from seat metering.
- QR-scan-to-ticket: reporting from `Scan.razor` with the asset pre-linked.
- Request fulfilment that creates an `AssetAssignment` in the same transaction as ticket
  closure.
- An append-only `AuditLog` covering assets, assignments and tickets.
- Per-priority SLA targets with an overdue background job (phase 3).

**Explicitly out of scope**

Change management, problem management, knowledge base, service catalogue, CSAT surveys,
escalation matrices, agent workload balancing, ticket merging, and email-to-ticket
intake. Each is standard in mature ITSM products; none is needed by a three-person IT
team, and each roughly doubles the build.

## Data model

### `Ticket` — generalises `Maintenance`

```csharp
public class Ticket : ITenantEntity
{
    public int Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    // Per-tenant display number, e.g. TKT-0183. Distinct from Id, which is global.
    public int TicketNumber { get; set; }

    public string Type { get; set; } = TicketTypes.Incident;
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = TicketStatus.New;
    public string Priority { get; set; } = TicketPriority.Medium;

    // Optional: a Request may precede the asset it will be fulfilled with.
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
    public DateTime? DueAt { get; set; }        // computed from Priority; phase 3
    public DateTime? BreachedAt { get; set; }   // phase 3

    public string? Resolution { get; set; }

    // Set when a Request is fulfilled, linking the ticket to what it produced.
    public int? AssetAssignmentId { get; set; }
    public AssetAssignment? AssetAssignment { get; set; }

    public ICollection<TicketAttachment> Attachments { get; set; } = [];
    public ICollection<TicketComment> Comments { get; set; } = [];
}
```

Constant classes follow the existing `AssetStatus` / `MaintenanceStatus` pattern:

```csharp
public static class TicketTypes
{
    public const string Incident      = "Incident";
    public const string Request       = "Request";
    public const string SecurityEvent = "SecurityEvent";
    public static readonly string[] All = [Incident, Request, SecurityEvent];
    public static bool IsValid(string t) => All.Contains(t);
}

public static class TicketStatus
{
    public const string New        = "New";
    public const string Assigned   = "Assigned";
    public const string InProgress = "InProgress";
    public const string OnHold     = "OnHold";
    public const string Resolved   = "Resolved";
    public const string Closed     = "Closed";
    public const string Cancelled  = "Cancelled";
    public static readonly string[] All =
        [New, Assigned, InProgress, OnHold, Resolved, Closed, Cancelled];
    public static readonly string[] Open =
        [New, Assigned, InProgress, OnHold];
    public static bool IsValid(string s) => All.Contains(s);
}

public static class TicketPriority
{
    public const string Low      = "Low";
    public const string Medium   = "Medium";
    public const string High     = "High";
    public const string Critical = "Critical";
    public static readonly string[] All = [Low, Medium, High, Critical];
    public static bool IsValid(string p) => All.Contains(p);
}
```

**Status transitions.** `New → Assigned → InProgress → Resolved → Closed`, with `OnHold`
reachable from `Assigned` or `InProgress` and returning to `InProgress`. `Cancelled` is
reachable from any open status. `Resolved → InProgress` is allowed (reopening). Closed is
terminal. Transitions are validated server-side in `TicketService`; invalid transitions
return a failed `ApiResponse<T>` rather than throwing.

In phase 1, staff move tickets from `Resolved` to `Closed` manually. Auto-close after a
configurable idle period is deferred to phase 3 alongside the SLA job.

**Ticket numbering.** `TicketNumber` is assigned inside the creating transaction as
`MAX(TicketNumber) + 1` scoped to the tenant, protected by a unique index on
`(TenantId, TicketNumber)`. On unique-violation the insert retries up to three times.
This keeps numbers human-friendly and per-tenant without a database sequence per tenant.

### `TicketComment`

```csharp
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
    public bool IsInternal { get; set; }   // staff-only; never returned to a requester
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

Internal comments are filtered out server-side in the query, not hidden in the UI. A
requester fetching a ticket never receives internal comment rows.

### `AuditLog`

```csharp
public class AuditLog : ITenantEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string EntityType { get; set; } = "";   // "Asset", "Ticket", "AssetAssignment"
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";        // Created | Updated | Deleted
    public string? UserId { get; set; }
    public string? Changes { get; set; }            // JSON: { field: { from, to } }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
```

Populated by an EF Core `SaveChangesInterceptor` that inspects `ChangeTracker` entries
for an allow-list of audited entity types. No update or delete endpoints are exposed for
`AuditLog`; it is readable by `Admin` and `Auditor` only.

### `Asset` additions

- `OwnerUserId` (`string?`) — the person accountable for the asset, distinct from
  `AssignedToUserId`, which is whoever currently holds it. Required by ISO 27001 A.5.9.
- `LastVerifiedAt` (`DateTime?`) — stamped by a QR verification scan. The scan-to-verify
  UI is out of scope here; the field is added now so verification history starts
  accumulating.

### `Tenant` addition

- `MaxTicketsPerMonth` (`int`) — Free 100, Pro 1000, Enterprise `int.MaxValue`. Added to
  `SubscriptionTiers.GetLimits`, which changes that method's return tuple and every call
  site.

## Migration from `Maintenance`

A single EF migration renames rather than recreates, so existing rows and attachments
survive:

| From | To |
| --- | --- |
| table `Maintenances` | `Tickets` |
| table `MaintenanceAttachments` | `TicketAttachments` |
| `MaintenanceAttachment.MaintenanceId` | `TicketAttachment.TicketId` |
| `Maintenance.PerformedByUserId` | `Ticket.AssignedToUserId` |
| `Maintenance.CreatedByUserId` | `Ticket.RequesterUserId` |
| `Maintenance.Notes` | folded into `Ticket.Resolution` |
| `Maintenance.CompletedAt` | `Ticket.ResolvedAt`, copied also to `ClosedAt` |

New columns are added with defaults: `Type = "Incident"`, `Priority = "Medium"`.
`TicketNumber` is backfilled per tenant ordered by `CreatedAt`.

Status values are remapped in the migration:

| Old | New |
| --- | --- |
| `Pending` | `New` |
| `InProgress` | `InProgress` |
| `Completed` | `Closed` |
| `Cancelled` | `Cancelled` |

The `Maintenance` entity, `MaintenanceController` and `MaintenanceAttachmentsController`
are removed. `/maintenance` redirects to `/tickets?type=Incident` so existing bookmarks
and muscle memory keep working, and the sidebar shows a single **Tickets** item in place
of **Maintenance**.

## Roles and metering

Add `Roles.Employee` to the existing `SuperAdmin | Admin | Management | Staff | Auditor`
set, and include it in `TenantAssignable`.

An `Employee` may create a ticket, read their own tickets, and add non-internal comments
to them. They may not list other users' tickets, assign, change status, or read anything
under `/assets`.

**Metering.** `SubscriptionService.CanCreateUserAsync` and `UpdateUserCountAsync` today
count every `ApplicationUser` in the tenant against `Tenant.MaxUsers`. Both must exclude
users whose only role is `Employee`; otherwise a 200-person agency exhausts the 25-seat
Pro cap on day one. `GetUsageAsync` gets the same exclusion so the usage bar stays
truthful.

A new `CanCreateTicketAsync(Guid tenantId)` counts tickets created in the current
calendar month against `Tenant.MaxTicketsPerMonth`, matching the shape of the existing
`CanCreateAssetAsync`.

## API surface

All endpoints return the existing `ApiResponse<T>` / `PagedResponse<T>` wrappers.

**`TicketsController`** — `/api/tickets`

| Method | Route | Policy |
| --- | --- | --- |
| `GET` | `/api/tickets` (filters: type, status, priority, assignee, assetId, search) | `Staff` |
| `GET` | `/api/tickets/mine` | authenticated |
| `GET` | `/api/tickets/{id}` | `Staff`, or requester of that ticket |
| `GET` | `/api/tickets/summary` (open, unassigned, in-progress, overdue) | `Staff` |
| `POST` | `/api/tickets` | authenticated |
| `PUT` | `/api/tickets/{id}` | `Staff` |
| `POST` | `/api/tickets/{id}/assign` | `Staff` |
| `POST` | `/api/tickets/{id}/status` | `Staff` |
| `POST` | `/api/tickets/{id}/resolve` | `Staff` |
| `POST` | `/api/tickets/{id}/fulfil` (Request only; assigns an asset) | `CanManageAssets` |

**`TicketCommentsController`** — `/api/tickets/{ticketId}/comments`: `GET` and `POST`.
`IsInternal = true` requires `Staff`.

**`TicketAttachmentsController`** — mirrors the existing
`MaintenanceAttachmentsController`, reusing `FileStorageService` and the
`CanUploadFileAsync` quota check.

A new authorization policy `CanFileTickets` covers every authenticated role including
`Employee`. The existing `Staff` policy (`Admin` or `Staff`) gates queue operations.

## Web surface

| Route | Page | Access |
| --- | --- | --- |
| `/tickets` | `Pages/Tickets/Index.razor` — summary cards, filters, queue table | `Staff` |
| `/tickets/{id}` | `Pages/Tickets/View.razor` — detail, activity thread, asset panel, actions | `Staff` |
| `/report` | `Pages/Tickets/Report.razor` — type picker then short form | any authenticated |
| `/my-tickets` | `Pages/Tickets/Mine.razor` — requester's own list | any authenticated |

`Scan.razor` gains a **Report a problem** action on the resolved-asset panel, navigating
to `/report?assetId={id}`. `Index.razor` (dashboard) gains open and overdue ticket tiles.
`MainLayout.razor` replaces the **Maintenance** nav item with **Tickets**, visible to
`Admin,Staff`, and shows **Report a problem** plus **My reports** for `Employee`.

Existing components are reused rather than replaced: summary cards and filter row from
`Maintenance.razor`, status pill styling from `MainLayout`, `FileUpload`, `Modal`,
`EmptyState`, `Badge`, `Snackbar`.

## Flows

**Incident.** An employee opens `/report`, picks *Equipment issue*, optionally links an
asset by search or by arriving from a QR scan, and submits. The ticket lands as
`New`. Staff assign it, work it, and resolve it with a resolution note. Sending a ticket
to maintenance sets the linked asset's status to `Maintenance`; resolving the ticket sets
that asset to `InUse` if it still has an assignee and `Available` if it does not.

**Request.** An employee asks for equipment. The ticket has no asset. Staff fulfil it by
selecting an `Available` asset: within one transaction the app creates an
`AssetAssignment`, sets the asset to `InUse`, stores `Ticket.AssetAssignmentId`, sets
`Resolution`, and moves the ticket to `Closed`. If any step fails the whole transaction
rolls back and the ticket stays open.

**Security event.** Minimal form, available to every role, defaulting to `High` priority.
Choosing *lost or stolen device* and linking an asset also sets that asset's status to
`Lost`.

## Notifications

Reuses the existing `Notification` entity and `NotificationService`. Events: ticket
assigned to you; public comment added to a ticket you requested or are assigned;
ticket resolved. `RelatedEntityType` is `"Ticket"`, `Link` is `/tickets/{id}`. Internal
comments never generate a requester notification.

## Phases

**Phase 1 — Ticket core.** Entities, migration from `Maintenance`, `TicketService`,
controllers, DTOs, and the staff queue and detail pages. `AuditLog` and its interceptor
ship here, first, because history not captured cannot be recovered later. Ships as a
strictly better maintenance module.

**Phase 2 — Employee portal.** `Roles.Employee`, the `SubscriptionService` metering
exclusions, `MaxTicketsPerMonth`, the `/report` and `/my-tickets` pages, the `Scan.razor`
report action, and the dashboard tiles. This is the demo.

**Phase 3 — SLA and polish.** Per-priority target hours stored on `Tenant`, `DueAt`
computed at creation and on priority change, an overdue background service modelled on
`WarrantyCheckService`, breach notifications, the overdue dashboard tile, and auto-close
of resolved tickets after an idle period.

Phases 1 and 2 together are the pilot. Phase 3 is what makes it read as ITSM to a buyer.

## Testing

- `TicketService` status-transition table: every valid transition succeeds, every invalid
  one returns a failed response and leaves state unchanged.
- Request fulfilment rolls back completely when assignment creation fails.
- Ticket numbering stays unique and per-tenant under concurrent creation.
- An `Employee` cannot read another user's ticket, cannot see internal comments, and
  cannot reach any queue endpoint.
- Tenant isolation holds for tickets, comments, attachments and audit logs.
- Users whose only role is `Employee` do not count toward `MaxUsers`.
- The migration preserves existing maintenance rows, attachments and status meanings.

## Assumptions

- Manning agencies run one AssetDesk tenant each; multi-branch offices are one tenant with
  `Asset.Location` distinguishing sites. A first-class `Site` entity is deferred.
- Office users authenticate with the same identity provider as staff. Anonymous or
  email-based ticket submission is not supported.
- Ticket volume per tenant stays in the low thousands per year, so the queue can be
  served by ordinary paged queries without a search index.
