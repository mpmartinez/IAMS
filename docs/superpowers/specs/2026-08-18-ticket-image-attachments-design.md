# Ticket Image Attachments: Capture, Upload, and Viewing

**Date:** 2026-08-18
**Status:** Approved

## Problem

Ticket attachments are half-built, and the half that exists is invisible.

**Uploads go into a void.** `Pages/Tickets/Report.razor` uploads attachments successfully. The API
stores them, and `SubscriptionService.CanUploadFileAsync` meters them against the tenant's storage
quota. But `Services/ApiClient.cs` contains exactly one attachment method, `UploadTicketAttachmentAsync`
— no list, no download, no delete. `Pages/Tickets/View.razor` and `Pages/Tickets/Index.razor` contain
zero references to attachments. The API's `GetAttachments`, `DownloadAttachment`, and
`DeleteAttachment` endpoints exist and are never called by anything.

The result: an employee attaches photos to a ticket, those photos consume tenant storage, and nobody
— not the requester, not the IT staff working the ticket — can ever see them.

**Correction from an earlier draft of this document:** it claimed the camera button rejects the photos
it takes, and proposed a new `js/imageResize.js` to fix that. Both were wrong. `Report.razor` already
downscales client-side using Blazor's built-in
`IBrowserFile.RequestImageFileAsync(file.ContentType, 1600, 1600)`, with a `catch` that falls back to
the original for formats the browser cannot resize (HEIC), plus a `MaxTotalBytes = 20 MB` aggregate
cap. A 1600px re-encode lands well under the 5 MB per-file limit, so oversized photos are already
handled and no new JavaScript is needed. This makes extraction more valuable, not less: the other
surfaces inherit working downscaling for free.

**The good implementation is trapped in one page.** `Report.razor` is 666 lines, roughly 200 of them
a well-considered attachment picker: camera capture, multi-select, thumbnail previews with remove,
per-file and aggregate size caps, and sequential upload with partial-failure reporting (its comment
notes that offices are on poor connections and parallel uploads of large photos saturate the link).
None of it is reachable from the two other places a ticket is created or viewed.

## Solution

Extract the existing picker, fix the two defects, and wire attachments to every ticket surface.

### Decisions

| Decision | Choice | Why |
|---|---|---|
| Camera | Native `capture="environment"` | Already implemented and working in `Report.razor`. Opens the OS camera on phones, handles orientation and permissions for free, zero JS. Falls back to the file picker on desktop. |
| Oversized photos | Already solved — keep `RequestImageFileAsync` | Blazor's built-in canvas downscale is already in `Report.razor` at 1600px. Nothing to build; extraction spreads it to the other surfaces. |
| Who uploads | The ticket creator | Stated requirement. Staff working the queue view but do not upload. |
| When | At creation, and afterwards by the requester on their own ticket | Covers IT replying "can you send a photo of the error?" after the ticket is filed. |
| Delete | The uploader only | It is the requester's photo, and possibly a sensitive one. IT working the queue can view it but cannot remove it, so an attachment cannot be quietly dropped from a ticket by anyone but the person who put it there. |
| Code reuse | One shared picker component | A second and third copy is how the Users role dropdown drifted from `Roles.TenantAssignable`. |

## Components

### `Components/TicketAttachmentPicker.razor` (new, extracted)

The pending-attachment picker, lifted from `Report.razor` with its behavior preserved: camera input,
multi-file input, thumbnail strip with per-item remove, `MaxFiles` and per-file/aggregate size
validation, and inline error text.

Parameters:

- `Files` (`List<PendingAttachment>`) and `FilesChanged` — two-way bound so the host page owns the list
  and decides when to upload.
- `Disabled` (bool) — hosts set this while submitting.
- `MaxFiles` (int, default 6) — matches the current constant in `Report.razor`.

`Report.razor`'s private nested `PendingFile` class moves out to `Components/PendingAttachment.cs`,
renamed `PendingAttachment`, so all three hosts share one shape: `Id`, `Name`, `ContentType`,
`Data` (byte[]), `PreviewUrl`. The rename is deliberate — as a shared public type sitting next to
`TicketAttachmentDto`, "PendingFile" reads as unrelated to attachments.

