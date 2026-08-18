# Ticket Image Attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ticket attachments visible and manageable, and make the capture experience that exists on one page available wherever a ticket is created.

**Architecture:** The attachment picker already exists inside `Pages/Tickets/Report.razor` — camera capture, multi-select, client-side downscaling, size caps, thumbnails. Extract it into one shared component, add the three `ApiClient` methods that were never written, build a gallery, and wire both to the ticket detail page and the staff New Ticket dialog.

**Tech Stack:** .NET 10, Blazor WebAssembly, ASP.NET Core Web API, EF Core + Npgsql, xUnit, Tailwind.

**Spec:** `docs/superpowers/specs/2026-08-18-ticket-image-attachments-design.md`

## Global Constraints

- **No new JavaScript.** Downscaling already works via Blazor's built-in `IBrowserFile.RequestImageFileAsync(file.ContentType, 1600, 1600)`. An earlier draft of the spec wrongly proposed a JS resize helper; do not add one.
- **Task 1 is behavior-preserving.** After extraction, `Report.razor` must behave exactly as before. It is the reference implementation, not a beneficiary.
- Constants carried from `Report.razor`, unchanged: `MaxFiles = 6`, `MaxFileSizeBytes = 5 * 1024 * 1024`, `MaxTotalBytes = 20 * 1024 * 1024`, `MaxImageDimension = 1600`, `AttachmentAccept = "image/*,.pdf,.doc,.docx,.txt"`.
- **Attachment delete is uploader-only.** This is a swap: the requester gains the ability, queue managers lose it for other people's files.
- Attachments are keyed by ticket id, so any create-then-attach flow uploads **after** the ticket exists, and an attachment failure never undoes a created ticket.
- Uploads are **sequential, never parallel** — the existing comment explains why: these offices are on poor connections and a batch of large photos saturates the link.
- The API caps a request at 10 MB (`[RequestSizeLimit]`) and a file at 5 MB (`FileStorageService`, `MaxFileSizeMB: 5`).
- Build: `dotnet build`. Tests: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj` (currently 179/179).
- There is **no test project for the Blazor assembly**. Blazor work is verified by build plus running the app; never imply test coverage that does not exist.

## File Structure

**Create:**
- `src/AssetDesk.Web/Components/PendingAttachment.cs` — the shared pending-file model
- `src/AssetDesk.Web/Components/TicketAttachmentPicker.razor` — camera + multi-select + previews
- `src/AssetDesk.Web/Components/TicketAttachmentGallery.razor` — view/download/delete existing attachments
- `tests/AssetDesk.Api.Tests/TicketAttachmentDeleteTests.cs`

**Modify:**
- `src/AssetDesk.Web/Pages/Tickets/Report.razor` — consume the picker, delete the extracted code
- `src/AssetDesk.Web/Services/ApiClient.cs` — add list/download/delete
- `src/AssetDesk.Api/Controllers/TicketAttachmentsController.cs:183-185` — uploader-only delete
- `src/AssetDesk.Web/Pages/Tickets/View.razor` — gallery + requester-only add
- `src/AssetDesk.Web/Pages/Tickets/Index.razor` — picker in the New Ticket dialog

---

### Task 1: Extract the attachment picker, and add the two shared values it needs

**Files:**
- Create: `src/AssetDesk.Web/Components/PendingAttachment.cs`, `src/AssetDesk.Web/Components/TicketAttachmentPicker.razor`, `src/AssetDesk.Web/Components/TicketAttachmentDefaults.cs`
- Modify: `src/AssetDesk.Shared/DTOs/TicketDto.cs`, `src/AssetDesk.Web/Pages/Tickets/Report.razor`
- Test: `tests/AssetDesk.Api.Tests/TicketDtoIsOpenTests.cs`

**Why the two shared values are here.** Tasks 5 and 6 both need "is this ticket still open?" and both need the
default attachment category. Writing either inline in each page would put a literal status list and a magic
string in three files — the same drift that has already bitten this codebase twice. Both get one definition
now, before the first consumer exists.

**Interfaces:**
- Consumes: nothing
- Produces: `AssetDesk.Web.Components.PendingAttachment` with `Guid Id`, `string Name`, `string ContentType`, `byte[] Data`, `string? PreviewUrl`. `TicketAttachmentPicker` with parameters `List<PendingAttachment> Files`, `EventCallback<List<PendingAttachment>> FilesChanged`, `bool Disabled`, `int MaxFiles` (default 6).

This task moves code. Nothing about the user-visible behavior of `Report.razor` may change.

- [ ] **Step 0a: Add `IsOpen` to the shared ticket DTO**

In `src/AssetDesk.Shared/DTOs/TicketDto.cs`, add to `TicketListItemDto` beside the existing computed
`Reference` property:

```csharp
    /// <summary>
    /// Whether the ticket is still being worked. Mirrors AssetDesk.Api.Entities.TicketStatus.Open.
    ///
    /// Duplicated here rather than referenced because AssetDesk.Web cannot see AssetDesk.Api. It lives on the
    /// DTO so there is exactly one copy shared by both projects instead of a status list pasted into
    /// each page, and TicketDtoIsOpenTests pins it against TicketStatus.Open so the two cannot drift.
    /// </summary>
    public bool IsOpen => Status is "New" or "Assigned" or "InProgress" or "OnHold";
```

- [ ] **Step 0b: Pin `IsOpen` against the API's canonical list**

Create `tests/AssetDesk.Api.Tests/TicketDtoIsOpenTests.cs`:

```csharp
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;

namespace AssetDesk.Api.Tests;

public class TicketDtoIsOpenTests
{
    private static TicketListItemDto WithStatus(string status) => new()
    {
        Type = TicketType.Incident,
        Category = TicketCategory.Other,
        Title = "t",
        Status = status,
        Priority = TicketPriority.Medium
    };

