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
public class AssignmentsController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Assign an asset to a user
    /// </summary>
    [HttpPost("assets/{assetId:int}/assign")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanAssignAssets")]
    public async Task<ActionResult<ApiResponse<AssetAssignmentDto>>> AssignAsset(int assetId, AssignAssetRequest request)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<AssetAssignmentDto>.Fail("Asset not found"));

        // Check if asset is already assigned
        if (!string.IsNullOrEmpty(asset.AssignedToUserId))
            return BadRequest(ApiResponse<AssetAssignmentDto>.Fail($"Asset is already assigned to another user. Please return it first."));

        // Validate user exists
        var user = await db.Users.FindAsync(request.UserId);
        if (user is null)
            return BadRequest(ApiResponse<AssetAssignmentDto>.Fail("User not found"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Create assignment record
        var assignment = new AssetAssignment
        {
            AssetId = assetId,
            UserId = request.UserId,
            AssignedByUserId = currentUserId,
            Notes = request.Notes,
            AssignedAt = DateTime.UtcNow
        };

        db.AssetAssignments.Add(assignment);

        // Update asset
        asset.AssignedToUserId = request.UserId;
        asset.Status = AssetStatus.InUse;
        asset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        // Load related entities for response
        await db.Entry(assignment).Reference(a => a.Asset).LoadAsync();
        await db.Entry(assignment).Reference(a => a.User).LoadAsync();
        await db.Entry(assignment).Reference(a => a.AssignedByUser).LoadAsync();

        return Ok(ApiResponse<AssetAssignmentDto>.Ok(MapToDto(assignment), "Asset assigned successfully"));
    }

    /// <summary>
    /// Return an asset from a user
    /// </summary>
    [HttpPost("assets/{assetId:int}/return")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanReturnAssets")]
    public async Task<ActionResult<ApiResponse<AssetAssignmentDto>>> ReturnAsset(int assetId, ReturnAssetRequest request)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<AssetAssignmentDto>.Fail("Asset not found"));

        if (string.IsNullOrEmpty(asset.AssignedToUserId))
            return BadRequest(ApiResponse<AssetAssignmentDto>.Fail("Asset is not currently assigned to anyone"));

        // Find active assignment
        var assignment = await db.AssetAssignments
            .Include(a => a.Asset)
            .Include(a => a.User)
            .Include(a => a.AssignedByUser)
            .FirstOrDefaultAsync(a => a.AssetId == assetId && a.ReturnedAt == null);

        if (assignment is null)
            return BadRequest(ApiResponse<AssetAssignmentDto>.Fail("No active assignment found for this asset"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        // Update assignment record
        assignment.ReturnedAt = DateTime.UtcNow;
        assignment.ReturnedByUserId = currentUserId;
        assignment.ReturnNotes = request.Notes;
        assignment.ReturnCondition = request.ReturnCondition ?? ReturnCondition.Good;

        // Update asset based on return condition
        asset.AssignedToUserId = null;
        asset.Status = request.ReturnCondition switch
        {
            ReturnCondition.Damaged or ReturnCondition.NeedsRepair => AssetStatus.Maintenance,
            ReturnCondition.Lost => AssetStatus.Lost,
            _ => AssetStatus.Available
        };
        asset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        // Load returned by user
        await db.Entry(assignment).Reference(a => a.ReturnedByUser).LoadAsync();

        return Ok(ApiResponse<AssetAssignmentDto>.Ok(MapToDto(assignment), "Asset returned successfully"));
    }

    /// <summary>
    /// Get assignment history for an asset
    /// </summary>
    [HttpGet("assets/{assetId:int}/history")]
    public async Task<ActionResult<List<AssetAssignmentDto>>> GetAssetHistory(int assetId)
    {
        var asset = await db.Assets.FindAsync(assetId);
        if (asset is null)
            return NotFound(ApiResponse<List<AssetAssignmentDto>>.Fail("Asset not found"));

        var assignments = await db.AssetAssignments
            .Include(a => a.Asset)
            .Include(a => a.User)
            .Include(a => a.AssignedByUser)
            .Include(a => a.ReturnedByUser)
            .Where(a => a.AssetId == assetId)
            .OrderByDescending(a => a.AssignedAt)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(assignments);
    }

    /// <summary>
    /// Get all assets currently assigned to a user
    /// </summary>
    [HttpGet("users/{userId}/assets")]
    public async Task<ActionResult<UserAssetsDto>> GetUserAssets(string userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return NotFound(ApiResponse<UserAssetsDto>.Fail("User not found"));

        var currentAssets = await db.Assets
            .Where(a => a.AssignedToUserId == userId)
            .OrderBy(a => a.DeviceType)
            .ThenBy(a => a.AssetTag)
            .ToListAsync();

        var pastAssignmentsCount = await db.AssetAssignments
            .Where(a => a.UserId == userId && a.ReturnedAt != null)
            .CountAsync();

        return Ok(new UserAssetsDto
        {
            UserId = user.Id,
            UserName = user.FullName,
            Department = user.Department,
            Email = user.Email,
            IsActive = user.IsActive,
            CurrentAssets = currentAssets.Select(a => new AssetDto
            {
                Id = a.Id,
                AssetTag = a.AssetTag,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                ModelYear = a.ModelYear,
                SerialNumber = a.SerialNumber,
                DeviceType = a.DeviceType,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency,
                WarrantyProvider = a.WarrantyProvider,
                WarrantyStartDate = a.WarrantyStartDate,
                WarrantyEndDate = a.WarrantyEndDate,
                Status = a.Status,
                AssignedToUserId = a.AssignedToUserId,
                Name = a.Name,
                Location = a.Location,
                PurchaseDate = a.PurchaseDate,
                Notes = a.Notes,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt
            }).ToList(),
            TotalCurrentAssets = currentAssets.Count,
            TotalAssetValue = currentAssets.Sum(a => a.PurchasePrice ?? 0),
            TotalPastAssignments = pastAssignmentsCount
        });
    }

    /// <summary>
    /// Get offboarding summary for a user
    /// </summary>
    [HttpGet("users/{userId}/offboarding")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewAssignments")]
    public async Task<ActionResult<OffboardingDto>> GetOffboardingSummary(string userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return NotFound(ApiResponse<OffboardingDto>.Fail("User not found"));

        var unreturnedAssets = await db.AssetAssignments
            .Include(a => a.Asset)
            .Where(a => a.UserId == userId && a.ReturnedAt == null)
            .OrderBy(a => a.Asset.DeviceType)
            .ThenBy(a => a.AssignedAt)
            .ToListAsync();

        return Ok(new OffboardingDto
        {
            UserId = user.Id,
            UserName = user.FullName,
            Department = user.Department,
            Email = user.Email,
            UnreturnedAssets = unreturnedAssets.Select(a => new UnreturnedAssetDto
            {
                AssetId = a.Asset.Id,
                AssetTag = a.Asset.AssetTag,
                DisplayName = a.Asset.DisplayName,
                DeviceType = a.Asset.DeviceType,
                SerialNumber = a.Asset.SerialNumber,
                PurchasePrice = a.Asset.PurchasePrice,
                Currency = a.Asset.Currency,
                AssignedAt = a.AssignedAt,
                DaysAssigned = (int)(DateTime.UtcNow - a.AssignedAt).TotalDays,
                Location = a.Asset.Location
            }).ToList(),
            TotalUnreturnedAssets = unreturnedAssets.Count,
            TotalUnreturnedValue = unreturnedAssets.Sum(a => a.Asset.PurchasePrice ?? 0)
        });
    }

    /// <summary>
    /// Bulk return assets for offboarding
    /// </summary>
    [HttpPost("users/{userId}/offboarding/return")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanReturnAssets")]
    public async Task<ActionResult<ApiResponse<BulkReturnResult>>> BulkReturnAssets(string userId, BulkReturnRequest request)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return NotFound(ApiResponse<BulkReturnResult>.Fail("User not found"));

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = new BulkReturnResult
        {
            TotalRequested = request.AssetIds.Count,
            Errors = []
        };

        var returnedCount = 0;

        foreach (var assetId in request.AssetIds)
        {
            var assignment = await db.AssetAssignments
                .Include(a => a.Asset)
                .FirstOrDefaultAsync(a => a.AssetId == assetId && a.UserId == userId && a.ReturnedAt == null);

            if (assignment is null)
            {
                result.Errors.Add($"Asset {assetId}: No active assignment found");
                continue;
            }

            // Update assignment
            assignment.ReturnedAt = DateTime.UtcNow;
            assignment.ReturnedByUserId = currentUserId;
            assignment.ReturnNotes = request.Notes;
            assignment.ReturnCondition = request.ReturnCondition;

            // Update asset
            var asset = assignment.Asset;
            asset.AssignedToUserId = null;
            asset.Status = request.ReturnCondition switch
            {
                ReturnCondition.Damaged or ReturnCondition.NeedsRepair => AssetStatus.Maintenance,
                ReturnCondition.Lost => AssetStatus.Lost,
                _ => AssetStatus.Available
            };
            asset.UpdatedAt = DateTime.UtcNow;

            returnedCount++;
        }

        await db.SaveChangesAsync();

        return Ok(ApiResponse<BulkReturnResult>.Ok(new BulkReturnResult
        {
            TotalRequested = request.AssetIds.Count,
            TotalReturned = returnedCount,
            Errors = result.Errors
        }, $"Successfully returned {returnedCount} of {request.AssetIds.Count} assets"));
    }

    /// <summary>
    /// Get all users with unreturned assets (for offboarding dashboard)
    /// </summary>
    [HttpGet("offboarding/pending")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewAssignments")]
    public async Task<ActionResult<List<OffboardingSummaryItem>>> GetPendingOffboardings()
    {
        var usersWithAssets = await db.AssetAssignments
            .Include(a => a.User)
            .Include(a => a.Asset)
            .Where(a => a.ReturnedAt == null && !a.User.IsActive)
            .GroupBy(a => a.User)
            .Select(g => new OffboardingSummaryItem
            {
                UserId = g.Key.Id,
                UserName = g.Key.FullName,
                Department = g.Key.Department,
                UnreturnedCount = g.Count(),
                TotalValue = g.Sum(a => a.Asset.PurchasePrice ?? 0),
                OldestAssignment = g.Min(a => a.AssignedAt)
            })
            .OrderByDescending(u => u.UnreturnedCount)
            .ToListAsync();

        return Ok(usersWithAssets);
    }

    /// <summary>
    /// Get assignment audit log
    /// </summary>
    [HttpGet("audit")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewReports")]
    public async Task<ActionResult<PagedResponse<AssetAssignmentDto>>> GetAuditLog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? assetTag = null,
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var query = db.AssetAssignments
            .Include(a => a.Asset)
            .Include(a => a.User)
            .Include(a => a.AssignedByUser)
            .Include(a => a.ReturnedByUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(assetTag))
            query = query.Where(a => a.Asset.AssetTag.Contains(assetTag));

        if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(a => a.UserId == userId);

        if (fromDate.HasValue)
            query = query.Where(a => a.AssignedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(a => a.AssignedAt <= toDate.Value);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.AssignedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(new PagedResponse<AssetAssignmentDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    private static AssetAssignmentDto MapToDto(AssetAssignment a) => new()
    {
        Id = a.Id,
        AssetId = a.AssetId,
        AssetTag = a.Asset.AssetTag,
        AssetDisplayName = a.Asset.DisplayName,
        UserId = a.UserId,
        UserName = a.User.FullName,
        UserDepartment = a.User.Department,
        AssignedAt = a.AssignedAt,
        ReturnedAt = a.ReturnedAt,
        AssignedByUserId = a.AssignedByUserId,
        AssignedByUserName = a.AssignedByUser.FullName,
        ReturnedByUserId = a.ReturnedByUserId,
        ReturnedByUserName = a.ReturnedByUser?.FullName,
        Notes = a.Notes,
        ReturnNotes = a.ReturnNotes,
        ReturnCondition = a.ReturnCondition
    };
}

public record OffboardingSummaryItem
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? Department { get; init; }
    public int UnreturnedCount { get; init; }
    public decimal TotalValue { get; init; }
    public DateTime OldestAssignment { get; init; }
}
