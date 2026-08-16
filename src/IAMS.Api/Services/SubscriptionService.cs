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
    Task<bool> CanCreateTicketAsync(Guid tenantId);
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

        var currentCount = await CountBillableUsersAsync(db, tenantId);

        return currentCount < tenant.MaxUsers;
    }

    public async Task<bool> CanCreateTicketAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.FindAsync(tenantId);
        if (tenant == null || !tenant.IsActive)
            return false;

        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return false;

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var currentCount = await db.Tickets
            .IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == tenantId && t.CreatedAt >= monthStart);

        return currentCount < tenant.MaxTicketsPerMonth;
    }

    // Seats are metered per human license, not per account: a user whose only role is
    // Employee (an office user who just files and follows their own tickets) does not
    // count against Tenant.MaxUsers. Otherwise a 200-person agency would exhaust a
    // 25-seat Pro plan on day one. A user with Employee plus any other role - or with no
    // roles at all - still counts, same as today.
    private static async Task<int> CountBillableUsersAsync(AppDbContext db, Guid tenantId)
    {
        var employeeRoleId = await db.Roles
            .Where(r => r.Name == Entities.Roles.Employee)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        var users = db.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantId);

        if (employeeRoleId is null)
            return await users.CountAsync();

        return await users.CountAsync(u =>
            !db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == employeeRoleId) ||
            db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId != employeeRoleId));
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

        var ticketBytes = await db.TicketAttachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        return (currentUsage + ticketBytes + fileSizeBytes) <= tenant.MaxStorageBytes;
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

        tenant.CurrentUserCount = await CountBillableUsersAsync(db, tenantId);

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

        var storageBytes = await db.Attachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        var ticketBytes = await db.TicketAttachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        tenant.CurrentStorageBytes = storageBytes + ticketBytes;

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

        var userCount = await CountBillableUsersAsync(db, tenantId);

        var storageBytes = await db.Attachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .SumAsync(a => a.FileSizeBytes);

        var ticketBytes = await db.TicketAttachments
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
            CurrentStorageBytes = storageBytes + ticketBytes,
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