The component does not upload. Uploading is the host's job because the three surfaces differ: two
upload after creating a ticket, one uploads immediately against an existing ticket id.

### `Components/TicketAttachmentGallery.razor` (new)

Displays an existing ticket's attachments. Images render as a thumbnail grid; non-images render as
file rows with name, size, and type. Clicking an image opens it enlarged in the existing `Modal`
component. Each item offers download, and offers delete only when the viewer may delete it.

Parameters: `TicketId` (int) and `OnChanged` (EventCallback, so the host can refresh counts).

The component decides the delete affordance itself rather than taking a predicate from the host: the
rule is uploader-only, so it compares each `TicketAttachmentDto.UploadedByUserId` against the current
user's id from `AuthenticationStateProvider`. Every host would otherwise pass the identical callback,
and a host that got it wrong would offer a control the API rejects.

### Downscaling — no new code

Nothing is built here. The extracted picker carries `Report.razor`'s existing logic verbatim:

- `image/*` files go through `file.RequestImageFileAsync(file.ContentType, 1600, 1600)`, Blazor's
  built-in canvas resize.
- The surrounding `try`/`catch` falls back to the original file when the browser cannot resize the
  format (HEIC is the case the existing comment names), leaving the per-file size check as the
  backstop. Downscaling is an optimisation, never a gate.
- Non-images — PDF, DOC, DOCX, TXT — pass through untouched.

The constants move with the code and become the component's defaults: `MaxFiles = 6`,
`MaxFileSizeBytes = 5 MB` (matching `FileStorageService`), `MaxTotalBytes = 20 MB`,
`MaxImageDimension = 1600`.

## API Changes

### `ApiClient` (three methods added)

The endpoints already exist; only the client is missing.

| Method | Calls |
|---|---|
| `GetTicketAttachmentsAsync(int ticketId)` | `GET /api/tickets/{ticketId}/attachments` |
| `DownloadTicketAttachmentAsync(int ticketId, int attachmentId)` | `GET .../{attachmentId}/download` |
| `DeleteTicketAttachmentAsync(int ticketId, int attachmentId)` | `DELETE .../{attachmentId}` |

Read methods follow the file's existing shape (`ApiResponse<T>` unwrapped to `T?`); the delete method
returns `(bool Success, string? Error)` like the other write methods. Error bodies are read
defensively — a 403 from the authorization middleware has an empty body, which is what broke the role
methods previously.

### `TicketAttachmentsController.DeleteAttachment` (rule replaced)

This is a **swap, not a widening**. Today the action is `[Authorize(Policy = "CanManageTicketQueue")]`
— staff can delete, the uploader cannot. It becomes uploader-only:

```
if (attachment.UploadedByUserId != CurrentUserId)
    return Forbid();
```

The permission attribute is removed from the action and this check moves into the body. Deleting still
removes the stored file and the row, so metered storage is genuinely reclaimed.

Note both halves of the swap. The requester **gains** the ability to remove a file they uploaded.
Queue managers **lose** the ability to remove anyone else's — deliberately, so an attachment cannot be
quietly dropped from a ticket by anyone but the person who put it there.

**Known consequence, accepted:** if an employee uploads something genuinely inappropriate and then
leaves — `UsersController.DeleteUser` soft-deletes by setting `IsActive = false`, and
`AuthController` blocks inactive users from signing in — nobody can remove that file through the UI.
It would need a database or storage-level intervention. Widening this later to include SuperAdmin is a
one-line change if that becomes a real problem; it is not added pre-emptively.

No other API change. No migration: `TicketAttachment` already carries everything needed, including
`UploadedByUserId`.

## Surfaces