    [Fact]
    public void IsOpen_AgreesWithTicketStatusOpen_ForEveryStatus()
    {
        // TicketDto.IsOpen duplicates TicketStatus.Open because AssetDesk.Web cannot reference
        // AssetDesk.Api. This test is what makes that duplication safe: add a status to one side
        // without the other and it fails here rather than silently in the UI.
        foreach (var status in TicketStatus.All)
        {
            var expected = TicketStatus.Open.Contains(status);
            Assert.Equal(expected, WithStatus(status).IsOpen);
        }
    }

    [Fact]
    public void IsOpen_IsFalse_ForAnUnknownStatus()
    {
        Assert.False(WithStatus("NoSuchStatus").IsOpen);
    }
}
```

- [ ] **Step 0c: Run the pinning test**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter TicketDtoIsOpenTests`

Expected: PASS, 2 tests. A failure here means the literal list in `IsOpen` disagrees with
`TicketStatus.Open` — fix `IsOpen`, not the test.

- [ ] **Step 0d: Add the default attachment category constant**

Create `src/AssetDesk.Web/Components/TicketAttachmentDefaults.cs`:

```csharp
namespace AssetDesk.Web.Components;

/// <summary>
/// Values every page that uploads a ticket attachment needs. One definition so the three upload
/// sites (Report, the New Ticket dialog, the ticket detail page) cannot disagree.
/// </summary>
public static class TicketAttachmentDefaults
{
    /// Mirrors AssetDesk.Api.Entities.TicketAttachmentCategories.Other. The API requires a category and
    /// the picker does not expose the lookup, so every upload from the Web client sends this one.
    public const string Category = "Other";
}
```

- [ ] **Step 1: Create the shared model**

Create `src/AssetDesk.Web/Components/PendingAttachment.cs`:

```csharp
namespace AssetDesk.Web.Components;

/// <summary>
/// A file chosen in the browser but not yet uploaded. Attachments are keyed by ticket id, so a
/// page that creates a ticket has to hold its files here until the ticket exists.
/// Extracted from Pages/Tickets/Report.razor, where it was a private nested PendingFile.
/// </summary>
public sealed class PendingAttachment
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Data { get; init; }

    /// A data: URL for images, used for the thumbnail. Null for non-images.
    public string? PreviewUrl { get; init; }
}
```

- [ ] **Step 2: Create the picker component**

Create `src/AssetDesk.Web/Components/TicketAttachmentPicker.razor`. The markup and logic are lifted verbatim from `Report.razor` lines 213-278 and its file-handling methods:

```razor
@* Camera capture, multi-select, client-side downscaling and thumbnails for pending ticket
   attachments. Extracted from Pages/Tickets/Report.razor so the New Ticket dialog and the
   ticket detail page get the same behaviour instead of a second and third copy of it.

   This component does not upload. The three hosts differ - two create a ticket and then upload,
   one uploads against a ticket that already exists - so uploading stays with the host. *@

<div class="space-y-1.5">
    <label class="block text-sm font-medium text-slate-700 dark:text-slate-300">
        Attachments <span class="text-slate-400 font-normal">(optional)</span>
    </label>
    <p class="text-xs text-slate-400 dark:text-slate-500">
        A photo is often faster than a description. Up to @MaxFiles files, @(MaxFileSizeBytes / (1024 * 1024)) MB each.
    </p>

    <div class="flex gap-2">
        <label for="@_cameraInputId"
               class="flex-1 flex items-center justify-center gap-2 px-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-600 text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors text-sm cursor-pointer">
            <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z" />
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 13a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            Take Photo
        </label>
        <InputFile id="@_cameraInputId" class="sr-only" accept="image/*" capture="environment"
                   OnChange="OnPhotoCaptured" disabled="@Disabled" />

        <label for="@_filesInputId"
               class="flex-1 flex items-center justify-center gap-2 px-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-600 text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors text-sm cursor-pointer">
            <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
            </svg>
            Choose Files
        </label>
        <InputFile id="@_filesInputId" class="sr-only" accept="@AttachmentAccept" multiple
                   OnChange="OnFilesChosen" disabled="@Disabled" />
    </div>

    @if (!string.IsNullOrEmpty(_attachError))
    {
        <p class="text-xs text-red-600 dark:text-red-400">@_attachError</p>
    }

    @if (Files.Count > 0)
    {
        <div class="flex flex-wrap gap-2 pt-1">
            @foreach (var f in Files)
            {
                <div @key="f.Id" class="relative">
                    @if (f.PreviewUrl is not null)
                    {
                        <img src="@f.PreviewUrl" alt="@f.Name" class="w-16 h-16 object-cover rounded-lg border border-slate-200 dark:border-slate-600" />
                    }
                    else
                    {
                        <div class="w-16 h-16 flex flex-col items-center justify-center gap-1 rounded-lg border border-slate-200 dark:border-slate-600 bg-slate-50 dark:bg-slate-700 px-1">
                            <svg class="w-5 h-5 text-slate-400 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M7 21h10a2 2 0 002-2V9.414a1 1 0 00-.293-.707l-5.414-5.414A1 1 0 0012.586 3H7a2 2 0 00-2 2v14a2 2 0 002 2z" />
                            </svg>
                            <span class="text-[9px] leading-tight text-slate-500 dark:text-slate-400 truncate w-full text-center" title="@f.Name">@f.Name</span>
                        </div>
                    }
                    <button type="button" @onclick="() => RemoveFile(f)" disabled="@Disabled"
                            class="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full bg-slate-700 dark:bg-slate-600 text-white flex items-center justify-center hover:bg-red-600 transition-colors disabled:opacity-50">
                        <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="3" d="M6 18L18 6M6 6l12 12" />
                        </svg>
                    </button>
                </div>
            }
        </div>
    }
</div>

@code {
    [Parameter] public List<PendingAttachment> Files { get; set; } = new();
    [Parameter] public EventCallback<List<PendingAttachment>> FilesChanged { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public int MaxFiles { get; set; } = 6;

    // Matches the API's allow-list in FileStorageService (jpeg/png/gif/webp + pdf/doc/docx/txt)
    // so the picker never offers a type UploadAttachment will reject.
    private const string AttachmentAccept = "image/*,.pdf,.doc,.docx,.txt";
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // matches FileStorageService's default 5 MB per-file limit
    private const long MaxTotalBytes = 20 * 1024 * 1024;
    private const int MaxImageDimension = 1600; // client-side downscale target for photos

    private string? _attachError;
    private readonly string _cameraInputId = $"attach-camera-{Guid.NewGuid():N}";
    private readonly string _filesInputId = $"attach-files-{Guid.NewGuid():N}";

    private async Task OnPhotoCaptured(InputFileChangeEventArgs e)
    {
        if (e.File is not null)
        {
            await AddFile(e.File);
        }
    }

    private async Task OnFilesChosen(InputFileChangeEventArgs e)
    {
        // Pass a generous ceiling here purely so GetMultipleFiles doesn't throw on a large
        // selection - our own MaxFiles check below is what actually enforces the cap.
        foreach (var file in e.GetMultipleFiles(50))
        {
            await AddFile(file);
        }
    }

    private async Task AddFile(IBrowserFile file)
    {
        _attachError = null;

        if (Files.Count >= MaxFiles)
        {
            _attachError = $"You can attach up to {MaxFiles} files.";
            return;
        }

        var isImage = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        try
        {
            // Downscale photos client-side: phone camera shots run 5-10MB, which is slow
            // to upload on the poor connections these offices have and eats into the
            // tenant's storage quota. Non-image files (PDF/DOC/TXT) pass through as-is.
            var source = file;
            if (isImage)
            {
                try
                {
                    source = await file.RequestImageFileAsync(file.ContentType, MaxImageDimension, MaxImageDimension);
                }
                catch
                {
                    // Some browsers/formats can't be resized (e.g. HEIC) - fall back to the original
                    // and let the size/type checks below decide whether it can be attached.
                    source = file;
                }
            }

            if (source.Size > MaxFileSizeBytes)
            {
                _attachError = $"\"{file.Name}\" is over the {MaxFileSizeBytes / (1024 * 1024)} MB limit and wasn't added.";
                return;
            }

            var currentTotal = Files.Sum(f => (long)f.Data.Length);
            if (currentTotal + source.Size > MaxTotalBytes)
            {
                _attachError = $"Adding \"{file.Name}\" would go over the {MaxTotalBytes / (1024 * 1024)} MB total limit.";
                return;
            }

            await using var stream = source.OpenReadStream(MaxFileSizeBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            Files.Add(new PendingAttachment
            {
                Name = file.Name,
                ContentType = file.ContentType,
                Data = bytes,
                PreviewUrl = isImage ? $"data:{file.ContentType};base64,{Convert.ToBase64String(bytes)}" : null
            });

            await FilesChanged.InvokeAsync(Files);
        }
        catch (Exception ex)
        {
            _attachError = $"Couldn't read \"{file.Name}\": {ex.Message}";
        }
    }

    private async Task RemoveFile(PendingAttachment file)
    {
        Files.Remove(file);
        _attachError = null;
        await FilesChanged.InvokeAsync(Files);
    }
}
```

