using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WarrantyAlertsController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get all warranty alerts with optional filtering
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<WarrantyAlertDto>>> GetAlerts(
        [FromQuery] string? alertType = null,
        [FromQuery] bool? acknowledged = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = db.WarrantyAlerts
            .Include(a => a.Asset)
                .ThenInclude(asset => asset.AssignedToUser)
            .Include(a => a.AcknowledgedByUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(alertType))
            query = query.Where(a => a.AlertType == alertType);

        if (acknowledged.HasValue)
        {
            query = acknowledged.Value
                ? query.Where(a => a.AcknowledgedAt != null)
                : query.Where(a => a.AcknowledgedAt == null);
        }

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.AlertType == WarrantyAlertTypes.Expired) // Expired first
            .ThenBy(a => a.DaysRemaining) // Then by urgency
            .ThenByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(new PagedResponse<WarrantyAlertDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    /// <summary>
    /// Get warranty alert summary
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<WarrantyAlertSummaryDto>>> GetSummary()
    {
        var summary = new WarrantyAlertSummaryDto
        {
            TotalAlerts = await db.WarrantyAlerts.CountAsync(),
            ExpiringCount = await db.WarrantyAlerts.CountAsync(a => a.AlertType == WarrantyAlertTypes.Expiring),
            ExpiredCount = await db.WarrantyAlerts.CountAsync(a => a.AlertType == WarrantyAlertTypes.Expired),
            UnacknowledgedCount = await db.WarrantyAlerts.CountAsync(a => a.AcknowledgedAt == null)
        };

        return Ok(ApiResponse<WarrantyAlertSummaryDto>.Ok(summary));
    }

    /// <summary>
    /// Get a specific alert
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<WarrantyAlertDto>>> GetAlert(int id)
    {
        var alert = await db.WarrantyAlerts
            .Include(a => a.Asset)
                .ThenInclude(asset => asset.AssignedToUser)
            .Include(a => a.AcknowledgedByUser)
            .FirstOrDefaultAsync(a => a.Id == id);

        return alert is null
            ? NotFound(ApiResponse<WarrantyAlertDto>.Fail("Alert not found"))
            : Ok(ApiResponse<WarrantyAlertDto>.Ok(MapToDto(alert)));
    }

    /// <summary>
    /// Acknowledge an alert
    /// </summary>
    [HttpPost("{id:int}/acknowledge")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<ActionResult<ApiResponse<WarrantyAlertDto>>> AcknowledgeAlert(int id)
    {
        var alert = await db.WarrantyAlerts
            .Include(a => a.Asset)
                .ThenInclude(asset => asset.AssignedToUser)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (alert is null)
            return NotFound(ApiResponse<WarrantyAlertDto>.Fail("Alert not found"));

        if (alert.IsAcknowledged)
            return BadRequest(ApiResponse<WarrantyAlertDto>.Fail("Alert is already acknowledged"));

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedByUserId = userId;

        await db.SaveChangesAsync();

        // Reload to get the user info
        await db.Entry(alert).Reference(a => a.AcknowledgedByUser).LoadAsync();

        return Ok(ApiResponse<WarrantyAlertDto>.Ok(MapToDto(alert), "Alert acknowledged"));
    }

    /// <summary>
    /// Acknowledge multiple alerts at once
    /// </summary>
    [HttpPost("acknowledge-bulk")]
    [Authorize(Roles = "Admin,Staff")]
    public async Task<ActionResult<ApiResponse<object>>> AcknowledgeAlerts([FromBody] List<int> alertIds)
    {
        if (alertIds == null || alertIds.Count == 0)
            return BadRequest(ApiResponse<object>.Fail("No alert IDs provided"));

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var now = DateTime.UtcNow;

        var alerts = await db.WarrantyAlerts
            .Where(a => alertIds.Contains(a.Id) && a.AcknowledgedAt == null)
            .ToListAsync();

        foreach (var alert in alerts)
        {
            alert.AcknowledgedAt = now;
            alert.AcknowledgedByUserId = userId;
        }

        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new { AcknowledgedCount = alerts.Count }, $"Acknowledged {alerts.Count} alerts"));
    }

    /// <summary>
    /// Delete an acknowledged alert
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAlert(int id)
    {
        var alert = await db.WarrantyAlerts.FindAsync(id);

        if (alert is null)
            return NotFound(ApiResponse<object>.Fail("Alert not found"));

        db.WarrantyAlerts.Remove(alert);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(null, "Alert deleted"));
    }

    /// <summary>
    /// Get alerts for a specific asset
    /// </summary>
    [HttpGet("asset/{assetId:int}")]
    public async Task<ActionResult<List<WarrantyAlertDto>>> GetAssetAlerts(int assetId)
    {
        var alerts = await db.WarrantyAlerts
            .Include(a => a.Asset)
                .ThenInclude(asset => asset.AssignedToUser)
            .Include(a => a.AcknowledgedByUser)
            .Where(a => a.AssetId == assetId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(alerts);
    }

    /// <summary>
    /// Get unacknowledged alert count (for notification badge)
    /// </summary>
    [HttpGet("count")]
    public async Task<ActionResult<int>> GetUnacknowledgedCount()
    {
        var count = await db.WarrantyAlerts.CountAsync(a => a.AcknowledgedAt == null);
        return Ok(count);
    }

    private static WarrantyAlertDto MapToDto(WarrantyAlert alert) => new()
    {
        Id = alert.Id,
        AssetId = alert.AssetId,
        AssetTag = alert.Asset.AssetTag,
        AssetDisplayName = alert.Asset.DisplayName,
        DeviceType = alert.Asset.DeviceType,
        AlertType = alert.AlertType == WarrantyAlertTypes.Expiring
            ? WarrantyAlertType.Expiring
            : WarrantyAlertType.Expired,
        WarrantyEndDate = alert.WarrantyEndDate,
        DaysRemaining = alert.DaysRemaining,
        WarrantyProvider = alert.Asset.WarrantyProvider,
        AssignedToUserName = alert.Asset.AssignedToUser?.FullName,
        Location = alert.Asset.Location,
        CreatedAt = alert.CreatedAt,
        AcknowledgedAt = alert.AcknowledgedAt,
        AcknowledgedByUserId = alert.AcknowledgedByUserId,
        AcknowledgedByUserName = alert.AcknowledgedByUser?.FullName
    };
}
