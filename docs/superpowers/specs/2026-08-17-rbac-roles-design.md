# Permission-Based RBAC with Custom Roles

**Date:** 2026-08-17
**Status:** Approved

## Problem

Authorization in IAMS is hard-coded in three places that drift apart:

1. `src/IAMS.Api/Entities/Roles.cs` — six role name constants.
2. `src/IAMS.Api/Program.cs:113-139` — about twenty policies, each a literal `RequireRole(...)` list.
3. The Blazor UI — `AuthorizeView Roles="Admin,Staff,Management,Auditor,SuperAdmin"` strings scattered
   through `Layout/MainLayout.razor`, plus a hand-maintained role dropdown in `Pages/Users.razor:176`
   that already carries a comment admitting it drifts from `Roles.TenantAssignable`.

A tenant cannot tailor access. "Staff without delete" is unrepresentable. Adding a role means editing
Identity seeding, every policy that should include it, and every `AuthorizeView` in the UI.

## Solution

Replace role checks with permission checks. Roles become containers of permissions. Each tenant may
edit what the built-in roles grant and may define its own roles.

### Decisions

| Decision | Choice | Why |
|---|---|---|
| Roles per user | Exactly one | `UserDto.Role` stays a string; the users grid, badges, and edit modal are unchanged. |
| Permission delivery | JWT claims | Blazor reads the same claims the API enforces, so no extra round trip and the PWA still gates UI offline. |
| Built-in vs custom | Built-in global, custom per-tenant | Keeps SuperAdmin short-circuit and seeding intact; tenants can still tailor and extend. |
| Policy conversion | Keep policy names, redefine as permissions | Controllers using `Policy = "..."` need no edit, so the diff stays reviewable. |

## Permission Catalog

The catalog lives in code at `src/IAMS.Api/Authorization/Permissions.cs` as static
`{ Key, Group, Label, Description }` descriptors. Only the **grants** (role to permission) are stored
in the database.

Rationale: a permission is only real if some policy checks it. A DB-stored catalog would drift from
the policies exactly the way the Users dropdown drifted from `Roles.TenantAssignable`. Code is the
single source of truth for what permissions exist; the database owns who has them.

Keys follow the `iams:resource:action` convention **already present in the codebase** — see
`PermissionView` usages at `MainLayout.razor:75` and `Pages/Assets/View.razor:65`, and the comments at
`AssetsController.cs:84,155,224,287` and `UsersController.cs:235`. Adopting the existing convention
rather than introducing a second one is the whole point of this document.

| Group | Keys |
|---|---|
| Assets | `iams:assets:view`, `iams:assets:create`, `iams:assets:edit`, `iams:assets:delete`, `iams:assets:import`, `iams:assets:debug` |
| Assignments | `iams:assignments:view`, `iams:assignments:assign`, `iams:assignments:return` |
| Tickets | `iams:tickets:file`, `iams:tickets:queue`, `iams:tickets:manage` |
| Reports | `iams:reports:view` |
| Users | `iams:users:view`, `iams:users:manage`, `iams:users:read` |
| Roles | `iams:roles:view`, `iams:roles:manage` |
| Attachments | `iams:attachments:manage` |
| Warranty | `iams:warranty:manage`, `iams:warranty:delete` |
| Notifications | `iams:notifications:test` |

Platform-level endpoints stay on `RequireRole("SuperAdmin")` and are deliberately absent from the
catalog: `TenantsController` (whole controller) and `LookupsController` write actions. They are not
tenant-tunable, so listing them in a tenant's permission matrix would imply control that does not exist.

`Permissions.DefaultsFor(string builtInRole)` returns the default grant set for each built-in role,
chosen to reproduce current access exactly. It is used by both the migration backfill and tenant
provisioning.

## Data Model

### `ApplicationRole : IdentityRole`

New fields:

- `Guid? TenantId` — `null` for the six built-in roles, set for custom roles.
- `bool IsBuiltIn`
- `string? Description`

This requires swapping `IdentityRole` for `ApplicationRole` in `Program.cs:54` (`AddIdentity`),
`Program.cs:225` (`RoleManager<>`), and `Data/SeedData.cs`.

### `RolePermission`

```
Id         Guid    PK
RoleId     string  FK -> AspNetRoles
TenantId   Guid    FK -> Tenants
Permission string
```

Unique index on `(RoleId, TenantId, Permission)`.

### Resolution

A user's permission set is a single query with no fallback branch:

```
RolePermissions.Where(rp => rp.RoleId == user's role && rp.TenantId == user's tenant)
```

Built-in roles have their default rows **materialized per tenant**, rather than resolved by falling
back to a global default when a tenant has no override rows. The fallback scheme cannot represent an
empty grant set: a tenant admin unchecking every box on Auditor would produce zero rows, which is
indistinguishable from "never customized" and would silently restore the defaults.

Cost is at most 132 rows per tenant (6 roles x 22 permissions), which is negligible.

