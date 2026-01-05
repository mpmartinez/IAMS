using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class MaintenanceController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get all maintenance records with pagination and filtering
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<MaintenanceDto>>> GetMaintenanceRecords(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] int? assetId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(m =>
                m.Title.ToLower().Contains(search) ||
                (m.Description != null && m.Description.ToLower().Contains(search)) ||
                m.Asset.AssetTag.ToLower().Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(m => m.Status == status);

        if (assetId.HasValue)
            query = query.Where(m => m.AssetId == assetId.Value);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => MapToDto(m))
            .ToListAsync();

        return Ok(new PagedResponse<MaintenanceDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    /// <summary>
    /// Get a single maintenance record by ID
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> GetMaintenance(int id)
    {
        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id);

        return maintenance is null
            ? NotFound(ApiResponse<MaintenanceDto>.Fail("Maintenance record not found"))
            : Ok(ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance)));
    }

    /// <summary>
    /// Get maintenance history for a specific asset
    /// </summary>
    [HttpGet("/api/assets/{assetId:int}/maintenance")]
    public async Task<ActionResult<List<MaintenanceDto>>> GetAssetMaintenance(int assetId)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<List<MaintenanceDto>>.Fail("Asset not found"));

        var records = await db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .Where(m => m.AssetId == assetId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => MapToDto(m))
            .ToListAsync();

        return Ok(records);
    }

    /// <summary>
    /// Get maintenance summary statistics
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<MaintenanceSummaryDto>>> GetSummary()
    {
        var summary = new MaintenanceSummaryDto
        {
            TotalCount = await db.Maintenances.CountAsync(),
            PendingCount = await db.Maintenances.CountAsync(m => m.Status == MaintenanceStatus.Pending),
            InProgressCount = await db.Maintenances.CountAsync(m => m.Status == MaintenanceStatus.InProgress),
            CompletedCount = await db.Maintenances.CountAsync(m => m.Status == MaintenanceStatus.Completed),
            CancelledCount = await db.Maintenances.CountAsync(m => m.Status == MaintenanceStatus.Cancelled)
        };

        return Ok(ApiResponse<MaintenanceSummaryDto>.Ok(summary));
    }

    /// <summary>
    /// Create a new maintenance record
    /// </summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> CreateMaintenance(CreateMaintenanceDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<MaintenanceDto>.Fail("Validation failed", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList()));

        var asset = await db.Assets.FindAsync(dto.AssetId);
        if (asset is null)
            return NotFound(ApiResponse<MaintenanceDto>.Fail("Asset not found"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var maintenance = new Maintenance
        {
            AssetId = dto.AssetId,
            Title = dto.Title,
            Description = dto.Description,
            Status = MaintenanceStatus.Pending,
            CreatedByUserId = currentUserId
        };

        db.Maintenances.Add(maintenance);

        // Update asset status to Maintenance
        asset.Status = AssetStatus.Maintenance;
        asset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        // Reload with navigation properties
        await db.Entry(maintenance).Reference(m => m.Asset).LoadAsync();
        await db.Entry(maintenance).Reference(m => m.CreatedByUser).LoadAsync();
        await db.Entry(maintenance).Collection(m => m.Attachments).LoadAsync();

        return CreatedAtAction(nameof(GetMaintenance), new { id = maintenance.Id },
            ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance), "Maintenance record created successfully"));
    }

    /// <summary>
    /// Update a maintenance record
    /// </summary>
    [HttpPut("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> UpdateMaintenance(int id, UpdateMaintenanceDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<MaintenanceDto>.Fail("Validation failed", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList()));

        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (maintenance is null)
            return NotFound(ApiResponse<MaintenanceDto>.Fail("Maintenance record not found"));

        // Validate status if provided
        if (dto.Status is not null && !MaintenanceStatus.IsValid(dto.Status))
            return BadRequest(ApiResponse<MaintenanceDto>.Fail($"Invalid status. Must be one of: {string.Join(", ", MaintenanceStatus.All)}"));

        // Validate performed by user if provided
        if (!string.IsNullOrEmpty(dto.PerformedByUserId))
        {
            var userExists = await db.Users.AnyAsync(u => u.Id == dto.PerformedByUserId);
            if (!userExists)
                return BadRequest(ApiResponse<MaintenanceDto>.Fail("Performed by user not found"));
        }

        // Update fields
        if (dto.Title is not null) maintenance.Title = dto.Title;
        if (dto.Description is not null) maintenance.Description = dto.Description;
        if (dto.Notes is not null) maintenance.Notes = dto.Notes;
        if (dto.PerformedByUserId is not null) maintenance.PerformedByUserId = string.IsNullOrEmpty(dto.PerformedByUserId) ? null : dto.PerformedByUserId;

        if (dto.Status is not null && dto.Status != maintenance.Status)
        {
            maintenance.Status = dto.Status;

            // Set timestamps based on status change
            if (dto.Status == MaintenanceStatus.InProgress && maintenance.StartedAt is null)
                maintenance.StartedAt = DateTime.UtcNow;

            if (dto.Status is MaintenanceStatus.Completed or MaintenanceStatus.Cancelled)
            {
                maintenance.CompletedAt = DateTime.UtcNow;
                // Restore asset status to Available
                maintenance.Asset.Status = AssetStatus.Available;
                maintenance.Asset.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        // Reload navigation properties
        await db.Entry(maintenance).Reference(m => m.CreatedByUser).LoadAsync();
        await db.Entry(maintenance).Reference(m => m.PerformedByUser).LoadAsync();
        await db.Entry(maintenance).Collection(m => m.Attachments).LoadAsync();

        return Ok(ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance), "Maintenance record updated successfully"));
    }

    /// <summary>
    /// Start maintenance (change status to InProgress)
    /// </summary>
    [HttpPost("{id:int}/start")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> StartMaintenance(int id)
    {
        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (maintenance is null)
            return NotFound(ApiResponse<MaintenanceDto>.Fail("Maintenance record not found"));

        if (maintenance.Status != MaintenanceStatus.Pending)
            return BadRequest(ApiResponse<MaintenanceDto>.Fail("Only pending maintenance can be started"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        maintenance.Status = MaintenanceStatus.InProgress;
        maintenance.StartedAt = DateTime.UtcNow;
        maintenance.PerformedByUserId ??= currentUserId;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance), "Maintenance started"));
    }

    /// <summary>
    /// Complete maintenance (change status to Completed)
    /// </summary>
    [HttpPost("{id:int}/complete")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> CompleteMaintenance(int id, [FromBody] string? notes = null)
    {
        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (maintenance is null)
            return NotFound(ApiResponse<MaintenanceDto>.Fail("Maintenance record not found"));

        if (maintenance.Status is not (MaintenanceStatus.Pending or MaintenanceStatus.InProgress))
            return BadRequest(ApiResponse<MaintenanceDto>.Fail("Only pending or in-progress maintenance can be completed"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        maintenance.Status = MaintenanceStatus.Completed;
        maintenance.CompletedAt = DateTime.UtcNow;
        maintenance.PerformedByUserId ??= currentUserId;

        if (maintenance.StartedAt is null)
            maintenance.StartedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
            maintenance.Notes = notes;

        // Restore asset status to Available
        maintenance.Asset.Status = AssetStatus.Available;
        maintenance.Asset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance), "Maintenance completed"));
    }

    /// <summary>
    /// Cancel maintenance
    /// </summary>
    [HttpPost("{id:int}/cancel")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<MaintenanceDto>>> CancelMaintenance(int id, [FromBody] string? notes = null)
    {
        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .Include(m => m.CreatedByUser)
            .Include(m => m.PerformedByUser)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (maintenance is null)
            return NotFound(ApiResponse<MaintenanceDto>.Fail("Maintenance record not found"));

        if (maintenance.Status is MaintenanceStatus.Completed or MaintenanceStatus.Cancelled)
            return BadRequest(ApiResponse<MaintenanceDto>.Fail("Cannot cancel completed or already cancelled maintenance"));

        maintenance.Status = MaintenanceStatus.Cancelled;
        maintenance.CompletedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
            maintenance.Notes = notes;

        // Restore asset status to Available
        maintenance.Asset.Status = AssetStatus.Available;
        maintenance.Asset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<MaintenanceDto>.Ok(MapToDto(maintenance), "Maintenance cancelled"));
    }

    /// <summary>
    /// Delete a maintenance record
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageMaintenance")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteMaintenance(int id)
    {
        var maintenance = await db.Maintenances
            .Include(m => m.Asset)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (maintenance is null)
            return NotFound(ApiResponse<object>.Fail("Maintenance record not found"));

        // If maintenance is active, restore asset status
        if (maintenance.Status is MaintenanceStatus.Pending or MaintenanceStatus.InProgress)
        {
            maintenance.Asset.Status = AssetStatus.Available;
            maintenance.Asset.UpdatedAt = DateTime.UtcNow;
        }

        db.Maintenances.Remove(maintenance);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(null, "Maintenance record deleted successfully"));
    }

    /// <summary>
    /// Get available maintenance statuses
    /// </summary>
    [HttpGet("statuses")]
    [AllowAnonymous]
    public ActionResult<string[]> GetStatuses() => Ok(MaintenanceStatus.All);

    private static MaintenanceDto MapToDto(Maintenance m) => new()
    {
        Id = m.Id,
        AssetId = m.AssetId,
        AssetTag = m.Asset.AssetTag,
        AssetName = m.Asset.DisplayName,
        Title = m.Title,
        Description = m.Description,
        Status = m.Status,
        Notes = m.Notes,
        CreatedAt = m.CreatedAt,
        StartedAt = m.StartedAt,
        CompletedAt = m.CompletedAt,
        CreatedByUserId = m.CreatedByUserId,
        CreatedByUserName = m.CreatedByUser?.FullName ?? "",
        PerformedByUserId = m.PerformedByUserId,
        PerformedByUserName = m.PerformedByUser?.FullName,
        AttachmentCount = m.Attachments?.Count ?? 0
    };
}
