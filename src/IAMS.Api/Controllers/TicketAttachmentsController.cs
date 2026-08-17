using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/tickets/{ticketId:int}/attachments")]
[Authorize]
public class TicketAttachmentsController(
    AppDbContext db,
    IFileStorageService fileStorage,
    ISubscriptionService subscriptionService,
    ITenantProvider tenantProvider,
    ILookupService lookups) : ControllerBase
{
    private string CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
    private bool CanViewQueue => User.HasPermission(Permissions.TicketsQueue);

    /// <summary>
    /// Get all attachments for a ticket
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<TicketAttachmentDto>>> GetAttachments(int ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<List<TicketAttachmentDto>>.Fail("Ticket not found"));

        if (!CanViewQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var attachments = await db.TicketAttachments
            .Include(a => a.UploadedByUser)
            .Where(a => a.TicketId == ticketId)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => MapToDto(a))
            .ToListAsync(ct);

        return Ok(attachments);
    }

    /// <summary>
    /// Get a specific attachment metadata
    /// </summary>
    [HttpGet("{attachmentId:int}")]
    public async Task<ActionResult<ApiResponse<TicketAttachmentDto>>> GetAttachment(
        int ticketId, int attachmentId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<TicketAttachmentDto>.Fail("Ticket not found"));

        if (!CanViewQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var attachment = await db.TicketAttachments
            .Include(a => a.UploadedByUser)
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);

        if (attachment is null)
            return NotFound(ApiResponse<TicketAttachmentDto>.Fail("Attachment not found"));

        return Ok(ApiResponse<TicketAttachmentDto>.Ok(MapToDto(attachment)));
    }

    /// <summary>
    /// Upload a new attachment. A holder of the ticket-queue permission may upload to any
    /// ticket; a requester may only upload to their own ticket - same ownership check as the
    /// read endpoints above.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB to account for multipart overhead
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<TicketAttachmentDto>>> UploadAttachment(
        int ticketId,
        IFormFile file,
        [FromForm] string category,
        [FromForm] string? description,
        CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<TicketAttachmentDto>.Fail("Ticket not found"));

        if (!CanViewQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        // Validate category - editable lookup data, not the TicketAttachmentCategories constant.
        if (!await lookups.IsActiveValueAsync(LookupTypes.TicketAttachmentCategory, category))
            return BadRequest(ApiResponse<TicketAttachmentDto>.Fail($"'{category}' is not a valid attachment category."));

        // Validate file
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<TicketAttachmentDto>.Fail("No file provided"));

        if (!fileStorage.IsValidFileSize(file.Length))
            return BadRequest(ApiResponse<TicketAttachmentDto>.Fail("File size exceeds 5 MB limit"));

        if (!fileStorage.IsValidFileType(file.ContentType))
            return BadRequest(ApiResponse<TicketAttachmentDto>.Fail(
                "Invalid file type. Allowed types: JPEG, PNG, GIF, WebP, PDF, DOC, DOCX, TXT"));

        var tenantId = tenantProvider.GetRequiredTenantId();
        if (!await subscriptionService.CanUploadFileAsync(tenantId, file.Length))
            return BadRequest(ApiResponse<TicketAttachmentDto>.Fail(
                "Storage limit reached for your subscription. Please upgrade."));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Save file to storage
        await using var stream = file.OpenReadStream();
        var storedFileName = await fileStorage.SaveFileAsync(stream, file.FileName, file.ContentType);

        // Create attachment record
        var attachment = new TicketAttachment
        {
            TicketId = ticketId,
            FileName = file.FileName,
            StoredFileName = storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Category = category,
            Description = description,
            UploadedByUserId = currentUserId
        };

        db.TicketAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        // Load user for response
        await db.Entry(attachment).Reference(a => a.UploadedByUser).LoadAsync(ct);

        return CreatedAtAction(nameof(GetAttachment),
            new { ticketId, attachmentId = attachment.Id },
            ApiResponse<TicketAttachmentDto>.Ok(MapToDto(attachment), "Attachment uploaded successfully"));
    }

    /// <summary>
    /// Download an attachment file
    /// </summary>
    [HttpGet("{attachmentId:int}/download")]
    public async Task<IActionResult> DownloadAttachment(int ticketId, int attachmentId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<object>.Fail("Ticket not found"));

        if (!CanViewQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var attachment = await db.TicketAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));

        var fileResult = await fileStorage.GetFileAsync(attachment.StoredFileName);
        if (fileResult is null)
            return NotFound(ApiResponse<object>.Fail("File not found on server"));

        return File(fileResult.Value.FileStream, fileResult.Value.ContentType, attachment.FileName);
    }

    /// <summary>
    /// Delete an attachment
    /// </summary>
    [HttpDelete("{attachmentId:int}")]
    [Authorize(Policy = "CanManageTicketQueue")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAttachment(
        int ticketId, int attachmentId, CancellationToken ct)
    {
        var attachment = await db.TicketAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));

        // Delete file from storage
        await fileStorage.DeleteFileAsync(attachment.StoredFileName);

        // Remove database record
        db.TicketAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        // ApiResponse<object>.Ok takes a non-nullable T; null! matches the deleted
        // MaintenanceAttachmentsController's behaviour (no data on a delete response)
        // without the CS8625 warning that call carries in the sibling Attachments and
        // WarrantyAlerts controllers.
        return Ok(ApiResponse<object>.Ok(null!, "Attachment deleted successfully"));
    }

    /// <summary>
    /// Get available attachment categories
    /// </summary>
    [HttpGet("/api/tickets/attachment-categories")]
    [AllowAnonymous]
    public async Task<ActionResult<string[]>> GetCategories(CancellationToken ct) =>
        Ok(await lookups.GetActiveValuesAsync(LookupTypes.TicketAttachmentCategory, ct));

    private static TicketAttachmentDto MapToDto(TicketAttachment a) => new()
    {
        Id = a.Id,
        TicketId = a.TicketId,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        Category = a.Category,
        Description = a.Description,
        UploadedAt = a.UploadedAt,
        UploadedByUserId = a.UploadedByUserId,
        UploadedByUserName = a.UploadedByUser!.FullName
    };
}