Add `@using Microsoft.AspNetCore.Components.Forms` to `src/AssetDesk.Web/_Imports.razor` if `InputFile` and `IBrowserFile` do not resolve during the build in Step 4.

- [ ] **Step 3: Replace the extracted block in Report.razor**

In `src/AssetDesk.Web/Pages/Tickets/Report.razor`:

Replace the whole `<div class="space-y-1.5">` attachment block (starting at the `Attachments <span…(optional)` label, ending at the closing `</div>` before the `@if (!string.IsNullOrEmpty(_error))` block) with:

```razor
            <TicketAttachmentPicker @bind-Files="_pendingFiles" Disabled="@_submitting" />
```

Delete these now-unused members from the `@code` block: the `PendingFile` class, `AttachmentAccept`, `MaxFiles`, `MaxFileSizeBytes`, `MaxTotalBytes`, `MaxImageDimension`, `_attachError`, `_cameraInputId`, `_filesInputId`, and the `OnPhotoCaptured`, `OnFilesChosen`, `AddFile`, `RemoveFile` methods.

Change the field declaration from:

```csharp
    private readonly List<PendingFile> _pendingFiles = new();
```

to:

```csharp
    private List<PendingAttachment> _pendingFiles = new();
```

It loses `readonly` because `@bind-Files` assigns to it.

In `UploadAttachments`, the loop body is unchanged — `f.Data`, `f.Name`, `f.ContentType` exist on `PendingAttachment` with the same names and types.

Also delete `Report.razor`'s own `private const string TicketAttachmentCategoryOther = "Other";` and change the
`UploadTicketAttachmentAsync` call in `UploadAttachments` to pass `TicketAttachmentDefaults.Category`, so all
three upload sites share the constant added in Step 0d.

- [ ] **Step 4: Build and confirm nothing else referenced the removed members**

Run: `dotnet build`

Expected: Build succeeded, 0 errors. Any error naming `PendingFile`, `_attachError`, or `MaxFiles` is a leftover reference in `Report.razor` — remove it.

Then run: `git grep -n "PendingFile" -- src/`

Expected: no output.

- [ ] **Step 5: Verify Report.razor still behaves identically**

This step is manual and required — it is the only check that the extraction preserved behavior.

Start the app, open `/report`, and confirm: "Take Photo" and "Choose Files" both add thumbnails; the remove button clears one; adding a 7th file shows "You can attach up to 6 files."; submitting a report with two attachments still reports success.