SuperAdmin keeps its existing bypass and is not permission-resolved.

## Authorization

`PermissionRequirement` plus an `AuthorizationHandler` that succeeds when either:

- the user is in role `SuperAdmin` (existing bypass, unchanged), or
- the user holds a `permission` claim whose value equals the required key.

The claim type is `permission`, matching what `PermissionView.razor:33` already reads.

### Policies redefined (no controller edits)

| Policy | Now requires |
|---|---|
| `CanCreateAssets` | `iams:assets:create` |
| `CanEditAssets` | `iams:assets:edit` |
| `CanDeleteAssets` | `iams:assets:delete` |
| `CanManageAssets` | `iams:assets:edit` |
| `CanViewReports` | `iams:reports:view` |
| `CanAssignAssets` | `iams:assignments:assign` |
| `CanReturnAssets` | `iams:assignments:return` |
| `CanViewAssignments` | `iams:assignments:view` |
| `CanFileTickets` | `iams:tickets:file` |
| `CanViewUsersList` | `iams:users:read` |
| `Admin` | `iams:assets:debug` (its only call site, `AssetsController.cs:459`) |

### Import split out

`AssetsController.cs:243` (bulk `.xlsx` import) currently shares `CanCreateAssets` with single-asset
creation. It moves to `iams:assets:import` so a tenant can allow one without the other. Defaults grant
`iams:assets:import` to exactly the roles that hold `iams:assets:create`, so no one loses access.

### `Staff` policy retired

`Staff` is overloaded. It guards asset reads (`AssetsController.cs:23`, `:72`) *and* the ticket queue
(`TicketsController.cs:42,72,159,171,183`, `TicketAttachmentsController.cs:173`). Redefining it as one
permission would weld "can see assets" to "can work the ticket queue", making the two permanently
inseparable for every tenant. The policy is removed and its eight call sites set explicitly:

- `AssetsController.cs:23`, `:72` → `iams:assets:view`
- `TicketsController.cs:42`, `:72` → `iams:tickets:queue`
- `TicketsController.cs:159`, `:171`, `:183`, `TicketAttachmentsController.cs:173` → `iams:tickets:manage`

### Role attributes converted

Ten `[Authorize(..., Roles = "...")]` attributes become policies:

| Site | Policy requires |
|---|---|
| `UsersController.cs:23`, `:129` | `iams:users:view` |
| `UsersController.cs:73`, `:148`, `:215` | `iams:users:manage` |
| `AttachmentsController.cs:75`, `:172` | `iams:attachments:manage` |
| `WarrantyAlertsController.cs:100`, `:130` | `iams:warranty:manage` |
| `WarrantyAlertsController.cs:158` | `iams:warranty:delete` |
| `NotificationsController.cs:144` | `iams:notifications:test` |

### Left alone

`Auditor`, `TenantAdmin`, and `CanManageOrgSettings` have zero call sites in the API. They are noted
here but not touched; removing them is unrelated cleanup.

## Token and Staleness

`TokenService.GenerateTokenAsync` resolves the user's permissions and emits one `permission` claim per
permission. At most 22 short claims, roughly 400 bytes of token growth.

`AuthStateProvider.GetAuthenticationStateAsync` must copy those `permission` claims out of the JWT
into the `ClaimsIdentity` it builds, alongside the role claims it already copies at lines 59-72.
Without that step the API would enforce permissions the UI cannot see.

**Query-filter trap.** `RolePermission` must **not** get a global query filter, and every read must
filter `TenantId` explicitly. `TokenService` runs during login, when the HTTP context is still
unauthenticated, so `TenantProvider.GetCurrentTenantId()` returns null and `IsSuperAdmin()` returns
false. EF evaluates the filter's provider call eagerly into a query parameter, so the
`_tenantProvider == null` guard does not short-circuit (see the comment in `tests/IAMS.Api.Tests/TestDb.cs:21-26`).
A filtered `RolePermission` would therefore match zero rows at login and hand every user an empty
permission set.

Staleness is handled differently by blast radius:

- **A user's own role changed** — affects one person. `UsersController.UpdateUser` calls the existing
  `TokenService.RevokeAllUserTokensAsync` so the change takes effect on their next refresh.
- **A role's permission set edited** — affects potentially every user in the tenant. Accept the natural
  JWT lifetime (`Jwt:ExpireMinutes`, currently 30) rather than mass-logout a tenant mid-work.

## API

New `RolesController`:

| Endpoint | Policy | Behaviour |
|---|---|---|
| `GET /api/roles` | `roles.view` | Built-in roles plus this tenant's custom roles, each with its permission keys and a user count. |
| `GET /api/roles/assignable` | `users.manage` | Thin `{ name, description }` list for the Users dropdown. Gated by who can assign, not who can read roles. |
| `POST /api/roles` | `roles.manage` | Creates a custom role. Name unique per tenant, `TenantId` set, `IsBuiltIn = false`. |
| `PUT /api/roles/{id}` | `roles.manage` | Updates description and the full permission list. For built-in roles both name and description are fixed, so only grants change. |
| `DELETE /api/roles/{id}` | `roles.manage` | Custom roles only. Returns 409 with the user count if any user holds it. |
| `GET /api/permissions` | `roles.view` | The catalog, grouped, for the matrix UI. |

