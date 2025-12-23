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
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class AssetsController(AppDbContext db, IQrCodeService qrCodeService) : ControllerBase
{
    // All authenticated users can view assets
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AssetDto>>> GetAssets(
        [FromQuery] string? search = null,
        [FromQuery] string? deviceType = null,
        [FromQuery] string? status = null,
        [FromQuery] string? assignedToUserId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = db.Assets.Include(a => a.AssignedToUser).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(a =>
                a.AssetTag.ToLower().Contains(search) ||
                (a.Manufacturer != null && a.Manufacturer.ToLower().Contains(search)) ||
                (a.Model != null && a.Model.ToLower().Contains(search)) ||
                (a.SerialNumber != null && a.SerialNumber.ToLower().Contains(search)) ||
                (a.Name != null && a.Name.ToLower().Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(deviceType))
            query = query.Where(a => a.DeviceType == deviceType);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

        if (!string.IsNullOrWhiteSpace(assignedToUserId))
            query = query.Where(a => a.AssignedToUserId == assignedToUserId);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => MapToDto(a))
            .ToListAsync();

        return Ok(new PagedResponse<AssetDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<AssetDto>>> GetAsset(int id)
    {
        var asset = await db.Assets
            .Include(a => a.AssignedToUser)
            .FirstOrDefaultAsync(a => a.Id == id);

        return asset is null
            ? NotFound(ApiResponse<AssetDto>.Fail("Asset not found"))
            : Ok(ApiResponse<AssetDto>.Ok(MapToDto(asset)));
    }

    // Users with iams:assets:create permission can create assets
    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanCreateAssets")]
    public async Task<ActionResult<ApiResponse<AssetDto>>> CreateAsset(CreateAssetDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<AssetDto>.Fail("Validation failed", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList()));

        // Validate device type
        if (!DeviceTypes.All.Contains(dto.DeviceType))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid device type. Must be one of: {string.Join(", ", DeviceTypes.All)}"));

        // Validate status
        var validStatuses = new[] { AssetStatus.Available, AssetStatus.InUse, AssetStatus.Maintenance, AssetStatus.Retired, AssetStatus.Lost };
        if (!validStatuses.Contains(dto.Status))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid status. Must be one of: {string.Join(", ", validStatuses)}"));

        // Validate currency
        if (!Currencies.All.Contains(dto.Currency))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid currency. Must be one of: {string.Join(", ", Currencies.All)}"));

        // Validate warranty dates
        if (dto.WarrantyStartDate.HasValue && dto.WarrantyEndDate.HasValue && dto.WarrantyStartDate > dto.WarrantyEndDate)
            return BadRequest(ApiResponse<AssetDto>.Fail("Warranty start date cannot be after warranty end date"));

        // Validate assigned user exists
        if (!string.IsNullOrEmpty(dto.AssignedToUserId))
        {
            var userExists = await db.Users.AnyAsync(u => u.Id == dto.AssignedToUserId);
            if (!userExists)
                return BadRequest(ApiResponse<AssetDto>.Fail("Assigned user not found"));
        }

        // Generate unique AssetTag
        var assetTag = await GenerateAssetTagAsync(dto.DeviceType);

        var asset = new Asset
        {
            AssetTag = assetTag,
            Manufacturer = dto.Manufacturer,
            Model = dto.Model,
            ModelYear = dto.ModelYear,
            SerialNumber = dto.SerialNumber,
            DeviceType = dto.DeviceType,
            PurchasePrice = dto.PurchasePrice,
            Currency = dto.Currency,
            WarrantyProvider = dto.WarrantyProvider,
            WarrantyStartDate = dto.WarrantyStartDate,
            WarrantyEndDate = dto.WarrantyEndDate,
            Status = dto.Status,
            AssignedToUserId = dto.AssignedToUserId,
            Name = dto.Name,
            Location = dto.Location,
            PurchaseDate = dto.PurchaseDate,
            Notes = dto.Notes
        };

        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        // Reload with navigation property
        await db.Entry(asset).Reference(a => a.AssignedToUser).LoadAsync();

        return CreatedAtAction(nameof(GetAsset), new { id = asset.Id }, ApiResponse<AssetDto>.Ok(MapToDto(asset), "Asset created successfully"));
    }

    // Users with iams:assets:edit permission can update assets
    [HttpPut("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanEditAssets")]
    public async Task<ActionResult<ApiResponse<AssetDto>>> UpdateAsset(int id, UpdateAssetDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse<AssetDto>.Fail("Validation failed", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToList()));

        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound(ApiResponse<AssetDto>.Fail("Asset not found"));

        // Validate device type if provided
        if (dto.DeviceType is not null && !DeviceTypes.All.Contains(dto.DeviceType))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid device type. Must be one of: {string.Join(", ", DeviceTypes.All)}"));

        // Validate status if provided
        var validStatuses = new[] { AssetStatus.Available, AssetStatus.InUse, AssetStatus.Maintenance, AssetStatus.Retired, AssetStatus.Lost };
        if (dto.Status is not null && !validStatuses.Contains(dto.Status))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid status. Must be one of: {string.Join(", ", validStatuses)}"));

        // Validate currency if provided
        if (dto.Currency is not null && !Currencies.All.Contains(dto.Currency))
            return BadRequest(ApiResponse<AssetDto>.Fail($"Invalid currency. Must be one of: {string.Join(", ", Currencies.All)}"));

        // Validate warranty dates
        var startDate = dto.WarrantyStartDate ?? asset.WarrantyStartDate;
        var endDate = dto.WarrantyEndDate ?? asset.WarrantyEndDate;
        if (startDate.HasValue && endDate.HasValue && startDate > endDate)
            return BadRequest(ApiResponse<AssetDto>.Fail("Warranty start date cannot be after warranty end date"));

        // Validate assigned user if provided
        if (dto.AssignedToUserId is not null && !string.IsNullOrEmpty(dto.AssignedToUserId))
        {
            var userExists = await db.Users.AnyAsync(u => u.Id == dto.AssignedToUserId);
            if (!userExists)
                return BadRequest(ApiResponse<AssetDto>.Fail("Assigned user not found"));
        }

        // Update fields
        if (dto.DeviceType is not null) asset.DeviceType = dto.DeviceType;
        if (dto.Manufacturer is not null) asset.Manufacturer = dto.Manufacturer;
        if (dto.Model is not null) asset.Model = dto.Model;
        if (dto.ModelYear.HasValue) asset.ModelYear = dto.ModelYear;
        if (dto.SerialNumber is not null) asset.SerialNumber = dto.SerialNumber;
        if (dto.PurchasePrice.HasValue) asset.PurchasePrice = dto.PurchasePrice;
        if (dto.Currency is not null) asset.Currency = dto.Currency;
        if (dto.WarrantyProvider is not null) asset.WarrantyProvider = dto.WarrantyProvider;
        if (dto.WarrantyStartDate.HasValue) asset.WarrantyStartDate = dto.WarrantyStartDate;
        if (dto.WarrantyEndDate.HasValue) asset.WarrantyEndDate = dto.WarrantyEndDate;
        if (dto.Status is not null) asset.Status = dto.Status;
        if (dto.AssignedToUserId is not null) asset.AssignedToUserId = string.IsNullOrEmpty(dto.AssignedToUserId) ? null : dto.AssignedToUserId;
        if (dto.Name is not null) asset.Name = dto.Name;
        if (dto.Location is not null) asset.Location = dto.Location;
        if (dto.PurchaseDate.HasValue) asset.PurchaseDate = dto.PurchaseDate;
        if (dto.Notes is not null) asset.Notes = dto.Notes;

        asset.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Reload with navigation property
        await db.Entry(asset).Reference(a => a.AssignedToUser).LoadAsync();

        return Ok(ApiResponse<AssetDto>.Ok(MapToDto(asset), "Asset updated successfully"));
    }

    // Users with iams:assets:delete permission can delete assets
    [HttpDelete("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanDeleteAssets")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsset(int id)
    {
        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound(ApiResponse<object>.Fail("Asset not found"));

        db.Assets.Remove(asset);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(null, "Asset deleted successfully"));
    }

    [HttpGet("device-types")]
    [AllowAnonymous]
    public ActionResult<string[]> GetDeviceTypes() => Ok(DeviceTypes.All);

    [HttpGet("statuses")]
    [AllowAnonymous]
    public ActionResult<string[]> GetStatuses() =>
        Ok(new[] { AssetStatus.Available, AssetStatus.InUse, AssetStatus.Maintenance, AssetStatus.Retired, AssetStatus.Lost });

    [HttpGet("currencies")]
    [AllowAnonymous]
    public ActionResult<string[]> GetCurrencies() => Ok(Currencies.All);

    // Users with iams:reports:view permission can view reports
    [HttpGet("reports/summary")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewReports")]
    public async Task<ActionResult> GetAssetSummary()
    {
        var summary = new
        {
            TotalAssets = await db.Assets.CountAsync(),
            ByStatus = await db.Assets
                .GroupBy(a => a.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(),
            ByDeviceType = await db.Assets
                .GroupBy(a => a.DeviceType)
                .Select(g => new { DeviceType = g.Key, Count = g.Count() })
                .ToListAsync(),
            TotalValue = await db.Assets
                .Where(a => a.PurchasePrice.HasValue)
                .SumAsync(a => a.PurchasePrice ?? 0),
            AssignedAssets = await db.Assets.CountAsync(a => a.AssignedToUserId != null),
            ExpiringWarranties = await db.Assets
                .CountAsync(a => a.WarrantyEndDate.HasValue && a.WarrantyEndDate <= DateTime.UtcNow.AddMonths(3) && a.WarrantyEndDate > DateTime.UtcNow)
        };

        return Ok(ApiResponse<object>.Ok(summary));
    }

    /// <summary>
    /// Generate QR code for an asset as PNG image
    /// </summary>
    /// <param name="id">Asset ID</param>
    /// <param name="size">Pixels per module (5-20, default 10)</param>
    /// <param name="contentType">QR content: "tag" for AssetTag only, "url" for full asset URL (default)</param>
    [HttpGet("{id:int}/qr.png")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQrCodePng(int id, [FromQuery] int size = 10, [FromQuery] string contentType = "url")
    {
        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound(ApiResponse<object>.Fail("Asset not found"));

        try
        {
            size = Math.Clamp(size, 5, 20);
            var content = contentType == "tag" ? asset.AssetTag : qrCodeService.GenerateAssetUrl(asset.AssetTag);
            var pngBytes = qrCodeService.GeneratePng(content, size);

            return File(pngBytes, "image/png", $"qr-{asset.AssetTag}.png");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object>.Fail($"QR code generation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Generate QR code for an asset as SVG
    /// </summary>
    /// <param name="id">Asset ID</param>
    /// <param name="size">Pixels per module (5-20, default 10)</param>
    /// <param name="contentType">QR content: "tag" for AssetTag only, "url" for full asset URL (default)</param>
    [HttpGet("{id:int}/qr.svg")]
    [ProducesResponseType(typeof(ContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQrCodeSvg(int id, [FromQuery] int size = 10, [FromQuery] string contentType = "url")
    {
        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound(ApiResponse<object>.Fail("Asset not found"));

        try
        {
            size = Math.Clamp(size, 5, 20);
            var content = contentType == "tag" ? asset.AssetTag : qrCodeService.GenerateAssetUrl(asset.AssetTag);
            var svg = qrCodeService.GenerateSvg(content, size);

            return Content(svg, "image/svg+xml");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object>.Fail($"QR code generation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Generate QR code by asset tag (alternative lookup)
    /// </summary>
    [HttpGet("by-tag/{assetTag}/qr.png")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQrCodeByTagPng(string assetTag, [FromQuery] int size = 10, [FromQuery] string contentType = "url")
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.AssetTag == assetTag);
        if (asset is null)
            return NotFound(ApiResponse<object>.Fail("Asset not found"));

        try
        {
            size = Math.Clamp(size, 5, 20);
            var content = contentType == "tag" ? asset.AssetTag : qrCodeService.GenerateAssetUrl(asset.AssetTag);
            var pngBytes = qrCodeService.GeneratePng(content, size);

            return File(pngBytes, "image/png", $"qr-{asset.AssetTag}.png");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object>.Fail($"QR code generation failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Lookup asset by scanning QR code (used by mobile app)
    /// </summary>
    [HttpGet("scan/{assetTag}")]
    public async Task<ActionResult<ApiResponse<AssetDto>>> GetAssetByTag(string assetTag)
    {
        // Normalize asset tag - trim whitespace
        var normalizedTag = assetTag?.Trim();
        if (string.IsNullOrEmpty(normalizedTag))
            return BadRequest(ApiResponse<AssetDto>.Fail("Asset tag is required"));

        // SQLite COLLATE NOCASE for case-insensitive comparison
        var asset = await db.Assets
            .Include(a => a.AssignedToUser)
            .FirstOrDefaultAsync(a => EF.Functions.Collate(a.AssetTag, "NOCASE") == normalizedTag);

        // If not found, try exact match (in case collate doesn't work)
        asset ??= await db.Assets
            .Include(a => a.AssignedToUser)
            .FirstOrDefaultAsync(a => a.AssetTag == normalizedTag);

        if (asset is null)
        {
            // Log all existing tags for debugging
            var allTags = await db.Assets.Select(a => a.AssetTag).ToListAsync();
            Console.WriteLine($"Scan failed for tag: '{normalizedTag}'");
            Console.WriteLine($"Existing tags in DB: {string.Join(", ", allTags)}");
        }

        return asset is null
            ? NotFound(ApiResponse<AssetDto>.Fail($"Asset not found: {normalizedTag}"))
            : Ok(ApiResponse<AssetDto>.Ok(MapToDto(asset)));
    }

    /// <summary>
    /// Debug endpoint - list all asset tags
    /// </summary>
    [HttpGet("debug/tags")]
    public async Task<ActionResult<List<string>>> GetAllTags()
    {
        var tags = await db.Assets.Select(a => a.AssetTag).OrderBy(t => t).ToListAsync();
        return Ok(tags);
    }

    private async Task<string> GenerateAssetTagAsync(string deviceType)
    {
        // Format: XXX-YYYYMMDD-NNNN (e.g., LAP-20251218-0001)
        var prefix = deviceType switch
        {
            DeviceTypes.Laptop => "LAP",
            DeviceTypes.Desktop => "DSK",
            DeviceTypes.Monitor => "MON",
            DeviceTypes.Phone => "PHN",
            DeviceTypes.Tablet => "TAB",
            DeviceTypes.Printer => "PRN",
            DeviceTypes.Network => "NET",
            DeviceTypes.Server => "SVR",
            DeviceTypes.Peripheral => "PER",
            DeviceTypes.Software => "SFT",
            _ => "OTH"
        };

        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        var baseTag = $"{prefix}-{datePart}-";

        // Find the highest sequence number for today
        var todayTags = await db.Assets
            .Where(a => a.AssetTag.StartsWith(baseTag))
            .Select(a => a.AssetTag)
            .ToListAsync();

        var maxSequence = 0;
        foreach (var tag in todayTags)
        {
            var sequencePart = tag.Replace(baseTag, "");
            if (int.TryParse(sequencePart, out var seq) && seq > maxSequence)
                maxSequence = seq;
        }

        return $"{baseTag}{(maxSequence + 1):D4}";
    }

    private static AssetDto MapToDto(Asset asset) => new()
    {
        Id = asset.Id,
        AssetTag = asset.AssetTag,
        Manufacturer = asset.Manufacturer,
        Model = asset.Model,
        ModelYear = asset.ModelYear,
        SerialNumber = asset.SerialNumber,
        DeviceType = asset.DeviceType,
        PurchasePrice = asset.PurchasePrice,
        Currency = asset.Currency,
        WarrantyProvider = asset.WarrantyProvider,
        WarrantyStartDate = asset.WarrantyStartDate,
        WarrantyEndDate = asset.WarrantyEndDate,
        Status = asset.Status,
        AssignedToUserId = asset.AssignedToUserId,
        AssignedToUserName = asset.AssignedToUser?.FullName,
        Name = asset.Name,
        Location = asset.Location,
        PurchaseDate = asset.PurchaseDate,
        Notes = asset.Notes,
        CreatedAt = asset.CreatedAt,
        UpdatedAt = asset.UpdatedAt
    };
}