If you cannot run the app, say so plainly in your report rather than claiming this step passed.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Web/Components/PendingAttachment.cs src/AssetDesk.Web/Components/TicketAttachmentPicker.razor src/AssetDesk.Web/Pages/Tickets/Report.razor && git commit -m "refactor(web): extract the ticket attachment picker out of Report.razor"
```

---

### Task 2: ApiClient read, download, and delete

**Files:**
- Modify: `src/AssetDesk.Web/Services/ApiClient.cs`

**Interfaces:**
- Consumes: `TicketAttachmentDto` from `AssetDesk.Shared.DTOs`
- Produces: `Task<List<TicketAttachmentDto>?> GetTicketAttachmentsAsync(int ticketId)`, `Task<(bool Success, byte[]? Data, string? ContentType, string? Error)> DownloadTicketAttachmentAsync(int ticketId, int attachmentId)`, `Task<(bool Success, string? Error)> DeleteTicketAttachmentAsync(int ticketId, int attachmentId)`

The endpoints already exist on `TicketAttachmentsController`; only the client is missing, which is why no screen can show attachments.

- [ ] **Step 1: Add the three methods**

Append to `src/AssetDesk.Web/Services/ApiClient.cs`, next to `UploadTicketAttachmentAsync`:

```csharp
    public async Task<List<TicketAttachmentDto>?> GetTicketAttachmentsAsync(int ticketId)
    {
        var client = await GetAuthenticatedClient();
        try
        {
            return await client.GetFromJsonAsync<List<TicketAttachmentDto>>(
                $"api/tickets/{ticketId}/attachments");
        }
        catch
        {
            // GetFromJsonAsync throws on any non-success status. A caller showing a gallery wants
            // an empty/error state, not an unhandled exception that blanks the whole page.
            return null;
        }
    }

    public async Task<(bool Success, byte[]? Data, string? ContentType, string? Error)> DownloadTicketAttachmentAsync(
        int ticketId, int attachmentId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetAsync($"api/tickets/{ticketId}/attachments/{attachmentId}/download");

        if (!response.IsSuccessStatusCode)
            return (false, null, null, $"Download failed ({(int)response.StatusCode}).");

        var data = await response.Content.ReadAsByteArrayAsync();
        return (true, data, response.Content.Headers.ContentType?.MediaType, null);
    }

    public async Task<(bool Success, string? Error)> DeleteTicketAttachmentAsync(int ticketId, int attachmentId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/tickets/{ticketId}/attachments/{attachmentId}");

        if (response.IsSuccessStatusCode)
            return (true, null);

        // Read the body defensively: a 403 from the authorization middleware has an empty body,
        // and ReadFromJsonAsync throws on that.
        var body = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<object>>(
                    body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (!string.IsNullOrWhiteSpace(payload?.Message))
                    return (false, payload.Message);
            }
            catch
            {
                // Non-JSON body (a proxy error page). Fall through to the status-derived message.
            }
        }

        return (false, response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? "You can only remove attachments you uploaded."
            : $"Couldn't remove the attachment ({(int)response.StatusCode}).");
    }
```

Note `GetAttachments` returns a bare `List<TicketAttachmentDto>` and not an `ApiResponse<T>` wrapper — confirm against `TicketAttachmentsController.GetAttachments`, whose return type is `ActionResult<List<TicketAttachmentDto>>`.

- [ ] **Step 2: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/AssetDesk.Web/Services/ApiClient.cs && git commit -m "feat(web): add the ticket attachment read, download and delete client methods"
```

---

### Task 3: Uploader-only delete

**Files:**
- Modify: `src/AssetDesk.Api/Controllers/TicketAttachmentsController.cs:183-185`
- Test: `tests/AssetDesk.Api.Tests/TicketAttachmentDeleteTests.cs`

**Interfaces:**
- Consumes: `TicketAttachmentsController.CurrentUserId` (already present)
- Produces: no new public API

This is a **swap**, not a widening: today the action is `[Authorize(Policy = "CanManageTicketQueue")]`, so staff can delete and the uploader cannot. Afterwards the uploader can and staff cannot.

- [ ] **Step 1: Write the failing tests**

Create `tests/AssetDesk.Api.Tests/TicketAttachmentDeleteTests.cs`. Follow the principal-construction pattern in `tests/AssetDesk.Api.Tests/PermissionGateTests.cs`:

```csharp
using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class TicketAttachmentDeleteTests
{
    private static ClaimsPrincipal Principal(string userId, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TicketAttachmentsController Controller(
        AppDbContext db, IFileStorageService storage, ITenantProvider tenants, ClaimsPrincipal user)
    {
        var controller = new TicketAttachmentsController(
            db, storage, new FakeSubscriptionService(), tenants, new FakeLookupService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    private static async Task<(AppDbContext Db, Ticket Ticket, TicketAttachment Attachment)> SeedAsync(
        AppDbContext db, Guid tenantId, string uploaderId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, uploaderId, "Uploader");

        var ticket = new Ticket
        {
            TenantId = tenantId,
            TicketNumber = 1,
            Type = TicketType.Incident,
            Category = TicketCategory.Other,
            Title = "Broken screen",
            Status = TicketStatus.New,
            Priority = TicketPriority.Medium,
            RequesterUserId = uploaderId
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var attachment = new TicketAttachment
        {
            TenantId = tenantId,
            TicketId = ticket.Id,
            FileName = "photo.jpg",
            StoredFileName = "stored-photo.jpg",
            ContentType = "image/jpeg",
            FileSizeBytes = 1234,
            Category = "Other",
            UploadedByUserId = uploaderId
        };
        db.TicketAttachments.Add(attachment);
        await db.SaveChangesAsync();

        return (db, ticket, attachment);
    }

    [Fact]
    public async Task Uploader_CanDeleteTheirOwnAttachment()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: false));
        using var _ = conn;
        var (_, ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, new FakeTenantProvider(tenantId, isSuperAdmin: false),
            Principal("uploader-1"));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.False(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Contains("stored-photo.jpg", storage.Deleted);
    }

    [Fact]
    public async Task AnotherUser_CannotDelete_AndTheAttachmentSurvives()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: false));
        using var _ = conn;
        var (_, ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, new FakeTenantProvider(tenantId, isSuperAdmin: false),
            Principal("someone-else"));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.True(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task QueueManager_WhoDidNotUpload_CannotDelete()
    {
        // This is the half of the rule that CHANGED. Before this task a holder of
        // iams:tickets:manage could delete anyone's attachment; now nobody but the uploader can.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId, isSuperAdmin: false));
        using var _ = conn;
        var (_, ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, new FakeTenantProvider(tenantId, isSuperAdmin: false),
            Principal("it-staff", Permissions.TicketsManage, Permissions.TicketsQueue));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.True(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
    }
}
```

