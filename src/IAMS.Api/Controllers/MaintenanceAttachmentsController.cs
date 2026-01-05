using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/maintenance/{maintenanceId:int}/attachments")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class MaintenanceAttachmentsController(
    AppDbContext db,
    IFileStorageService fileStorage) : ControllerBase
{
    /// <summary>
    /// Get all attachments for a maintenance record
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaintenanceAttachmentDto>>> GetAttachments(int maintenanceId)
    {
        var maintenance = await db.Maintenances.FindAsync(maintenanceId);
        if (maintenance is null)
            return NotFound(ApiResponse<List<MaintenanceAttachmentDto>>.Fail("Maintenance record not found"));

        var attachments = await db.MaintenanceAttachments
            .Include(a => a.UploadedByUser)
            .Where(a => a.MaintenanceId == maintenanceId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(attachments);
    }

    /// <summary>
    /// Get a specific attachment metadata
    /// </summary>
    [HttpGet("{attachmentId:int}")]
    public async Task<ActionResult<ApiResponse<MaintenanceAttachmentDto>>> GetAttachment(int maintenanceId, int attachmentId)
    {
        var attachment = await db.MaintenanceAttachments
            .Include(a => a.UploadedByUser)
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.MaintenanceId == maintenanceId);

        if (attachment is null)
            return NotFound(ApiResponse<MaintenanceAttachmentDto>.Fail("Attachment not found"));

        return Ok(ApiResponse<MaintenanceAttachmentDto>.Ok(MapToDto(attachment)));
    }

    /// <summary>
    /// Upload a new attachment
    /// </summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB to account for multipart overhead
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<MaintenanceAttachmentDto>>> UploadAttachment(
        int maintenanceId,
        IFormFile file,
        [FromForm] string category,
        [FromForm] string? description = null)
    {
        var maintenance = await db.Maintenances.FindAsync(maintenanceId);
        if (maintenance is null)
            return NotFound(ApiResponse<MaintenanceAttachmentDto>.Fail("Maintenance record not found"));

        // Validate category
        if (!MaintenanceAttachmentCategories.IsValid(category))
            return BadRequest(ApiResponse<MaintenanceAttachmentDto>.Fail(
                $"Invalid category. Must be one of: {string.Join(", ", MaintenanceAttachmentCategories.All)}"));

        // Validate file
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<MaintenanceAttachmentDto>.Fail("No file provided"));

        if (!fileStorage.IsValidFileSize(file.Length))
            return BadRequest(ApiResponse<MaintenanceAttachmentDto>.Fail("File size exceeds 5 MB limit"));

        if (!fileStorage.IsValidFileType(file.ContentType))
            return BadRequest(ApiResponse<MaintenanceAttachmentDto>.Fail(
                "Invalid file type. Allowed types: JPEG, PNG, GIF, WebP, PDF, DOC, DOCX, TXT"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Save file to storage
        await using var stream = file.OpenReadStream();
        var storedFileName = await fileStorage.SaveFileAsync(stream, file.FileName, file.ContentType);

        // Create attachment record
        var attachment = new MaintenanceAttachment
        {
            MaintenanceId = maintenanceId,
            FileName = file.FileName,
            StoredFileName = storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Category = category,
            Description = description,
            UploadedByUserId = currentUserId
        };

        db.MaintenanceAttachments.Add(attachment);
        await db.SaveChangesAsync();

        // Load user for response
        await db.Entry(attachment).Reference(a => a.UploadedByUser).LoadAsync();

        return CreatedAtAction(nameof(GetAttachment),
            new { maintenanceId, attachmentId = attachment.Id },
            ApiResponse<MaintenanceAttachmentDto>.Ok(MapToDto(attachment), "Attachment uploaded successfully"));
    }

    /// <summary>
    /// Download an attachment file
    /// </summary>
    [HttpGet("{attachmentId:int}/download")]
    public async Task<IActionResult> DownloadAttachment(int maintenanceId, int attachmentId)
    {
        var attachment = await db.MaintenanceAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.MaintenanceId == maintenanceId);

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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAttachment(int maintenanceId, int attachmentId)
    {
        var attachment = await db.MaintenanceAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.MaintenanceId == maintenanceId);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));

        // Delete file from storage
        await fileStorage.DeleteFileAsync(attachment.StoredFileName);

        // Remove database record
        db.MaintenanceAttachments.Remove(attachment);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(null, "Attachment deleted successfully"));
    }

    /// <summary>
    /// Get available attachment categories
    /// </summary>
    [HttpGet("/api/maintenance/attachment-categories")]
    [AllowAnonymous]
    public ActionResult<string[]> GetCategories() => Ok(MaintenanceAttachmentCategories.All);

    private static MaintenanceAttachmentDto MapToDto(MaintenanceAttachment a) => new()
    {
        Id = a.Id,
        MaintenanceId = a.MaintenanceId,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSizeBytes = a.FileSizeBytes,
        Category = a.Category,
        Description = a.Description,
        CreatedAt = a.CreatedAt,
        UploadedByUserId = a.UploadedByUserId,
        UploadedByUserName = a.UploadedByUser.FullName
    };
}
