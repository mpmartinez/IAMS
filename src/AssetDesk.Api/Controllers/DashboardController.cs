using System.Security.Claims;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Mapping;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class DashboardController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get dashboard statistics - optimized single query.
    /// </summary>
    /// <remarks>
    /// Gated on iams:assets:view. This payload carries estate-wide counts and TotalAssetValue, so
    /// a user who cannot open the asset list must not be able to read it either - see
    /// <see cref="GetMyDashboard"/> for what they get instead.
    /// </remarks>
    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewAssets")]
    public async Task<ActionResult<ApiResponse<DashboardDto>>> GetDashboard()
    {
        var today = DateTime.UtcNow.Date;
        var expiringThreshold = today.AddDays(90);

        // Get all assets with necessary data in a single query
        var assets = await db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .Select(a => new
            {
                a.Id,
                a.AssetTag,
                a.Name,
                a.Manufacturer,
                a.Model,
                a.DeviceType,
                a.Status,
                a.PurchasePrice,
                a.Currency,
                a.WarrantyEndDate,
                a.AssignedToUserId,
                AssignedToUserName = a.AssignedToUser != null ? a.AssignedToUser.FullName : null,
                a.CreatedAt
            })
            .ToListAsync();

        // Calculate counts
        var totalAssets = assets.Count;
        var assignedAssets = assets.Count(a => !string.IsNullOrEmpty(a.AssignedToUserId));
        var unassignedAssets = totalAssets - assignedAssets;
        var availableAssets = assets.Count(a => a.Status == AssetStatus.Available);
        var inUseAssets = assets.Count(a => a.Status == AssetStatus.InUse);
        var maintenanceAssets = assets.Count(a => a.Status == AssetStatus.Maintenance);

        // Calculate total value (assuming USD as primary currency for simplicity)
        var totalValue = assets
            .Where(a => a.PurchasePrice.HasValue)
            .Sum(a => a.PurchasePrice!.Value);

        // Get primary currency (most used)
        var primaryCurrency = assets
            .Where(a => !string.IsNullOrEmpty(a.Currency))
            .GroupBy(a => a.Currency)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "USD";

        // Warranty counts
        var warrantiesExpiringSoon = assets.Count(a =>
            a.WarrantyEndDate.HasValue &&
            a.WarrantyEndDate.Value >= today &&
            a.WarrantyEndDate.Value <= expiringThreshold);

        var warrantiesExpired = assets.Count(a =>
            a.WarrantyEndDate.HasValue &&
            a.WarrantyEndDate.Value < today);

        // Recent assets (last 5)
        var recentAssets = assets
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new RecentAssetDto
            {
                Id = a.Id,
                AssetTag = a.AssetTag,
                DisplayName = !string.IsNullOrEmpty(a.Name)
                    ? a.Name
                    : $"{a.Manufacturer ?? "Unknown"} {a.Model ?? a.DeviceType}".Trim(),
                DeviceType = a.DeviceType,
                Status = a.Status,
                AssignedToUserName = a.AssignedToUserName,
                CreatedAt = a.CreatedAt
            })
            .ToList();

        // Assets by type
        var assetsByType = assets
            .GroupBy(a => a.DeviceType)
            .Select(g => new DeviceTypeCountDto
            {
                DeviceType = g.Key,
                Count = g.Count(),
                TotalValue = g.Sum(a => a.PurchasePrice ?? 0)
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var dashboard = new DashboardDto
        {
            TotalAssets = totalAssets,
            AssignedAssets = assignedAssets,
            UnassignedAssets = unassignedAssets,
            AvailableAssets = availableAssets,
            InUseAssets = inUseAssets,
            MaintenanceAssets = maintenanceAssets,
            TotalAssetValue = totalValue,
            PrimaryCurrency = primaryCurrency,
            WarrantiesExpiringSoon = warrantiesExpiringSoon,
            WarrantiesExpired = warrantiesExpired,
            RecentAssets = recentAssets,
            AssetsByType = assetsByType
        };

        return Ok(ApiResponse<DashboardDto>.Ok(dashboard));
    }

    /// <summary>
    /// The self-service dashboard: the assets on the caller's own name and their own tickets.
    /// Requires no permission beyond being signed in, because every field is the caller's own data.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<MyDashboardDto>>> GetMyDashboard(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // Retired and Lost are kept here, unlike the estate query above: an asset still on your
        // name after being written off is exactly the thing you would want to see and query.
        var assets = await db.Assets
            .Where(a => a.AssignedToUserId == userId)
            .Select(a => new
            {
                a.Id,
                a.AssetTag,
                a.Name,
                a.Manufacturer,
                a.Model,
                a.DeviceType,
                a.Status,
                a.WarrantyEndDate
            })
            .ToListAsync(ct);

        // "Held since" for each asset, from the open assignment row. Max() rather than Single()
        // because nothing in the schema stops two open assignments existing for one asset.
        var heldSince = await db.AssetAssignments
            .Where(x => x.UserId == userId && x.ReturnedAt == null)
            .GroupBy(x => x.AssetId)
            .Select(g => new { AssetId = g.Key, AssignedAt = g.Max(x => x.AssignedAt) })
            .ToDictionaryAsync(x => x.AssetId, x => x.AssignedAt, ct);

        var myAssets = assets
            .Select(a => new MyAssetDto
            {
                Id = a.Id,
                AssetTag = a.AssetTag,
                DisplayName = !string.IsNullOrEmpty(a.Name)
                    ? a.Name
                    : $"{a.Manufacturer ?? "Unknown"} {a.Model ?? a.DeviceType}".Trim(),
                DeviceType = a.DeviceType,
                Status = a.Status,
                WarrantyEndDate = a.WarrantyEndDate,
                AssignedAt = heldSince.TryGetValue(a.Id, out var at) ? at : null
            })
            .OrderBy(a => a.AssetTag)
            .ToList();

        // Unpaged, matching TicketsController.Mine - one person's own tickets is a small set.
        var tickets = await db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .Where(t => t.RequesterUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        var items = tickets.Select(t => t.ToListItem()).ToList();

        var dashboard = new MyDashboardDto
        {
            MyAssets = myAssets,
            OpenTicketCount = items.Count(t => t.IsOpen),
            ResolvedTicketCount = items.Count(t => !t.IsOpen),
            RecentTickets = items.Take(5).ToList()
        };

        return Ok(ApiResponse<MyDashboardDto>.Ok(dashboard));
    }
}