These three fakes do **not** exist — `tests/AssetDesk.Api.Tests/` contains only `FakeTenantProvider.cs`. Create `tests/AssetDesk.Api.Tests/AttachmentTestDoubles.cs`:

```csharp
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;

namespace AssetDesk.Api.Tests;

/// Records what was deleted so a test can assert the stored file went too, not just the row.
internal sealed class FakeFileStorageService : IFileStorageService
{
    public List<string> Deleted { get; } = new();

    public Task<bool> DeleteFileAsync(string storedFileName)
    {
        Deleted.Add(storedFileName);
        return Task.FromResult(true);
    }

    // The delete path touches none of these.
    public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType) =>
        throw new NotImplementedException();
    public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName) =>
        throw new NotImplementedException();
    public bool IsValidFileType(string contentType) => true;
    public bool IsValidFileSize(long sizeBytes) => true;
}

internal sealed class FakeSubscriptionService : ISubscriptionService
{
    public Task<bool> CanCreateAssetAsync(Guid tenantId) => Task.FromResult(true);
    public Task<bool> CanCreateUserAsync(Guid tenantId) => Task.FromResult(true);
    public Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes) => Task.FromResult(true);
    public Task<bool> CanCreateTicketAsync(Guid tenantId) => Task.FromResult(true);
    public Task<TenantUsageDto> GetUsageAsync(Guid tenantId) => throw new NotImplementedException();
    public Task<bool> IsSubscriptionActiveAsync(Guid tenantId) => Task.FromResult(true);
}

internal sealed class FakeLookupService : ILookupService
{
    public Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default) =>
        Task.FromResult(true);
}
```

`ISubscriptionService` and `ILookupService` may declare members beyond those listed — the compiler is authoritative. Add any missing member as `throw new NotImplementedException()` unless the delete path needs it, and note in your report which ones you had to add.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter TicketAttachmentDeleteTests`

Expected: `Uploader_CanDeleteTheirOwnAttachment` and `QueueManager_WhoDidNotUpload_CannotDelete` FAIL. The uploader test fails because nothing yet lets a non-manager through; the manager test fails because today a manager is allowed.

Note the attribute-based policy is not evaluated when a controller is constructed directly in a unit test, so `AnotherUser_CannotDelete` may already pass. That is expected and is why the other two carry the weight.

- [ ] **Step 3: Replace the authorization rule**

In `src/AssetDesk.Api/Controllers/TicketAttachmentsController.cs`, change:

```csharp
    [HttpDelete("{attachmentId:int}")]
    [Authorize(Policy = "CanManageTicketQueue")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAttachment(
        int ticketId, int attachmentId, CancellationToken ct)
    {
        var attachment = await db.TicketAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));
```

to:

```csharp
    [HttpDelete("{attachmentId:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAttachment(
        int ticketId, int attachmentId, CancellationToken ct)
    {
        var attachment = await db.TicketAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));

        // Uploader-only, deliberately narrower than the rest of this controller. An attachment is
        // often the requester's own photo and may be sensitive, so the person who attached it is
        // the only one who can take it back - a queue manager can view it but not quietly drop it
        // from the ticket. Note this REMOVED the manage-permission delete that used to live in an
        // [Authorize] attribute here; it is a swap, not a widening.
        if (attachment.UploadedByUserId != CurrentUserId)
            return Forbid();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj --filter TicketAttachmentDeleteTests`

Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 182/182 (179 before this task, plus 3). If a pre-existing test asserted that a manager could delete an attachment, it was pinning the old rule — update it to the new one and say so in your report rather than deleting it.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Api/Controllers/TicketAttachmentsController.cs tests/AssetDesk.Api.Tests/TicketAttachmentDeleteTests.cs && git commit -m "feat(api): make attachment delete uploader-only"
```

---

### Task 4: Attachment gallery component

**Files:**
- Create: `src/AssetDesk.Web/Components/TicketAttachmentGallery.razor`

**Interfaces:**
- Consumes: `ApiClient.GetTicketAttachmentsAsync`, `DownloadTicketAttachmentAsync`, `DeleteTicketAttachmentAsync` (Task 2)
- Produces: `TicketAttachmentGallery` with parameters `int TicketId` and `EventCallback OnChanged`, plus a public `Task ReloadAsync()` the host calls after uploading

The component decides the delete affordance itself by comparing `UploadedByUserId` to the signed-in user, because the rule is uploader-only and every host would otherwise pass an identical predicate.

- [ ] **Step 1: Create the component**

Create `src/AssetDesk.Web/Components/TicketAttachmentGallery.razor`:

