# Audit trail: read API and admin browse page

Date: 2026-09-06

## Problem

`AuditSaveChangesInterceptor` has been writing append-only `AuditLog` rows for
`Asset`, `AssetAssignment`, `Ticket`, `TicketComment` and `TicketAttachment` since
the tickets work, and `UsersController` writes an explicit `PasswordResetSent` row.
Nothing reads any of it. There is no controller, no DTO, no permission key and no
page, so the evidence accumulates in a table nobody can open.

`/api/assignments/audit` is not this: it pages `AssetAssignment` rows, not `AuditLog`.

This design covers the missing read half only. Widening `AuditedTypes`, per-entity
history timelines on the asset and ticket detail pages, retention and CSV export are
all deliberately out of scope.

## Permission

A new first-class key, `iams:audit:view`, in a new "Audit" group so it appears in the
`/admin/roles` matrix and stays tunable per tenant.

`Permissions.DefaultsFor` gives `Admin` and `SuperAdmin` every key already, so new
tenants pick it up for free. `Auditor` is granted it explicitly: a role named Auditor
that cannot open the audit trail is a contradiction.

Existing tenants are the problem. `SeedData.EnsureRolePermissionsAsync` returns early
whenever `Tenant.RolePermissionsSeededAt` is non-null, and never re-runs, so a key
added to the catalog today reaches no tenant provisioned yesterday. Every current
Admin would silently lack it.

So the migration backfills with raw SQL:

```sql
INSERT INTO "RolePermissions" ("Id","RoleId","TenantId","Permission")
SELECT gen_random_uuid(), r."Id", t."Id", 'iams:audit:view'
FROM "Tenants" t CROSS JOIN "AspNetRoles" r
WHERE r."IsBuiltIn" AND r."Name" IN ('Admin','SuperAdmin','Auditor')
  AND NOT EXISTS (SELECT 1 FROM "RolePermissions" x
                  WHERE x."RoleId" = r."Id" AND x."TenantId" = t."Id"
                    AND x."Permission" = 'iams:audit:view');
```

Backfilling unconditionally is safe precisely because the key is new: no tenant can
have deliberately revoked something that did not exist, so there is no admin decision
to trample. That reasoning does not generalise - a future key that renames or splits
an existing one must not copy this migration blindly.

Built-in roles carry a null `TenantId` and are shared across tenants, hence the cross
join against `Tenants` rather than a join on `RoleId`. `Down` deletes the same rows.

This is the first permission-backfill migration in the repository and sets the pattern
the CLAUDE.md gotcha has been asking for.

`Program.cs` gains `.RequirePermission("CanViewAuditLog", Permissions.AuditView)`.

## DTO

`AssetDesk.Shared/DTOs/AuditLogDto.cs`:

- `AuditLogDto` - `Id`, `TenantId`, `EntityType`, `EntityId`, `Action`, `UserId`,
  `UserName`, `Timestamp`, `List<AuditChangeDto> Changes`
- `AuditChangeDto` - `Field`, `From`, `To`

The `Changes` JSON blob is parsed server-side into `AuditChangeDto`. The interceptor
truncates long string values and writes null for Created and Deleted, so a row can
legitimately carry no changes and can in principle carry a malformed blob; parsing it
in one place on the server keeps that handling out of the Blazor page. A blob that
fails to parse yields an empty list rather than failing the request - a page of audit
history must not disappear because one row is unreadable.

`UserName` is resolved by a second query over the distinct `UserId` values on the
returned page. `AuditLog` has no navigation to `ApplicationUser` on purpose: users can
be deleted and the audit row must outlive them. An id that no longer resolves renders
as "Unknown user"; a null id (a background job or system action) renders as "System".

## Endpoint

`GET /api/audit`, gated on `CanViewAuditLog`, returning
`ApiResponse<PagedResponse<AuditLogDto>>`.

Query parameters: `page` (default 1), `pageSize` (default 25, capped at 100),
`entityType`, `entityId`, `action`, `userId`, `fromDate`, `toDate`.

Ordering is `Timestamp DESC, Id DESC`. The tie-break is load-bearing, not decoration:
a single `SaveChanges` writes a whole batch with one `DateTime.UtcNow` per row taken
microseconds apart, and PostgreSQL is free to return equal-timestamp rows in any order,
which would let a row shuffle between pages and appear twice or not at all.

Filters compose as AND. `entityId` without `entityType` is accepted but matches across
types; the UI always sends them together.

Tenant scoping comes from the existing global query filter, as in every other
controller. That filter is bypassed for `SuperAdmin`, who therefore sees every tenant's
rows interleaved. Rather than leave that silently misleading, `TenantId` is on the DTO
and the page renders a tenant column only when the viewer is a super-admin.

A companion `GET /api/audit/filters` returns the distinct `EntityType` and `Action`
values present in the tenant, so the dropdowns show what actually exists rather than a
hardcoded list that drifts from `AuditedTypes`.

## Web

`ApiClient.GetAuditLogAsync(...)` alongside the existing paged getters, following the
`ApiResponse<PagedResponse<T>>` unwrapping that `GetTicketsAsync` uses.

`/admin/audit` (`Pages/Admin/AuditLog.razor`), following the `Users.razor` and
`Roles.razor` conventions: a filter bar (entity type, action, date range, user), a
table of Timestamp / User / Action / Entity, and an expandable row showing the
field-level `from -> to` diff. The whole page is wrapped in
`<PermissionView Permission="iams:audit:view" ShowDeniedMessage="true">`, and a
matching gated `NavItem` joins the Admin section of `MainLayout`.

Any new arbitrary-value Tailwind class means running `build:css` and bumping both the
`?v=` on the stylesheet link and `cacheName` in `service-worker.published.js`.

## Tests

`AuditLogReadTests.cs`, exercising the controller directly over `TestDb.Create` like
the rest of the suite:

- tenant isolation - a tenant-scoped provider never sees another tenant's rows
- each filter narrows correctly, and filters compose
- paging, including that equal timestamps order deterministically by `Id`
- `Changes` parsing: a real blob, a null blob, a malformed blob
- an unresolvable `UserId` falls back to "Unknown user"; a null one to "System"
- `pageSize` above the cap is clamped

Plus `PermissionCatalogTests` gains the new key's presence, and its
`BuiltInRoles_HaveTheExpectedGrantCount` case for `Auditor` moves from 3 to 4.

The suite runs on in-memory SQLite, so it does not prove the backfill migration's raw
SQL applies on PostgreSQL - `gen_random_uuid()` and the quoted identifiers are
Postgres-specific and were checked by reading the generated SQL, not by a green test.
