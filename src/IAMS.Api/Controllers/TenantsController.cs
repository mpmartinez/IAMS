using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "CanManageTenants")]
public class TenantsController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ISubscriptionService subscriptionService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<TenantDto>>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null)
    {
        var query = db.Tenants
            .IgnoreQueryFilters()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t =>
                t.Name.Contains(search) ||
                t.Slug.Contains(search));
        }

        if (isActive.HasValue)
        {
            query = query.Where(t => t.IsActive == isActive.Value);
        }

        var totalCount = await query.CountAsync();
        var tenants = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => MapToDto(t))
            .ToListAsync();

        var response = new PagedResponse<TenantDto>
        {
            Items = tenants,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
        return Ok(ApiResponse<PagedResponse<TenantDto>>.Ok(response));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<TenantDto>>> GetById(Guid id)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return NotFound(ApiResponse<TenantDto>.Fail("Tenant not found"));

        return Ok(ApiResponse<TenantDto>.Ok(MapToDto(tenant)));
    }

    [HttpGet("{id:guid}/usage")]
    public async Task<ActionResult<ApiResponse<TenantUsageDto>>> GetUsage(Guid id)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return NotFound(ApiResponse<TenantUsageDto>.Fail("Tenant not found"));

        // Get live counts
        var usage = await subscriptionService.GetUsageAsync(id);

        return Ok(ApiResponse<TenantUsageDto>.Ok(usage));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<TenantDto>>> Create(CreateTenantDto dto)
    {
        // Validate slug is unique
        var existingSlug = await db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == dto.Slug.ToLower());

        if (existingSlug)
            return BadRequest(ApiResponse<TenantDto>.Fail("Slug already exists"));

        // Validate admin email is unique
        var existingUser = await userManager.FindByEmailAsync(dto.AdminEmail);
        if (existingUser != null)
            return BadRequest(ApiResponse<TenantDto>.Fail("Admin email already exists"));

        // Get subscription limits
        var (maxAssets, maxUsers, maxStorageBytes) = SubscriptionTiers.GetLimits(dto.SubscriptionTier);

        // Create tenant
        var tenant = new Tenant
        {
            Name = dto.Name,
            Slug = dto.Slug.ToLower(),
            LogoUrl = dto.LogoUrl,
            PrimaryColor = dto.PrimaryColor,
            SubscriptionTier = dto.SubscriptionTier,
            MaxAssets = maxAssets,
            MaxUsers = maxUsers,
            MaxStorageBytes = maxStorageBytes,
            IsActive = true
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Create Admin role if not exists
        if (!await roleManager.RoleExistsAsync("Admin"))
        {
            await roleManager.CreateAsync(new IdentityRole("Admin"));
        }

        // Create tenant admin user
        var adminUser = new ApplicationUser
        {
            UserName = dto.AdminEmail,
            Email = dto.AdminEmail,
            EmailConfirmed = true,
            FullName = dto.AdminFullName,
            IsActive = true,
            TenantId = tenant.Id,
            IsTenantAdmin = true
        };

        var result = await userManager.CreateAsync(adminUser, dto.AdminPassword);
        if (!result.Succeeded)
        {
            // Rollback tenant creation
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync();

            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return BadRequest(ApiResponse<TenantDto>.Fail($"Failed to create admin user: {errors}"));
        }

        await userManager.AddToRoleAsync(adminUser, "Admin");

        // Update tenant user count
        tenant.CurrentUserCount = 1;
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, ApiResponse<TenantDto>.Ok(MapToDto(tenant)));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<TenantDto>>> Update(Guid id, UpdateTenantDto dto)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return NotFound(ApiResponse<TenantDto>.Fail("Tenant not found"));

        if (dto.Name != null)
            tenant.Name = dto.Name;

        if (dto.LogoUrl != null)
            tenant.LogoUrl = dto.LogoUrl;

        if (dto.PrimaryColor != null)
            tenant.PrimaryColor = dto.PrimaryColor;

        if (dto.IsActive.HasValue)
            tenant.IsActive = dto.IsActive.Value;

        if (dto.SubscriptionEndDate.HasValue)
            tenant.SubscriptionEndDate = dto.SubscriptionEndDate.Value;

        // Update subscription tier and limits
        if (dto.SubscriptionTier != null && dto.SubscriptionTier != tenant.SubscriptionTier)
        {
            var (maxAssets, maxUsers, maxStorageBytes) = SubscriptionTiers.GetLimits(dto.SubscriptionTier);
            tenant.SubscriptionTier = dto.SubscriptionTier;
            tenant.MaxAssets = maxAssets;
            tenant.MaxUsers = maxUsers;
            tenant.MaxStorageBytes = maxStorageBytes;
        }

        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<TenantDto>.Ok(MapToDto(tenant)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return NotFound(ApiResponse<object>.Fail("Tenant not found"));

        // Soft delete - just deactivate
        tenant.IsActive = false;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new { }, "Tenant deactivated successfully"));
    }

    [HttpGet("subscription-tiers")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<object[]>> GetSubscriptionTiers()
    {
        var tiers = SubscriptionTiers.All.Select(tier =>
        {
            var (maxAssets, maxUsers, maxStorageBytes) = SubscriptionTiers.GetLimits(tier);
            return new
            {
                Name = tier,
                MaxAssets = maxAssets,
                MaxUsers = maxUsers,
                MaxStorageBytes = maxStorageBytes,
                MaxStorageDisplay = FormatBytes(maxStorageBytes)
            };
        }).ToArray();

        return Ok(ApiResponse<object[]>.Ok(tiers));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F0} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    private static TenantDto MapToDto(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        Slug = tenant.Slug,
        LogoUrl = tenant.LogoUrl,
        PrimaryColor = tenant.PrimaryColor,
        SubscriptionTier = tenant.SubscriptionTier,
        SubscriptionStartDate = tenant.SubscriptionStartDate,
        SubscriptionEndDate = tenant.SubscriptionEndDate,
        IsActive = tenant.IsActive,
        MaxAssets = tenant.MaxAssets,
        MaxUsers = tenant.MaxUsers,
        MaxStorageBytes = tenant.MaxStorageBytes,
        CurrentAssetCount = tenant.CurrentAssetCount,
        CurrentUserCount = tenant.CurrentUserCount,
        CurrentStorageBytes = tenant.CurrentStorageBytes,
        CreatedAt = tenant.CreatedAt
    };
}