```razor
@using Microsoft.AspNetCore.Components.Authorization
@using System.Security.Claims
@inject ApiClient Api
@inject SnackbarService Snackbar
@inject IJSRuntime JS
@inject AuthenticationStateProvider AuthProvider

@if (_loading)
{
    <div class="flex flex-wrap gap-2">
        @for (var i = 0; i < 3; i++)
        {
            <div class="w-20 h-20 rounded-lg skeleton"></div>
        }
    </div>
}
else if (_error is not null)
{
    <div class="flex items-center gap-3">
        <p class="text-sm text-red-600 dark:text-red-400">@_error</p>
        <Button Variant="ghost" Size="sm" OnClick="ReloadAsync">Retry</Button>
    </div>
}
else if (_attachments.Count == 0)
{
    <p class="text-sm text-slate-500 dark:text-slate-400">No attachments.</p>
}
else
{
    <div class="flex flex-wrap gap-2">
        @foreach (var a in _attachments)
        {
            <div @key="a.Id" class="relative group">
                @if (a.IsImage)
                {
                    <button type="button" @onclick="() => OpenPreview(a)"
                            class="block w-20 h-20 rounded-lg overflow-hidden border border-slate-200 dark:border-slate-600">
                        <img src="@ThumbnailSrc(a)" alt="@a.FileName" class="w-full h-full object-cover" />
                    </button>
                }
                else
                {
                    <button type="button" @onclick="() => Download(a)"
                            class="w-20 h-20 flex flex-col items-center justify-center gap-1 rounded-lg border border-slate-200 dark:border-slate-600 bg-slate-50 dark:bg-slate-700 px-1">
                        <svg class="w-5 h-5 text-slate-400 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M7 21h10a2 2 0 002-2V9.414a1 1 0 00-.293-.707l-5.414-5.414A1 1 0 0012.586 3H7a2 2 0 00-2 2v14a2 2 0 002 2z" />
                        </svg>
                        <span class="text-[9px] leading-tight text-slate-500 dark:text-slate-400 truncate w-full text-center" title="@a.FileName">@a.FileName</span>
                    </button>
                }

                @if (CanDelete(a))
                {
                    <button type="button" @onclick="() => ConfirmDelete(a)" title="Remove"
                            class="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full bg-slate-700 dark:bg-slate-600 text-white flex items-center justify-center hover:bg-red-600 transition-colors">
                        <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="3" d="M6 18L18 6M6 6l12 12" />
                        </svg>
                    </button>
                }
            </div>
        }
    </div>
}

<Modal IsOpen="@(_preview is not null)" Title="@(_preview?.FileName ?? "")" Size="lg" OnClose="ClosePreview">
    <ChildContent>
        @if (_previewSrc is not null)
        {
            <img src="@_previewSrc" alt="@_preview?.FileName" class="w-full h-auto rounded-lg" />
        }
    </ChildContent>
    <FooterContent>
        <Button Variant="ghost" OnClick="ClosePreview">Close</Button>
        @if (_preview is not null)
        {
            <Button OnClick="() => Download(_preview)">Download</Button>
        }
    </FooterContent>
</Modal>

<Modal IsOpen="@(_toDelete is not null)" Title="Remove attachment" OnClose="CancelDelete">
    <ChildContent>
        <p class="text-sm text-slate-600 dark:text-slate-300">
            Remove <span class="font-medium text-slate-900 dark:text-white">@_toDelete?.FileName</span>?
            This deletes the file and cannot be undone.
        </p>
    </ChildContent>
    <FooterContent>
        <Button Variant="ghost" OnClick="CancelDelete" Disabled="@_deleting">Cancel</Button>
        <Button Variant="destructive" OnClick="DeleteConfirmed" Loading="@_deleting">Remove</Button>
    </FooterContent>
</Modal>

@code {
    [Parameter, EditorRequired] public int TicketId { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }

    private List<TicketAttachmentDto> _attachments = new();
    private bool _loading = true;
    private string? _error;
    private string? _currentUserId;

    private TicketAttachmentDto? _preview;
    private string? _previewSrc;
    private readonly Dictionary<int, string> _thumbCache = new();

    private TicketAttachmentDto? _toDelete;
    private bool _deleting;

    protected override async Task OnInitializedAsync()
    {
        var state = await AuthProvider.GetAuthenticationStateAsync();
        _currentUserId = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        await ReloadAsync();
    }

    /// Public so a host can refresh the gallery after uploading new files.
    public async Task ReloadAsync()
    {
        _loading = true;
        _error = null;
        StateHasChanged();

        var list = await Api.GetTicketAttachmentsAsync(TicketId);
        if (list is null)
        {
            _error = "Couldn't load attachments.";
            _attachments = new();
        }
        else
        {
            _attachments = list;
        }

        _loading = false;
        StateHasChanged();
    }

    // Uploader-only, mirroring TicketAttachmentsController.DeleteAttachment. Deciding it here
    // rather than taking a predicate keeps every host from re-deriving the same rule and getting
    // it subtly wrong, which would offer a control the API rejects.
    private bool CanDelete(TicketAttachmentDto a) =>
        _currentUserId is not null && a.UploadedByUserId == _currentUserId;

    private string ThumbnailSrc(TicketAttachmentDto a) =>
        _thumbCache.TryGetValue(a.Id, out var src) ? src : "";

    private async Task OpenPreview(TicketAttachmentDto a)
    {
        _preview = a;
        _previewSrc = null;

        var (ok, data, contentType, _) = await Api.DownloadTicketAttachmentAsync(TicketId, a.Id);
        if (ok && data is not null)
        {
            _previewSrc = $"data:{contentType ?? a.ContentType};base64,{Convert.ToBase64String(data)}";
            _thumbCache[a.Id] = _previewSrc;
        }
        else
        {
            Snackbar.Error("Couldn't open that image.");
            _preview = null;
        }
    }

    private void ClosePreview()
    {
        _preview = null;
        _previewSrc = null;
    }

    private async Task Download(TicketAttachmentDto a)
    {
        var (ok, data, contentType, error) = await Api.DownloadTicketAttachmentAsync(TicketId, a.Id);
        if (!ok || data is null)
        {
            Snackbar.Error(error ?? "Download failed.");
            return;
        }

        // Reuses the existing helper in wwwroot/js/fileUtils.js: window.downloadFile(dataUrl, fileName).
        var dataUrl = $"data:{contentType ?? a.ContentType};base64,{Convert.ToBase64String(data)}";
        await JS.InvokeVoidAsync("downloadFile", dataUrl, a.FileName);
    }

    private void ConfirmDelete(TicketAttachmentDto a) => _toDelete = a;

    private void CancelDelete()
    {
        if (_deleting) return;
        _toDelete = null;
    }

    private async Task DeleteConfirmed()
    {
        var target = _toDelete;
        if (target is null) return;

        _deleting = true;
        var (ok, error) = await Api.DeleteTicketAttachmentAsync(TicketId, target.Id);
        _deleting = false;

        if (ok)
        {
            Snackbar.Success($"Removed {target.FileName}");
            _toDelete = null;
            _thumbCache.Remove(target.Id);
            await ReloadAsync();
            await OnChanged.InvokeAsync();
        }
        else
        {
            Snackbar.Error(error ?? "Couldn't remove the attachment.");
            _toDelete = null;
        }
    }
}
```

