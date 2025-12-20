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
public class DashboardController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get dashboard statistics - optimized single query
    /// </summary>
    [HttpGet]
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
}
