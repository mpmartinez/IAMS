using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public interface ISubscriptionService
{
    Task<bool> CanCreateAssetAsync(Guid tenantId);
    Task<bool> CanCreateUserAsync(Guid tenantId);
    Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes);
    Task UpdateAssetCountAsync(Guid tenantId);
    Task UpdateUserCountAsync(Guid tenantId);
    Task UpdateStorageUsageAsync(Guid tenantId);
    Task<TenantUsageDto> GetUsageAsync(Guid tenantId);
    Task<bool> IsSubscriptionActiveAsync(Guid tenantId);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> CanCreateAssetAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null || !tenant.IsActive)
            return false;

        // Check subscription expiry
        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return false;

        var currentCount = await db.Assets
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId);

        return currentCount < tenant.MaxAssets;
    }

    public async Task<bool> CanCreateUserAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null || !tenant.IsActive)
            return false;

        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return false;

        var currentCount = await db.Users
            .IgnoreQueryFilters()
            .Cast<ApplicationUser>()
            .CountAsync(u => u.TenantId == tenantId);

        return currentCount < tenant.MaxUsers;
    }

    public async Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null || !tenant.IsActive)
            return false;

        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return false;

        var currentUsage = await db.Attachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        return (currentUsage + fileSizeBytes) <= tenant.MaxStorageBytes;
    }

    public async Task UpdateAssetCountAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null) return;

        tenant.CurrentAssetCount = await db.Assets
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId);

        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogDebug("Updated asset count for tenant {TenantId}: {Count}",
            tenantId, tenant.CurrentAssetCount);
    }

    public async Task UpdateUserCountAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null) return;

        tenant.CurrentUserCount = await db.Users
            .IgnoreQueryFilters()
            .Cast<ApplicationUser>()
            .CountAsync(u => u.TenantId == tenantId);

        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogDebug("Updated user count for tenant {TenantId}: {Count}",
            tenantId, tenant.CurrentUserCount);
    }

    public async Task UpdateStorageUsageAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null) return;

        tenant.CurrentStorageBytes = await db.Attachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogDebug("Updated storage usage for tenant {TenantId}: {Bytes} bytes",
            tenantId, tenant.CurrentStorageBytes);
    }

    public async Task<TenantUsageDto> GetUsageAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null)
            throw new InvalidOperationException($"Tenant {tenantId} not found");

        // Get live counts
        var assetCount = await db.Assets
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId);

        var userCount = await db.Users
            .IgnoreQueryFilters()
            .Cast<ApplicationUser>()
            .CountAsync(u => u.TenantId == tenantId);

        var storageBytes = await db.Attachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        return new TenantUsageDto
        {
            TenantId = tenantId,
            TenantName = tenant.Name,
            CurrentAssetCount = assetCount,
            MaxAssets = tenant.MaxAssets,
            CurrentUserCount = userCount,
            MaxUsers = tenant.MaxUsers,
            CurrentStorageBytes = storageBytes,
            MaxStorageBytes = tenant.MaxStorageBytes
        };
    }

    public async Task<bool> IsSubscriptionActiveAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null)
            return false;

        if (!tenant.IsActive)
            return false;

        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return false;

        return true;
    }
}
