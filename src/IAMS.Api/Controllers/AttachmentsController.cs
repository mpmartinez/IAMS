using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/assets/{assetId:int}/attachments")]
[Authorize]
public class AttachmentsController(
    AppDbContext db,
    IFileStorageService fileStorage) : ControllerBase
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>
    /// Get all attachments for an asset
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<AttachmentDto>>> GetAttachments(int assetId)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<List<AttachmentDto>>.Fail("Asset not found"));

        var attachments = await db.Attachments
            .Include(a => a.UploadedByUser)
            .Where(a => a.AssetId == assetId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(attachments);
    }

    /// <summary>
    /// Get attachment summary for an asset
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<AttachmentSummaryDto>>> GetSummary(int assetId)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<AttachmentSummaryDto>.Fail("Asset not found"));

        var attachments = await db.Attachments
            .Where(a => a.AssetId == assetId)
            .ToListAsync();

        var summary = new AttachmentSummaryDto
        {
            TotalCount = attachments.Count,
            TotalSizeBytes = attachments.Sum(a => a.FileSizeBytes),
            ReceiptCount = attachments.Count(a => a.Category == AttachmentCategories.Receipt),
            PhotoCount = attachments.Count(a => a.Category == AttachmentCategories.Photo),
            WarrantyDocumentCount = attachments.Count(a => a.Category == AttachmentCategories.WarrantyDocument),
            ManualCount = attachments.Count(a => a.Category == AttachmentCategories.Manual),
            OtherCount = attachments.Count(a => a.Category == AttachmentCategories.Other)
        };

        return Ok(ApiResponse<AttachmentSummaryDto>.Ok(summary));
    }

    /// <summary>
    /// Upload a new attachment
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Staff")]
    [RequestSizeLimit(MaxFileSizeBytes + 1024)] // Add margin for form data
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ApiResponse<AttachmentDto>>> UploadAttachment(
        int assetId,
        IFormFile file,
        [FromForm] string category,
        [FromForm] string? description = null)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<AttachmentDto>.Fail("Asset not found"));

        // Validate category
        if (!AttachmentCategories.All.Contains(category))
            return BadRequest(ApiResponse<AttachmentDto>.Fail(
                $"Invalid category. Must be one of: {string.Join(", ", AttachmentCategories.All)}"));

        // Validate file
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<AttachmentDto>.Fail("No file provided"));

        if (!fileStorage.IsValidFileSize(file.Length))
            return BadRequest(ApiResponse<AttachmentDto>.Fail("File size exceeds 5 MB limit"));

        if (!fileStorage.IsValidFileType(file.ContentType))
            return BadRequest(ApiResponse<AttachmentDto>.Fail(
                "Invalid file type. Allowed types: JPEG, PNG, GIF, WebP, PDF, DOC, DOCX, TXT"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Save file to storage
        await using var stream = file.OpenReadStream();
        var storedFileName = await fileStorage.SaveFileAsync(stream, file.FileName, file.ContentType);

        // Create attachment record
        var attachment = new Attachment
        {
            AssetId = assetId,
            FileName = file.FileName,
            StoredFileName = storedFileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Category = category,
            Description = description,
            UploadedByUserId = currentUserId
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        // Load user for response
        await db.Entry(attachment).Reference(a => a.UploadedByUser).LoadAsync();

        return CreatedAtAction(nameof(GetAttachment),
            new { assetId, attachmentId = attachment.Id },
            ApiResponse<AttachmentDto>.Ok(MapToDto(attachment), "Attachment uploaded successfully"));
    }

    /// <summary>
    /// Get a specific attachment metadata
    /// </summary>
    [HttpGet("{attachmentId:int}")]
    public async Task<ActionResult<ApiResponse<AttachmentDto>>> GetAttachment(int assetId, int attachmentId)
    {
        var attachment = await db.Attachments
            .Include(a => a.UploadedByUser)
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.AssetId == assetId);

        if (attachment is null)
            return NotFound(ApiResponse<AttachmentDto>.Fail("Attachment not found"));

        return Ok(ApiResponse<AttachmentDto>.Ok(MapToDto(attachment)));
    }

    /// <summary>
    /// Download an attachment file
    /// </summary>
    [HttpGet("{attachmentId:int}/download")]
    public async Task<IActionResult> DownloadAttachment(int assetId, int attachmentId)
    {
        var attachment = await db.Attachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.AssetId == assetId);

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
    [Authorize(Roles = "Admin,Staff")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAttachment(int assetId, int attachmentId)
    {
        var attachment = await db.Attachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.AssetId == assetId);

        if (attachment is null)
            return NotFound(ApiResponse<object>.Fail("Attachment not found"));

        // Delete file from storage
        await fileStorage.DeleteFileAsync(attachment.StoredFileName);

        // Remove database record
        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(null, "Attachment deleted successfully"));
    }

    /// <summary>
    /// Get available attachment categories
    /// </summary>
    [HttpGet("/api/attachments/categories")]
    [AllowAnonymous]
    public ActionResult<string[]> GetCategories() => Ok(AttachmentCategories.All);

    private static AttachmentDto MapToDto(Attachment a) => new()
    {
        Id = a.Id,
        AssetId = a.AssetId,
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
