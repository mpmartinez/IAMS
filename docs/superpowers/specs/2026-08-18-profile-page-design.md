# Profile Page — Design

**Date:** 2026-08-18
**Status:** Approved

## Problem

IAMS has no account page. Everything a user might want to know or change about their own account
is either invisible or scattered:

- Their department, tenant, role and join date exist in the database but are never shown to them.
- Change-password is a key icon in the sidebar footer, unlabelled and easy to miss.
- "Sign out everywhere" exists in the API (`POST /api/auth/logout-all`) but has no UI at all.
- A user cannot correct their own name or department. `PUT /api/users/{id}` is gated behind the
  `CanManageUsers` policy, so a typo in a name requires an admin.

## Scope

A `/profile` page showing the signed-in user's account, letting them edit their own full name and
department, and collecting the account actions that already exist.

**In scope:** the page, its entry point, a self-service update endpoint, and tests for that
endpoint.

**Out of scope:** avatar upload, notification preferences, theme setting (already in the top bar),
assigned-assets and open-tickets summaries (already at `/users/{id}/assets` and `/my-tickets`),
and viewing *another* user's profile — `/profile` is always the signed-in user.

## Architecture

### Route and entry point

New page at `/profile` (`src/IAMS.Web/Pages/Profile.razor`), `[Authorize]` with no role or
permission gate — every authenticated user has an account to look at.

The sidebar footer user card in `MainLayout.razor` becomes a `NavLink` to `/profile`, gaining a
hover state and closing the mobile menu on click. The change-password key icon is removed from the
sidebar; its function moves into the page's Security section. The logout button stays in the
sidebar — signing out should not require a page navigation.

`MainLayout`'s `_showChangePassword` field, `OpenChangePassword` method and
`<ChangePasswordDialog>` instance become unreachable once the key icon is gone, so they are
removed from `MainLayout` and the dialog is instantiated by `Profile.razor` instead.

### Page structure

Single column, `max-w-3xl`, `space-y-6` — the stacked-card idiom used by `Assets/View.razor` and
`Assignments/UserAssets.razor`. Four sections:

1. **Identity header** — avatar circle with initials, full name, email, role badge, tenant name.
   The role→colour map is the one already inlined in `MainLayout` (Admin purple, Auditor teal,
   else blue). Rather than copy it, it becomes a `RoleBadge.razor` component in
   `Components/UI/` taking a `Role` string, and `MainLayout` is changed to use it — so the two
   places a role is displayed cannot drift apart.
2. **Personal details** — full name and department. Read-only text by default with an Edit button;
   Edit swaps them to inputs with Save and Cancel. Cancel restores the values loaded from the
   server.
3. **Account** — email, role, member since (`CreatedAt`), account status. Read-only, with a line
   noting an administrator changes these.
4. **Security** — "Change password" (opens the existing `ChangePasswordDialog`) and "Sign out
   everywhere", the latter behind a confirmation since it invalidates the user's other sessions.

### Data flow

On init the page calls `GET /api/auth/me` for a fresh `UserDto` rather than reading the cached
copy from local storage, so a role or department changed by an admin since login is shown
correctly.

Saving posts to the new `PUT /api/auth/me` and gets the updated `UserDto` back. The page then
hands that DTO to `AuthService.UpdateCachedUserAsync`, which overwrites the `currentUser` key in
local storage and calls `NotifyAuthenticationStateChanged()`. `AuthStateProvider` builds its
`ClaimTypes.Name` claim from that stored `UserDto`, so the sidebar name updates immediately
without a reload and without minting a new token.

### API

One new endpoint on `AuthController`:

```csharp
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[HttpPut("me")]
public async Task<ActionResult<ApiResponse<UserDto>>> UpdateCurrentUser(UpdateProfileDto dto)
```

It resolves the user from `ClaimTypes.NameIdentifier` on the token, exactly as the sibling
`GetCurrentUser` and `ChangePassword` actions do. There is no id parameter, so it cannot be aimed
at another account.

New DTO in `AuthDto.cs`:

```csharp
public record UpdateProfileDto
{
    public required string FullName { get; init; }
    public string? Department { get; init; }
}
```

The DTO carries only these two fields. Role, Email, IsActive and TenantId are absent from the
contract entirely, so a crafted payload has nothing to bind to and self-escalation is impossible
by construction rather than by a filter that could later be edited away.

Server-side validation: `FullName` required, trimmed, 1–100 characters; `Department` optional,
trimmed, ≤100 characters, empty string stored as null. Violations return
`BadRequest(ApiResponse<UserDto>.Fail(...))` — matching how `ChangePassword` reports failure.
`UpdatedAt` is stamped on save.

### Client

- `ApiClient.UpdateProfileAsync(UpdateProfileDto)` → `(bool Success, UserDto? User, string? Error)`,
  following the tuple-return shape used by the other mutating methods in that file.
- `ApiClient.LogoutAllAsync()` → `bool`, wrapping the existing `POST /api/auth/logout-all`.
- `AuthService.UpdateCachedUserAsync(UserDto)` — writes local storage and notifies.

### States

- **Loading:** skeleton cards matching `UserAssets.razor`'s `skeleton` class treatment.
- **Load failure:** centred message with a Retry button; the page does not render a half-empty
  form over missing data.
- **Save in flight:** Save button shows its `Loading` state, inputs disabled.
- **Save failure:** inline red panel above the fields, in the style `ChangePasswordDialog` already
  uses. Edit mode stays open so the entered values are not lost.
- **Save success:** snackbar via `SnackbarService`, fields return to read-only.
- **Client validation:** name required and ≤100 chars, checked before the request so the common
  mistake does not need a round trip. The server re-checks regardless.

## Testing

API tests in `tests/IAMS.Api.Tests/ProfileSelfServiceTests.cs`, following the existing pattern of
instantiating the controller directly over a real Identity store on a test `AppDbContext`
(as `UsersControllerRoleAssignmentTests` does):

1. A valid update changes `FullName` and `Department` and returns the updated `UserDto`.
2. The endpoint updates only the caller's own record — a second user in the database is untouched.
3. Role and `IsActive` are unchanged after an update, guarding the escalation boundary.
4. Blank or whitespace-only `FullName` is rejected with `BadRequest`.
5. `FullName` over 100 characters is rejected.
6. An empty-string `Department` is stored as null, not as `""`.
7. A token whose user id no longer resolves returns `NotFound`.

The page itself has no test project to live in — the solution has no Blazor component tests — so
it is verified by building the solution and exercising the page in the running app.

## Consequences

- One more endpoint on `AuthController`, which is already the home for self-scoped operations
  (`me`, `change-password`, `logout-all`), so it does not introduce a new pattern.
- The sidebar loses its change-password shortcut, trading one click for a labelled, discoverable
  location.
- `UpdateProfileDto` is deliberately narrower than `UpdateUserDto`. They should stay separate —
  widening the profile DTO to share the admin one would reintroduce exactly the escalation risk
  this design avoids.
