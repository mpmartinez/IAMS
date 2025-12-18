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
public class AssetsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AssetDto>>> GetAssets(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = db.Assets.Include(a => a.AssignedToUser).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.ToLower();
            query = query.Where(a =>
                a.Name.ToLower().Contains(search) ||
                a.AssetTag.ToLower().Contains(search) ||
                (a.SerialNumber != null && a.SerialNumber.ToLower().Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category == category);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(a => a.Status == status);

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
            ? NotFound()
            : Ok(ApiResponse<AssetDto>.Ok(MapToDto(asset)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<AssetDto>>> CreateAsset(CreateAssetDto dto)
    {
        if (await db.Assets.AnyAsync(a => a.AssetTag == dto.AssetTag))
            return BadRequest(ApiResponse<AssetDto>.Fail("Asset tag already exists"));

        var asset = new Asset
        {
            Name = dto.Name,
            AssetTag = dto.AssetTag,
            SerialNumber = dto.SerialNumber,
            Category = dto.Category,
            Status = dto.Status,
            Location = dto.Location,
            AssignedToUserId = dto.AssignedToUserId,
            PurchasePrice = dto.PurchasePrice,
            PurchaseDate = dto.PurchaseDate,
            WarrantyExpiry = dto.WarrantyExpiry,
            Notes = dto.Notes
        };

        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAsset), new { id = asset.Id }, ApiResponse<AssetDto>.Ok(MapToDto(asset)));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<AssetDto>>> UpdateAsset(int id, UpdateAssetDto dto)
    {
        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound();

        if (dto.AssetTag is not null && dto.AssetTag != asset.AssetTag)
        {
            if (await db.Assets.AnyAsync(a => a.AssetTag == dto.AssetTag && a.Id != id))
                return BadRequest(ApiResponse<AssetDto>.Fail("Asset tag already exists"));
            asset.AssetTag = dto.AssetTag;
        }

        if (dto.Name is not null) asset.Name = dto.Name;
        if (dto.SerialNumber is not null) asset.SerialNumber = dto.SerialNumber;
        if (dto.Category is not null) asset.Category = dto.Category;
        if (dto.Status is not null) asset.Status = dto.Status;
        if (dto.Location is not null) asset.Location = dto.Location;
        if (dto.AssignedToUserId.HasValue) asset.AssignedToUserId = dto.AssignedToUserId;
        if (dto.PurchasePrice.HasValue) asset.PurchasePrice = dto.PurchasePrice;
        if (dto.PurchaseDate.HasValue) asset.PurchaseDate = dto.PurchaseDate;
        if (dto.WarrantyExpiry.HasValue) asset.WarrantyExpiry = dto.WarrantyExpiry;
        if (dto.Notes is not null) asset.Notes = dto.Notes;

        asset.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<AssetDto>.Ok(MapToDto(asset)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsset(int id)
    {
        var asset = await db.Assets.FindAsync(id);
        if (asset is null)
            return NotFound();

        db.Assets.Remove(asset);
        await db.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("categories")]
    [AllowAnonymous]
    public ActionResult<string[]> GetCategories() =>
        Ok(new[]
        {
            AssetCategory.Laptop,
            AssetCategory.Desktop,
            AssetCategory.Monitor,
            AssetCategory.Phone,
            AssetCategory.Tablet,
            AssetCategory.Printer,
            AssetCategory.Network,
            AssetCategory.Server,
            AssetCategory.Peripheral,
            AssetCategory.Software,
            AssetCategory.Other
        });

    [HttpGet("statuses")]
    [AllowAnonymous]
    public ActionResult<string[]> GetStatuses() =>
        Ok(new[]
        {
            AssetStatus.Available,
            AssetStatus.InUse,
            AssetStatus.Maintenance,
            AssetStatus.Retired,
            AssetStatus.Lost
        });

    private static AssetDto MapToDto(Asset asset) => new()
    {
        Id = asset.Id,
        Name = asset.Name,
        AssetTag = asset.AssetTag,
        SerialNumber = asset.SerialNumber,
        Category = asset.Category,
        Status = asset.Status,
        Location = asset.Location,
        AssignedToUserId = asset.AssignedToUserId,
        AssignedToUserName = asset.AssignedToUser?.FullName,
        PurchasePrice = asset.PurchasePrice,
        PurchaseDate = asset.PurchaseDate,
        WarrantyExpiry = asset.WarrantyExpiry,
        Notes = asset.Notes,
        CreatedAt = asset.CreatedAt,
        UpdatedAt = asset.UpdatedAt
    };
}