No new JavaScript is needed for the download. `src/AssetDesk.Web/wwwroot/js/fileUtils.js:11` already defines
`window.downloadFile = function(dataUrl, fileName)`, which is what the code above calls. Do not add a
second helper.

- [ ] **Step 2: Thumbnails need loading, not just caching**

As written, `ThumbnailSrc` returns `""` until an image has been previewed once, so thumbnails start blank. Fix it by prefetching image bytes at the end of `ReloadAsync`:

```csharp
        // Prefetch image bytes so thumbnails render immediately. Sequential for the same reason
        // uploads are: these offices are on poor connections. Downscaled uploads are small.
        foreach (var a in _attachments.Where(a => a.IsImage && !_thumbCache.ContainsKey(a.Id)))
        {
            var (ok, data, contentType, _) = await Api.DownloadTicketAttachmentAsync(TicketId, a.Id);
            if (ok && data is not null)
            {
                _thumbCache[a.Id] = $"data:{contentType ?? a.ContentType};base64,{Convert.ToBase64String(data)}";
                StateHasChanged();
            }
        }
```

Insert this after `_loading = false; StateHasChanged();` so the grid appears before the images fill in.

- [ ] **Step 3: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors. `Modal` does have a `Size` parameter (`Modal.razor:38`, default `"md"`), so `Size="lg"` above is valid.

- [ ] **Step 4: Commit**

```bash
git add src/AssetDesk.Web/Components/TicketAttachmentGallery.razor src/AssetDesk.Web/wwwroot/js/fileUtils.js && git commit -m "feat(web): add the ticket attachment gallery"
```

---

### Task 5: Wire the gallery into the ticket detail page

**Files:**
- Modify: `src/AssetDesk.Web/Pages/Tickets/View.razor`

**Interfaces:**
- Consumes: `TicketAttachmentGallery` (Task 4), `TicketAttachmentPicker` (Task 1), `ApiClient.UploadTicketAttachmentAsync` (existing)
- Produces: nothing

This is the task that closes the black hole: until now nothing displayed attachments at all.

- [ ] **Step 1: Add the Attachments card**

In `src/AssetDesk.Web/Pages/Tickets/View.razor`, add a new `<Card>` immediately after the Description card (which begins near line 64 with `<CardTitle>Description</CardTitle>`):

```razor
                <Card>
                    <CardHeader>
                        <CardTitle>Attachments</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <div class="space-y-4">
                            <TicketAttachmentGallery @ref="_gallery" TicketId="_ticket.Id" />

                            @if (CanAddAttachments)
                            {
                                <div class="pt-2 border-t border-slate-200 dark:border-slate-700">
                                    <TicketAttachmentPicker @bind-Files="_pendingFiles" Disabled="@_uploading" />

                                    @if (_pendingFiles.Count > 0)
                                    {
                                        <div class="flex items-center gap-2 pt-3">
                                            <Button OnClick="UploadPending" Loading="@_uploading">
                                                Upload @_pendingFiles.Count file@(_pendingFiles.Count == 1 ? "" : "s")
                                            </Button>
                                            <Button Variant="ghost" OnClick="ClearPending" Disabled="@_uploading">Cancel</Button>
                                        </div>
                                    }
                                </div>
                            }
                        </div>
                    </CardContent>
                </Card>
```

- [ ] **Step 2: Add the supporting code**

Add to the `@code` block in `src/AssetDesk.Web/Pages/Tickets/View.razor`:

```csharp
    private TicketAttachmentGallery? _gallery;
    private List<PendingAttachment> _pendingFiles = new();
    private bool _uploading;
    private string? _currentUserId;

    // The creator uploads: the requester may add to their own ticket while it is still open.
    // Staff working the queue can see attachments but are not offered an upload control, even
    // though UploadAttachment would accept one from them - see the spec's UI/API asymmetry note.
    private bool CanAddAttachments =>
        _ticket is not null
        && _currentUserId is not null
        && _ticket.RequesterUserId == _currentUserId
        && _ticket.IsOpen;

    private void ClearPending()
    {
        _pendingFiles = new();
    }

    private async Task UploadPending()
    {
        if (_ticket is null || _pendingFiles.Count == 0) return;

        _uploading = true;
        var total = _pendingFiles.Count;
        var failed = 0;

        // Sequential, not parallel: same reason as Report.razor - poor office connections.
        foreach (var f in _pendingFiles)
        {
            using var stream = new MemoryStream(f.Data);
            var (ok, _, _) = await Api.UploadTicketAttachmentAsync(
                _ticket.Id, stream, f.Name, f.ContentType, TicketAttachmentDefaults.Category);

            if (!ok) failed++;
        }

        _uploading = false;
        _pendingFiles = new();

        var attached = total - failed;
        if (failed == 0)
            Snackbar.Success(attached == 1 ? "1 attachment added" : $"{attached} attachments added");
        else if (attached == 0)
            Snackbar.Error("The attachment(s) couldn't be uploaded.");
        else
            Snackbar.Warning($"{attached} of {total} attachments uploaded.");

        if (_gallery is not null)
            await _gallery.ReloadAsync();
    }
```

In whichever method loads the ticket (`OnInitializedAsync` or the existing load method), capture the current user id before rendering:

```csharp
        var state = await AuthProvider.GetAuthenticationStateAsync();
        _currentUserId = state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
```