New DTOs in `IAMS.Shared/DTOs/RoleDto.cs`: `RoleDto`, `CreateRoleDto`, `UpdateRoleDto`,
`PermissionDto`, `PermissionGroupDto`.

### Guardrails

- **No privilege escalation.** A tenant admin cannot grant a permission they do not themselves hold.
  Without this, anyone with `roles.manage` could mint a role holding every permission and assign it to
  themselves. SuperAdmin is exempt.
- **No cross-tenant writes.** A role whose `TenantId` is neither null nor the caller's tenant is a 404.
- **SuperAdmin grants are immutable**, including by another SuperAdmin, so the bypass cannot be
  weakened by a mistake in the UI.
- **Built-in roles cannot be deleted or renamed.**

## Web UI

### `/admin/roles`

New page, gated on `roles.view`. Follows the `Pages/Admin/Lookups.razor` pattern and uses
`Components/UI`.

- Table: name, Built-in/Custom badge, description, permission count, user count, edit and delete actions.
- Editor drawer: name, description, and a permission matrix — checkboxes grouped by resource with a
  select-all toggle per group. Built-in roles open the same drawer with the name field locked.
- Permissions the current user does not hold render disabled, matching the server-side escalation guard
  so the UI cannot offer an action the API will reject. SuperAdmin sees everything enabled.
- Built-in roles show name and description read-only; only the matrix is editable.

### `Users.razor`

The role `<select>` is fed from `GET /api/roles/assignable`, removing the hand-maintained option list
and its drift comment at line 176.

### `PermissionView` component (already exists — must be fixed)

`src/IAMS.Web/Shared/PermissionView.razor` already exists and already reads a `permission` claim. It is
**modified, not created**.

Today nothing emits a `permission` claim, so the check on line 33 always fails and the component falls
through to `user.IsInRole("Admin")` on line 38. Two consequences:

- The Reports nav link (`MainLayout.razor:75`, `iams:reports:view`) is Admin-only today, even though
  `CanViewReports` also grants Auditor. Auditors cannot see a link to a page they may open.
- The asset delete button (`Pages/Assets/View.razor:65`, `iams:assets:delete`) is Admin-only, which
  matches `CanDeleteAssets` by coincidence rather than design.

The `IsInRole("Admin")` fallback must be **removed** once real claims are emitted. Left in place it
would grant every Admin every permission regardless of what their tenant configured, which defeats the
feature. Both existing call sites keep working unchanged, because the keys they use are in the catalog.

The component then replaces the hard-coded role lists in `MainLayout.razor` nav entries and in
page-level `[Authorize(Roles = "...")]` attributes.

## Testing

In `tests/IAMS.Api.Tests`, using the existing SQLite-backed `TestDb` and `FakeTenantProvider`:

- Permission resolution returns only rows matching both role and tenant.
- The migration backfill reproduces current access for all six built-in roles, asserted against the
  policy-to-permission table above.
- The privilege-escalation guard rejects granting a permission the actor lacks, and allows it for
  SuperAdmin.
- Deleting a role held by at least one user returns 409.
- A tenant cannot read or write another tenant's custom role.
- Editing one tenant's copy of a built-in role does not affect another tenant's copy.

## Build Order

1. `Permissions` catalog and `DefaultsFor`.
2. `ApplicationRole`, `RolePermission`, Identity swap, migration with backfill.
3. `PermissionRequirement`, handler, policy redefinitions, `Staff` retirement, role-attribute conversion.
4. `TokenService` claim emission and refresh-token revocation on role change.
5. `RolesController`, DTOs, guardrails.
6. Tenant provisioning materializes rows for new tenants.
7. `PermissionView`, `MainLayout` and page guard conversion.
8. `/admin/roles` page.
9. `Users.razor` dropdown from the API.

## Pre-existing State Discovered During Planning

The repo already contains a half-built version of this feature, which this design absorbs rather than
duplicates:

- `src/IAMS.Web/Shared/PermissionView.razor` — reads a `permission` claim nothing emits.
- Two live call sites using `iams:reports:view` and `iams:assets:delete`.
- Comments at `AssetsController.cs:84,155,224,287` and `UsersController.cs:235` naming
  `iams:assets:create`, `iams:assets:edit`, `iams:assets:delete`, `iams:reports:view`,
  `iams:users:read`.

The catalog above uses exactly this naming so the existing artifacts become correct instead of being
superseded.

## Notes

`CLAUDE.md` states the database is SQLite. It is Postgres (`Program.cs:29` uses `UseNpgsql`; the
initial migration is `InitialPostgres`). Out of scope here, but the migration work below targets
Postgres.
