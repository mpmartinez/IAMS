using System.Globalization;
using System.Security.Claims;
using System.Text;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewReports")]
public class ReportsController(AppDbContext db, IPdfReportService pdf) : ControllerBase
{
    private string CurrentUserName => User.FindFirstValue(ClaimTypes.Name) ?? "Unknown";

    /// <summary>
    /// Get asset inventory report data
    /// </summary>
    [HttpGet("inventory")]
    public async Task<ActionResult<ApiResponse<List<AssetInventoryReportRow>>>> GetInventoryReport(
        [FromQuery] string? deviceType = null,
        [FromQuery] string? status = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(deviceType))
            query = query.Where(a => a.DeviceType == deviceType);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status.ToString() == status);

        var data = await query
            .OrderBy(a => a.AssetTag)
            .Select(a => new AssetInventoryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                Status = a.Status.ToString(),
                AssignedTo = a.AssignedToUser != null ? a.AssignedToUser.FullName : null,
                Location = a.Location,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency,
                PurchaseDate = a.PurchaseDate,
                WarrantyEndDate = a.WarrantyEndDate,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(ApiResponse<List<AssetInventoryReportRow>>.Ok(data));
    }

    /// <summary>
    /// Net book value across the estate, as of a date. Retired and Lost assets are excluded, and
    /// anything that cannot be depreciated is reported with the reason rather than folded in as
    /// zero - a total that quietly omits a fifth of the estate looks authoritative and is not.
    /// </summary>
    [HttpGet("depreciation")]
    public async Task<ActionResult<ApiResponse<DepreciationSummaryDto>>> GetDepreciationReport(
        [FromQuery] DateTime? asOf = null) =>
        Ok(ApiResponse<DepreciationSummaryDto>.Ok(
            await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow)));

    private async Task<DepreciationSummaryDto> BuildDepreciationSummaryAsync(DateTime asOf)
    {
        var assets = await db.Assets
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .OrderBy(a => a.AssetTag)
            .Select(a => new
            {
                a.AssetTag,
                a.DeviceType,
                a.Name,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate,
                a.PurchaseDate,
                a.TenantId
            })
            .ToListAsync();

        // Keyed on tenant + device type, not device type alone: with the query filter admitting
        // every tenant's rows for a super-admin caller, two tenants can each have a Laptop
        // policy, and a DeviceType-only dictionary throws on the duplicate key. Looking each
        // asset up under its own TenantId matches it against its own organisation's policy.
        var policies = await db.DepreciationPolicies
            .AsNoTracking()
            .ToDictionaryAsync(p => (p.TenantId, p.DeviceType));

        var rows = new List<DepreciationReportRow>(assets.Count);

        foreach (var a in assets)
        {
            policies.TryGetValue((a.TenantId, a.DeviceType), out var policy);

            var d = DepreciationCalculator.Calculate(
                a.PurchasePrice, a.ExchangeRate, a.PurchaseDate, policy, asOf);

            rows.Add(new DepreciationReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Name = a.Name,
                PurchaseDate = a.PurchaseDate,
                CostBasis = d.CostBasis,
                Currency = a.Currency,
                PurchasePrice = a.PurchasePrice,
                UsefulLifeMonths = policy?.UsefulLifeMonths,
                ElapsedMonths = d.IsDepreciable ? d.ElapsedMonths : null,
                AccumulatedDepreciation = d.IsDepreciable ? d.AccumulatedDepreciation : null,
                NetBookValue = d.IsDepreciable ? d.NetBookValue : null,
                IsDepreciable = d.IsDepreciable,
                IsFullyDepreciated = d.IsFullyDepreciated,
                NotDepreciableReason = d.NotDepreciableReason
            });
        }

        return new DepreciationSummaryDto
        {
            // Cost basis spans every reported asset; book value only the depreciable ones.
            TotalCostBasis = rows.Sum(r => r.CostBasis),
            TotalAccumulatedDepreciation = rows.Sum(r => r.AccumulatedDepreciation ?? 0m),
            TotalNetBookValue = rows.Sum(r => r.NetBookValue ?? 0m),
            DepreciableCount = rows.Count(r => r.IsDepreciable),
            NotDepreciableCount = rows.Count(r => !r.IsDepreciable),
            NotDepreciableByReason = rows
                .Where(r => r.NotDepreciableReason is not null)
                .GroupBy(r => r.NotDepreciableReason!)
                .Select(g => new NotDepreciableReasonCount { Reason = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList(),
            PrimaryCurrency = Currencies.PHP,
            AsOf = asOf,
            Rows = rows
        };
    }

    [HttpGet("depreciation/export")]
    public async Task<IActionResult> ExportDepreciationReport([FromQuery] DateTime? asOf = null)
    {
        var summary = await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow);

        var sb = new StringBuilder();
        sb.AppendLine("Asset Tag,Device Type,Name,Purchase Date,Purchase Price,Currency,Cost Basis (PHP),Useful Life (months),Elapsed Months,Accumulated Depreciation (PHP),Net Book Value (PHP),Status");

        foreach (var r in summary.Rows)
        {
            var status = r.IsDepreciable
                ? (r.IsFullyDepreciated ? "Fully depreciated" : "Depreciating")
                : r.NotDepreciableReason;

            sb.AppendLine(string.Join(',',
                EscapeCsv(r.AssetTag),
                EscapeCsv(r.DeviceType),
                EscapeCsv(r.Name),
                r.PurchaseDate?.ToString("yyyy-MM-dd") ?? "",
                r.PurchasePrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                EscapeCsv(r.Currency),
                r.CostBasis.ToString("F2", CultureInfo.InvariantCulture),
                r.UsefulLifeMonths?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.ElapsedMonths?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.AccumulatedDepreciation?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                r.NetBookValue?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                EscapeCsv(status)));
        }

        var fileName = $"Depreciation {summary.AsOf:yyyy-MM-dd}.csv";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
    }

    [HttpGet("depreciation/pdf")]
    public async Task<IActionResult> ExportDepreciationPdf([FromQuery] DateTime? asOf = null)
    {
        var summary = await BuildDepreciationSummaryAsync(asOf ?? DateTime.UtcNow);
        var bytes = pdf.BuildDepreciationPdf(summary);
        var fileName = $"Depreciation {summary.AsOf:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Export asset inventory report as CSV
    /// </summary>
    [HttpGet("inventory/export")]
    public async Task<IActionResult> ExportInventoryReport(
        [FromQuery] string? deviceType = null,
        [FromQuery] string? status = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(deviceType))
            query = query.Where(a => a.DeviceType == deviceType);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status.ToString() == status);

        var data = await query
            .OrderBy(a => a.AssetTag)
            .Select(a => new AssetInventoryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                Status = a.Status.ToString(),
                AssignedTo = a.AssignedToUser != null ? a.AssignedToUser.FullName : null,
                Location = a.Location,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency,
                PurchaseDate = a.PurchaseDate,
                WarrantyEndDate = a.WarrantyEndDate,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        var csv = GenerateInventoryCsv(data);
        var fileName = $"Asset Inventory {DateTime.UtcNow:yyyy-MM-dd}.csv";

        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }

    /// <summary>
    /// Get assigned assets by user report data
    /// </summary>
    [HttpGet("assigned-by-user")]
    public async Task<ActionResult<ApiResponse<List<AssignedAssetsByUserReportRow>>>> GetAssignedByUserReport(
        [FromQuery] string? userId = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.AssignedToUserId != null);

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(a => a.AssignedToUserId == userId);

        var data = await query
            .OrderBy(a => a.AssignedToUser!.FullName)
            .ThenBy(a => a.AssetTag)
            .Select(a => new AssignedAssetsByUserReportRow
            {
                UserName = a.AssignedToUser!.FullName,
                Department = a.AssignedToUser.Department,
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                AssignedDate = a.UpdatedAt, // Using UpdatedAt as proxy for assignment date
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency
            })
            .ToListAsync();

        return Ok(ApiResponse<List<AssignedAssetsByUserReportRow>>.Ok(data));
    }

    /// <summary>
    /// Export assigned assets by user report as CSV
    /// </summary>
    [HttpGet("assigned-by-user/export")]
    public async Task<IActionResult> ExportAssignedByUserReport(
        [FromQuery] string? userId = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.AssignedToUserId != null);

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(a => a.AssignedToUserId == userId);

        var data = await query
            .OrderBy(a => a.AssignedToUser!.FullName)
            .ThenBy(a => a.AssetTag)
            .Select(a => new AssignedAssetsByUserReportRow
            {
                UserName = a.AssignedToUser!.FullName,
                Department = a.AssignedToUser.Department,
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                AssignedDate = a.UpdatedAt,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency
            })
            .ToListAsync();

        var csv = GenerateAssignedByUserCsv(data);
        var fileName = $"Assigned Assets by User {DateTime.UtcNow:yyyy-MM-dd}.csv";

        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }

    /// <summary>
    /// Get warranty expiry report data
    /// </summary>
    [HttpGet("warranty-expiry")]
    public async Task<ActionResult<ApiResponse<List<WarrantyExpiryReportRow>>>> GetWarrantyExpiryReport(
        [FromQuery] string? warrantyStatus = null,
        [FromQuery] int? daysThreshold = null)
    {
        var today = DateTime.UtcNow.Date;

        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.WarrantyEndDate.HasValue);

        // Filter by warranty status
        if (!string.IsNullOrEmpty(warrantyStatus))
        {
            query = warrantyStatus.ToLower() switch
            {
                "expired" => query.Where(a => a.WarrantyEndDate!.Value < today),
                "expiring" => query.Where(a => a.WarrantyEndDate!.Value >= today && a.WarrantyEndDate!.Value <= today.AddDays(90)),
                "active" => query.Where(a => a.WarrantyEndDate!.Value > today.AddDays(90)),
                _ => query
            };
        }

        // Filter by days threshold
        if (daysThreshold.HasValue)
        {
            var thresholdDate = today.AddDays(daysThreshold.Value);
            query = query.Where(a => a.WarrantyEndDate!.Value <= thresholdDate);
        }

        var assets = await query
            .OrderBy(a => a.WarrantyEndDate)
            .ToListAsync();

        var data = assets.Select(a =>
        {
            var daysRemaining = (int)(a.WarrantyEndDate!.Value.Date - today).TotalDays;
            var status = daysRemaining < 0 ? "Expired" :
                         daysRemaining <= 90 ? "Expiring" : "Active";

            return new WarrantyExpiryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                WarrantyProvider = a.WarrantyProvider,
                WarrantyStartDate = a.WarrantyStartDate,
                WarrantyEndDate = a.WarrantyEndDate!.Value,
                DaysRemaining = daysRemaining,
                WarrantyStatus = status,
                AssignedTo = a.AssignedToUser?.FullName,
                Location = a.Location
            };
        }).ToList();

        return Ok(ApiResponse<List<WarrantyExpiryReportRow>>.Ok(data));
    }

    /// <summary>
    /// Export warranty expiry report as CSV
    /// </summary>
    [HttpGet("warranty-expiry/export")]
    public async Task<IActionResult> ExportWarrantyExpiryReport(
        [FromQuery] string? warrantyStatus = null,
        [FromQuery] int? daysThreshold = null)
    {
        var today = DateTime.UtcNow.Date;

        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.WarrantyEndDate.HasValue);

        if (!string.IsNullOrEmpty(warrantyStatus))
        {
            query = warrantyStatus.ToLower() switch
            {
                "expired" => query.Where(a => a.WarrantyEndDate!.Value < today),
                "expiring" => query.Where(a => a.WarrantyEndDate!.Value >= today && a.WarrantyEndDate!.Value <= today.AddDays(90)),
                "active" => query.Where(a => a.WarrantyEndDate!.Value > today.AddDays(90)),
                _ => query
            };
        }

        if (daysThreshold.HasValue)
        {
            var thresholdDate = today.AddDays(daysThreshold.Value);
            query = query.Where(a => a.WarrantyEndDate!.Value <= thresholdDate);
        }

        var assets = await query
            .OrderBy(a => a.WarrantyEndDate)
            .ToListAsync();

        var data = assets.Select(a =>
        {
            var daysRemaining = (int)(a.WarrantyEndDate!.Value.Date - today).TotalDays;
            var status = daysRemaining < 0 ? "Expired" :
                         daysRemaining <= 90 ? "Expiring" : "Active";

            return new WarrantyExpiryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                WarrantyProvider = a.WarrantyProvider,
                WarrantyStartDate = a.WarrantyStartDate,
                WarrantyEndDate = a.WarrantyEndDate!.Value,
                DaysRemaining = daysRemaining,
                WarrantyStatus = status,
                AssignedTo = a.AssignedToUser?.FullName,
                Location = a.Location
            };
        }).ToList();

        var csv = GenerateWarrantyExpiryCsv(data);
        var fileName = $"Warranty Expiry {DateTime.UtcNow:yyyy-MM-dd}.csv";

        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }

    /// <summary>
    /// Get asset value summary report
    /// </summary>
    [HttpGet("asset-value")]
    public async Task<ActionResult<ApiResponse<AssetValueSummaryDto>>> GetAssetValueReport()
    {
        var assets = await db.Assets
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .Select(a => new
            {
                a.DeviceType,
                a.Status,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate
            })
            .ToListAsync();

        // Every total is in pesos; a row keeps the currency it was booked in. Summing the raw
        // price would add a USD figure to a peso figure.
        var totalValue = assets.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate);
        var totalCount = assets.Count;
        var avgValue = totalCount > 0 ? totalValue / totalCount : 0;

        var primaryCurrency = Currencies.PHP;

        var byDeviceType = assets
            .GroupBy(a => a.DeviceType)
            .Select(g => new AssetValueReportRow
            {
                DeviceType = g.Key,
                AssetCount = g.Count(),
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate),
                AverageValue = g.Count() > 0 ? g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate) / g.Count() : 0,
                Currency = primaryCurrency
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        var byStatus = assets
            .GroupBy(a => a.Status.ToString())
            .Select(g => new AssetValueByStatusDto
            {
                Status = g.Key,
                AssetCount = g.Count(),
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate)
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        var summary = new AssetValueSummaryDto
        {
            GrandTotalValue = totalValue,
            TotalAssetCount = totalCount,
            AverageAssetValue = avgValue,
            PrimaryCurrency = primaryCurrency,
            ByDeviceType = byDeviceType,
            ByStatus = byStatus
        };

        return Ok(ApiResponse<AssetValueSummaryDto>.Ok(summary));
    }

    /// <summary>
    /// Export asset value report as CSV
    /// </summary>
    [HttpGet("asset-value/export")]
    public async Task<IActionResult> ExportAssetValueReport()
    {
        var assets = await db.Assets
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .Select(a => new
            {
                a.DeviceType,
                a.Status,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate
            })
            .ToListAsync();

        var primaryCurrency = Currencies.PHP;

        var byDeviceType = assets
            .GroupBy(a => a.DeviceType)
            .Select(g => new AssetValueReportRow
            {
                DeviceType = g.Key,
                AssetCount = g.Count(),
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate),
                AverageValue = g.Count() > 0 ? g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate) / g.Count() : 0,
                Currency = primaryCurrency
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        var csv = GenerateAssetValueCsv(byDeviceType, assets.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate), primaryCurrency);
        var fileName = $"Asset Value {DateTime.UtcNow:yyyy-MM-dd}.csv";

        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }

    /// <summary>
    /// Export asset inventory report as PDF
    /// </summary>
    [HttpGet("inventory/pdf")]
    public async Task<IActionResult> ExportInventoryPdf(
        [FromQuery] string? deviceType = null,
        [FromQuery] string? status = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(deviceType))
            query = query.Where(a => a.DeviceType == deviceType);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status.ToString() == status);

        var data = await query
            .OrderBy(a => a.AssetTag)
            .Select(a => new AssetInventoryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                Status = a.Status.ToString(),
                AssignedTo = a.AssignedToUser != null ? a.AssignedToUser.FullName : null,
                Location = a.Location,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency,
                PurchaseDate = a.PurchaseDate,
                WarrantyEndDate = a.WarrantyEndDate,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        var bytes = pdf.BuildInventoryPdf(data, deviceType, status);
        var fileName = $"Asset Inventory {DateTime.UtcNow:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Export assigned assets by user report as PDF
    /// </summary>
    [HttpGet("assigned-by-user/pdf")]
    public async Task<IActionResult> ExportAssignedByUserPdf(
        [FromQuery] string? userId = null)
    {
        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.AssignedToUserId != null);

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(a => a.AssignedToUserId == userId);

        var data = await query
            .OrderBy(a => a.AssignedToUser!.FullName)
            .ThenBy(a => a.AssetTag)
            .Select(a => new AssignedAssetsByUserReportRow
            {
                UserName = a.AssignedToUser!.FullName,
                Department = a.AssignedToUser.Department,
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                SerialNumber = a.SerialNumber,
                AssignedDate = a.UpdatedAt,
                PurchasePrice = a.PurchasePrice,
                Currency = a.Currency
            })
            .ToListAsync();

        var userName = !string.IsNullOrEmpty(userId) ? data.FirstOrDefault()?.UserName : null;
        var bytes = pdf.BuildAssignedByUserPdf(data, userName);
        var fileName = $"Assigned Assets by User {DateTime.UtcNow:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Export warranty expiry report as PDF
    /// </summary>
    [HttpGet("warranty-expiry/pdf")]
    public async Task<IActionResult> ExportWarrantyExpiryPdf(
        [FromQuery] string? warrantyStatus = null,
        [FromQuery] int? daysThreshold = null)
    {
        var today = DateTime.UtcNow.Date;

        var query = db.Assets
            .Include(a => a.AssignedToUser)
            .Where(a => a.WarrantyEndDate.HasValue);

        if (!string.IsNullOrEmpty(warrantyStatus))
        {
            query = warrantyStatus.ToLower() switch
            {
                "expired" => query.Where(a => a.WarrantyEndDate!.Value < today),
                "expiring" => query.Where(a => a.WarrantyEndDate!.Value >= today && a.WarrantyEndDate!.Value <= today.AddDays(90)),
                "active" => query.Where(a => a.WarrantyEndDate!.Value > today.AddDays(90)),
                _ => query
            };
        }

        if (daysThreshold.HasValue)
        {
            var thresholdDate = today.AddDays(daysThreshold.Value);
            query = query.Where(a => a.WarrantyEndDate!.Value <= thresholdDate);
        }

        var assets = await query
            .OrderBy(a => a.WarrantyEndDate)
            .ToListAsync();

        var data = assets.Select(a =>
        {
            var daysRemaining = (int)(a.WarrantyEndDate!.Value.Date - today).TotalDays;
            var rowStatus = daysRemaining < 0 ? "Expired" :
                            daysRemaining <= 90 ? "Expiring" : "Active";

            return new WarrantyExpiryReportRow
            {
                AssetTag = a.AssetTag,
                DeviceType = a.DeviceType,
                Manufacturer = a.Manufacturer,
                Model = a.Model,
                WarrantyProvider = a.WarrantyProvider,
                WarrantyStartDate = a.WarrantyStartDate,
                WarrantyEndDate = a.WarrantyEndDate!.Value,
                DaysRemaining = daysRemaining,
                WarrantyStatus = rowStatus,
                AssignedTo = a.AssignedToUser?.FullName,
                Location = a.Location
            };
        }).ToList();

        var bytes = pdf.BuildWarrantyExpiryPdf(data, warrantyStatus, daysThreshold);
        var fileName = $"Warranty Expiry {DateTime.UtcNow:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Export asset value report as PDF
    /// </summary>
    [HttpGet("asset-value/pdf")]
    public async Task<IActionResult> ExportAssetValuePdf()
    {
        var assets = await db.Assets
            .Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Lost)
            .Select(a => new
            {
                a.DeviceType,
                a.Status,
                a.PurchasePrice,
                a.Currency,
                a.ExchangeRate
            })
            .ToListAsync();

        var totalValue = assets.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate);
        var totalCount = assets.Count;
        var avgValue = totalCount > 0 ? totalValue / totalCount : 0;

        var primaryCurrency = Currencies.PHP;

        var byDeviceType = assets
            .GroupBy(a => a.DeviceType)
            .Select(g => new AssetValueReportRow
            {
                DeviceType = g.Key,
                AssetCount = g.Count(),
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate),
                AverageValue = g.Count() > 0 ? g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate) / g.Count() : 0,
                Currency = primaryCurrency
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        var byStatus = assets
            .GroupBy(a => a.Status.ToString())
            .Select(g => new AssetValueByStatusDto
            {
                Status = g.Key,
                AssetCount = g.Count(),
                TotalValue = g.Sum(a => (a.PurchasePrice ?? 0) * a.ExchangeRate)
            })
            .OrderByDescending(x => x.TotalValue)
            .ToList();

        var summary = new AssetValueSummaryDto
        {
            GrandTotalValue = totalValue,
            TotalAssetCount = totalCount,
            AverageAssetValue = avgValue,
            PrimaryCurrency = primaryCurrency,
            ByDeviceType = byDeviceType,
            ByStatus = byStatus
        };

        var bytes = pdf.BuildAssetValuePdf(summary);
        var fileName = $"Asset Value {DateTime.UtcNow:yyyy-MM-dd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // CSV Generation Methods

    private static string GenerateInventoryCsv(List<AssetInventoryReportRow> data)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Asset Tag,Device Type,Manufacturer,Model,Serial Number,Status,Assigned To,Location,Purchase Price,Currency,Purchase Date,Warranty End Date,Created At");

        // Data rows
        foreach (var row in data)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.AssetTag),
                EscapeCsv(row.DeviceType),
                EscapeCsv(row.Manufacturer),
                EscapeCsv(row.Model),
                EscapeCsv(row.SerialNumber),
                EscapeCsv(row.Status),
                EscapeCsv(row.AssignedTo),
                EscapeCsv(row.Location),
                row.PurchasePrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                EscapeCsv(row.Currency),
                row.PurchaseDate?.ToString("yyyy-MM-dd") ?? "",
                row.WarrantyEndDate?.ToString("yyyy-MM-dd") ?? "",
                row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            ));
        }

        return sb.ToString();
    }

    private static string GenerateAssignedByUserCsv(List<AssignedAssetsByUserReportRow> data)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("User Name,Department,Asset Tag,Device Type,Manufacturer,Model,Serial Number,Assigned Date,Purchase Price,Currency");

        // Data rows
        foreach (var row in data)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.UserName),
                EscapeCsv(row.Department),
                EscapeCsv(row.AssetTag),
                EscapeCsv(row.DeviceType),
                EscapeCsv(row.Manufacturer),
                EscapeCsv(row.Model),
                EscapeCsv(row.SerialNumber),
                row.AssignedDate?.ToString("yyyy-MM-dd") ?? "",
                row.PurchasePrice?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                EscapeCsv(row.Currency)
            ));
        }

        return sb.ToString();
    }

    private static string GenerateWarrantyExpiryCsv(List<WarrantyExpiryReportRow> data)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Asset Tag,Device Type,Manufacturer,Model,Warranty Provider,Warranty Start,Warranty End,Days Remaining,Status,Assigned To,Location");

        // Data rows
        foreach (var row in data)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.AssetTag),
                EscapeCsv(row.DeviceType),
                EscapeCsv(row.Manufacturer),
                EscapeCsv(row.Model),
                EscapeCsv(row.WarrantyProvider),
                row.WarrantyStartDate?.ToString("yyyy-MM-dd") ?? "",
                row.WarrantyEndDate.ToString("yyyy-MM-dd"),
                row.DaysRemaining.ToString(),
                EscapeCsv(row.WarrantyStatus),
                EscapeCsv(row.AssignedTo),
                EscapeCsv(row.Location)
            ));
        }

        return sb.ToString();
    }

    private static string GenerateAssetValueCsv(List<AssetValueReportRow> data, decimal grandTotal, string currency)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Device Type,Asset Count,Total Value,Average Value,Currency");

        // Data rows
        foreach (var row in data)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(row.DeviceType),
                row.AssetCount.ToString(),
                row.TotalValue.ToString("F2", CultureInfo.InvariantCulture),
                row.AverageValue.ToString("F2", CultureInfo.InvariantCulture),
                EscapeCsv(row.Currency)
            ));
        }

        // Summary row
        sb.AppendLine();
        sb.AppendLine($"GRAND TOTAL,{data.Sum(r => r.AssetCount)},{grandTotal:F2},,{currency}");

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Escape quotes and wrap in quotes if contains special characters
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