Add the injections at the top of the page if not already present:

```razor
@using Microsoft.AspNetCore.Components.Authorization
@inject AuthenticationStateProvider AuthProvider
@inject SnackbarService Snackbar
```

`TicketDto` exposes `string? RequesterUserId` and `required string Status` — verified, so the comparison above is correct as written. `RequesterUserId` being nullable is why `CanAddAttachments` also guards `_currentUserId is not null`: two nulls must not compare equal and hand an anonymous viewer the upload control.

- [ ] **Step 3: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/AssetDesk.Web/Pages/Tickets/View.razor && git commit -m "feat(web): show ticket attachments and let the requester add more"
```

---

### Task 6: Attachments in the staff New Ticket dialog

**Files:**
- Modify: `src/AssetDesk.Web/Pages/Tickets/Index.razor`

**Interfaces:**
- Consumes: `TicketAttachmentPicker` (Task 1), `ApiClient.UploadTicketAttachmentAsync` (existing)
- Produces: nothing

Staff creating a ticket are its creator, so they attach here. Attachments need a ticket id, so the upload happens after `CreateTicketAsync` succeeds — the same order `Report.razor` already uses.

- [ ] **Step 1: Add the picker to the dialog**

In `src/AssetDesk.Web/Pages/Tickets/Index.razor`, inside the New Ticket `<Modal>` (opens near line 275), add the picker as the last field before the modal's `FooterContent`:

```razor
        <TicketAttachmentPicker @bind-Files="_pendingFiles" Disabled="@_creating" />
```

- [ ] **Step 2: Upload after create**

Add to the `@code` block:

```csharp
    private List<PendingAttachment> _pendingFiles = new();

    private async Task<int> UploadPendingAttachments(int ticketId)
    {
        var failed = 0;

        // Sequential, not parallel: same reason as Report.razor - poor office connections.
        foreach (var f in _pendingFiles)
        {
            using var stream = new MemoryStream(f.Data);
            var (ok, _, _) = await Api.UploadTicketAttachmentAsync(
                ticketId, stream, f.Name, f.ContentType, TicketAttachmentDefaults.Category);

            if (!ok) failed++;
        }

        return failed;
    }
```

In `SubmitCreate`, the existing call is:

```csharp
        var (success, _, error) = await Api.CreateTicketAsync(request);
```

Change it to capture the created ticket and upload afterwards:

```csharp
        var (success, created, error) = await Api.CreateTicketAsync(request);

        if (success && created is not null && _pendingFiles.Count > 0)
        {
            // An attachment failure never undoes the ticket - a ticket that was created but whose
            // photos failed is still a filed ticket. Report the two outcomes separately.
            var total = _pendingFiles.Count;
            var failed = await UploadPendingAttachments(created.Id);
            var attached = total - failed;

            if (failed > 0)
            {
                Snackbar.Warning(attached == 0
                    ? "Ticket created, but the attachment(s) couldn't be uploaded."
                    : $"Ticket created. {attached} of {total} attachments uploaded.");
            }
        }

        _pendingFiles = new();
```

`CreateTicketAsync` returns `(bool Success, TicketDto? Ticket, string? Error)` — verified, so `created.Id` is correct as written.

Also clear `_pendingFiles` in `HideCreateModal` so a cancelled dialog does not carry files into the next one:

```csharp
    private void HideCreateModal()
    {
        _showCreateModal = false;
        _pendingFiles = new();
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build`

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test tests/AssetDesk.Api.Tests/AssetDesk.Api.Tests.csproj`

Expected: 182/182.

- [ ] **Step 5: Manual verification**

Start the app and confirm end to end, since none of the Blazor work has automated coverage:

1. As an employee, file a report at `/report` with two photos. Confirm success.
2. Open that ticket's detail page. **The photos appear in the Attachments card** — this is the defect the whole plan exists to fix.
3. As the requester, add a third photo from the detail page and confirm it appears.
4. As the requester, remove one and confirm it disappears.
5. Sign in as IT staff, open the same ticket: photos are visible, and there is **no** remove control on the requester's files.
6. As staff, create a ticket from the New Ticket dialog with an attachment and confirm it appears on the new ticket.

Report which of these you could and could not perform.

- [ ] **Step 6: Commit**

```bash
git add src/AssetDesk.Web/Pages/Tickets/Index.razor && git commit -m "feat(web): attach files when creating a ticket from the queue"
```

---

## Self-Review Notes

Checked against the spec:

- Every spec section maps to a task: picker extraction (1), `ApiClient` methods (2), uploader-only delete plus its tests (3), gallery (4), `View.razor` surface (5), New Ticket dialog surface (6).
- The spec's `imageResize.js` was removed in a correction commit; no task builds it, and Task 1 carries `RequestImageFileAsync` through unchanged.
- The spec's UI/API asymmetry note is implemented in Task 5's `CanAddAttachments` and called out in a comment there.

Deviations recorded rather than left silent:

- Task 4 Step 2 fixes a real defect in Step 1's own code: `ThumbnailSrc` returns empty until an image is previewed, so thumbnails would start blank. Kept as a separate step so the reason is visible rather than folded into the component silently.
- A pre-flight review flagged that the first draft duplicated the open-status list and the `"Other"` category into each page. Both are now single definitions added in Task 1: `TicketListItemDto.IsOpen` in `AssetDesk.Shared` (pinned against `TicketStatus.Open` by `TicketDtoIsOpenTests`, so the two cannot drift) and `TicketAttachmentDefaults.Category` in `AssetDesk.Web`. Moving `TicketStatus` itself into `AssetDesk.Shared` would have been the purist fix but touches 95 call sites across the API and tests — out of scope for this feature.
- Task 3's `AnotherUser_CannotDelete` test may pass before the fix, because attribute policies are not evaluated on a directly-constructed controller. This is stated in the step so nobody reads it as a discriminating test; the other two carry the proof.
