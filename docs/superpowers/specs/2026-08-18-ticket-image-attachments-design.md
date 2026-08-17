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

**The camera rejects the photos it takes.** `Report.razor` already has a "Take Photo" button using
`capture="environment"`. It also enforces `MaxFileSizeBytes = 5 * 1024 * 1024`, matching the server.
A modern phone camera produces 3–8 MB JPEGs, so the button frequently produces a file its own page
rejects with *"…is over the 5 MB limit and wasn't added."*

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
| Oversized photos | Downscale in the browser | The alternative is raising the server limit, which consumes tenant storage ~10× faster on a metered quota. |
| Who uploads | The ticket creator | Stated requirement. Staff working the queue view but do not upload. |
| When | At creation, and afterwards by the requester on their own ticket | Covers IT replying "can you send a photo of the error?" after the ticket is filed. |
| Delete | Owner or `iams:tickets:manage` | An employee who attaches the wrong or a sensitive photo can currently only have it removed by asking IT. |
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

Parameters: `TicketId` (int), `CanDelete` (Func<TicketAttachmentDto, bool>), `OnChanged`
(EventCallback, so the host can refresh counts).

### `wwwroot/js/imageResize.js` (new)

One exported function:

```
downscaleImage(dataUrl, maxEdge, quality) -> Promise<{ dataUrl, width, height, bytes }>
```

Draws the image to a canvas capped at `maxEdge` on its longest side and re-encodes as JPEG at
`quality`. Defaults: `maxEdge = 1920`, `quality = 0.8`.

1920px is chosen so a serial-number label photographed at arm's length stays legible — the limiting
factor for this app's actual use, which is reading asset tags and seeing physical damage. An 8 MB
phone photo lands around 300–600 KB.

Rules:

- Only `image/*` inputs are downscaled. PDFs, DOCX, and TXT pass through untouched.
- An image already under the max edge is still re-encoded only if that makes it smaller; otherwise the
  original bytes are kept, so a small PNG screenshot is not needlessly degraded into a larger JPEG.
- If the canvas step throws (corrupt image, exotic format), the original file is used unchanged and the
  server's size check remains the backstop. Downscaling is an optimisation, never a gate.

Both constants live in one place in the picker so the quality floor can be retuned without hunting
through pages.

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

### `TicketAttachmentsController.DeleteAttachment` (loosened)

Currently `[Authorize(Policy = "CanManageTicketQueue")]`, a flat permission check. It becomes an
ownership-or-permission check, matching what `TicketCommentsController` and the attachment read paths
already do:

```
if (!User.HasPermission(Permissions.TicketsManage) && attachment.UploadedByUserId != CurrentUserId)
    return Forbid();
```

The permission attribute is removed from the action and the ownership check moves into the body, so a
requester can delete a file they uploaded. Deleting still removes the stored file and the row, so
metered storage is genuinely reclaimed.

No other API change. No migration: `TicketAttachment` already carries everything needed, including
`UploadedByUserId`.

## Surfaces

| Page | Picker | Gallery | Notes |
|---|---|---|---|
| `Pages/Tickets/Report.razor` | yes (already) | no | Swaps its inline markup for the component. Gains downscaling. Behavior otherwise unchanged. |
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

API-side, in `tests/IAMS.Api.Tests`, following the existing controller-test pattern (controllers
constructed directly with a `ClaimsPrincipal` on `ControllerContext`):

- The uploader can delete their own attachment.
- A different non-manager user cannot delete it (403), and the attachment survives.
- A holder of `iams:tickets:manage` can delete anyone's attachment.
- Deleting removes both the row and the stored file, so quota is reclaimed.

Each test must be verified to discriminate by breaking the predicate it targets and confirming failure.

The picker, gallery, and downscaling are Blazor and JavaScript, and this solution still has no test
project for the Web assembly. They are verified by running the app: attach a file over 5 MB and
confirm it is accepted after downscaling, confirm the gallery lists it, confirm a non-owner sees no
delete control. This is stated plainly rather than implied to be covered.

## Build Order

1. `PendingAttachment` + `TicketAttachmentPicker` extracted from `Report.razor`; `Report.razor` consumes it.
2. `imageResize.js` and its wiring into the picker.
3. `ApiClient` read/download/delete methods.
4. `DeleteAttachment` ownership check + tests.
5. `TicketAttachmentGallery`.
6. `View.razor` — gallery, plus the requester-only Add control.
7. `Index.razor` New Ticket dialog — picker + upload-after-create.

Step 1 is deliberately first and behavior-preserving: it should produce an identical `Report.razor`
experience, which makes it easy to tell whether a later step broke something.

## Out of Scope

- In-app `getUserMedia` camera preview. The native capture attribute covers the phone case, which is
  where photos are actually taken.
- Attachment categories in the UI. The API requires a category and `Report.razor` hardcodes `"Other"`;
  the picker keeps that until there is a reason to expose the lookup.
- Editing or annotating images.
- Thumbnail generation server-side. Downscaled uploads are small enough to serve directly.