| Page | Picker | Gallery | Notes |
|---|---|---|---|
| `Pages/Tickets/Report.razor` | yes (already) | no | Swaps its inline markup and file-handling code for the component. Behavior must be identical afterwards — this page is the reference implementation, not a beneficiary. |
| `Pages/Tickets/Index.razor` (New Ticket dialog) | yes | no | Staff creating a ticket are its creator, so they attach here. Uploads after create, same order `Report.razor` uses. |
| `Pages/Tickets/View.razor` | requester only | yes | Everyone with access to the ticket sees the gallery. The Add control renders only when the viewer is the requester and `TicketStatus.Open.Contains(ticket.Status)` — so nothing can be attached to a Resolved, Closed, or Cancelled ticket. |

### A deliberate UI/API asymmetry

`UploadAttachment` permits `CanManageQueue || ticket.RequesterUserId == CurrentUserId`, so the API
allows IT staff to upload to any ticket. The UI deliberately offers the Add control only to the
requester, per the "the creator uploads" rule.

This is recorded rather than silently applied because elsewhere on this codebase the rule is that the
UI must not offer an action the API will reject — this is the reverse, and it is intentional. The API
is left permissive so that a staff-side upload affordance can be added later without an API change,
and so a queue manager retains the ability if it is ever surfaced. If staff upload should be
impossible rather than merely unoffered, the endpoint needs tightening too; that is not done here.

The New Ticket dialog and `Report.razor` must upload *after* the ticket is created, because
attachments are keyed by ticket id. Both follow the pattern already in `Report.razor`: create the
ticket, then upload sequentially, and report attachment failures separately without undoing the
ticket. A ticket that was created but whose photos failed is still a filed ticket.

## Error Handling

- **Partial upload failure.** Already handled in `Report.razor` and preserved in the extracted flow:
  count successes and failures, and report "N of M attachments uploaded" rather than a bare failure.
- **Over-limit after downscaling.** Rare, but a 5 MB scanned PDF cannot be shrunk. The existing
  per-file message names the offending file and skips it, leaving the rest of the batch intact.
- **Storage quota exhausted.** The API returns "Storage limit reached for your subscription." That
  message reaches the user unchanged rather than being replaced with a generic string.
- **Gallery load failure.** The gallery shows an inline error with a retry rather than throwing —
  `GetFromJsonAsync` throws on non-success, so these calls are wrapped.

## Testing

API-side, in `tests/AssetDesk.Api.Tests`, following the existing controller-test pattern (controllers
constructed directly with a `ClaimsPrincipal` on `ControllerContext`):

- The uploader can delete their own attachment.
- A different user cannot delete it (403), and the attachment survives.
- **A holder of `iams:tickets:manage` who is not the uploader cannot delete it** — this is the half of
  the rule that changed, so it needs a test that would fail against today's staff-only behavior.
- Deleting removes both the row and the stored file, so quota is reclaimed.

Each test must be verified to discriminate by breaking the predicate it targets and confirming failure.

The picker, gallery, and downscaling are Blazor and JavaScript, and this solution still has no test
project for the Web assembly. They are verified by running the app: attach a file over 5 MB and
confirm it is accepted after downscaling, confirm the gallery lists it, confirm a non-owner sees no
delete control. This is stated plainly rather than implied to be covered.

## Build Order

1. `PendingAttachment` + `TicketAttachmentPicker` extracted from `Report.razor`; `Report.razor` consumes it.
2. `ApiClient` read/download/delete methods.
3. `DeleteAttachment` uploader-only rule + tests.
4. `TicketAttachmentGallery`.
5. `View.razor` — gallery, plus the requester-only Add control.
6. `Index.razor` New Ticket dialog — picker + upload-after-create.

Step 1 is deliberately first and behavior-preserving: it should produce an identical `Report.razor`
experience, which makes it easy to tell whether a later step broke something.

## Out of Scope

- In-app `getUserMedia` camera preview. The native capture attribute covers the phone case, which is
  where photos are actually taken.
- Attachment categories in the UI. The API requires a category and `Report.razor` hardcodes `"Other"`;
  the picker keeps that until there is a reason to expose the lookup.
- Editing or annotating images.
- Thumbnail generation server-side. Downscaled uploads are small enough to serve directly.
