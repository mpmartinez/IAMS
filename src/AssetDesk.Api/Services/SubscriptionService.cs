using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface ISubscriptionService
{
    Task<bool> CanCreateAssetAsync(Guid tenantId);

    /// <summary>
    /// The enforcing form of the asset limit: returns null when <paramref name="count"/> more
    /// assets fit, or the refusal to show the caller. It runs on the caller's
    /// <paramref name="db"/> and must be called inside a transaction the caller has already
    /// opened, before the assets are added - it takes the tenant row's write lock and holds it
    /// to the end of that transaction, so two writers cannot both count the same free room.
    /// See the implementation for why.
    /// </summary>
    Task<string?> ReserveAssetCapacityAsync(AppDbContext db, Guid tenantId, int count, CancellationToken ct = default);
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

    // Advisory only - it answers from its own scope, outside any transaction, so the answer can
    // be stale by the time anything is inserted. Every path that creates assets goes through
    // ReserveAssetCapacityAsync instead.
    public async Task<bool> CanCreateAssetAsync(Guid tenantId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await AssetCapacityRefusalAsync(db, tenantId, 1, CancellationToken.None) is null;
    }

    // Check-then-insert on its own is a lost update: two receipts or imports against one tenant
    // both count 45 of 50, both agree 5 more fit, and both commit - 55 assets, and for an import
    // the overshoot is a whole file. Nothing in the schema holds the limit, so it has to be
    // serialised here. The tenant row is the natural lock: it is one row per tenant, every asset
    // writer can reach it, and a no-op UPDATE takes its write lock until the transaction ends.
    // Under READ COMMITTED the second writer blocks on that UPDATE and, once the first commits,
    // its COUNT below runs on a fresh snapshot that includes the first writer's assets. Same
    // "the UPDATE is the lock" shape as the purchase order lock in GoodsReceiptService.
    //
    // It must run on the caller's context and connection. Counting from a scope of our own, as
    // the Can* methods do, would count on a second connection that cannot see the caller's
    // uncommitted writes - and would wait forever on the lock the caller is holding if it ever
    // wrote to the tenant row.
    public async Task<string?> ReserveAssetCapacityAsync(
        AppDbContext db, Guid tenantId, int count, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Reserving asset capacity needs an open transaction, or the lock is released before the assets are written.");

        var locked = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.CurrentAssetCount, t => t.CurrentAssetCount), ct);

        if (locked != 1)
            return InactiveSubscriptionMessage;

        return await AssetCapacityRefusalAsync(db, tenantId, count, ct);
    }

    private const string InactiveSubscriptionMessage =
        "Your organisation's subscription is inactive or has expired, so no assets can be added.";

    private static async Task<string?> AssetCapacityRefusalAsync(
        AppDbContext db, Guid tenantId, int count, CancellationToken ct)
    {
        // Untracked: the reserving caller's context may already track this tenant, and identity
        // resolution would hand back that instance rather than the row as it stands now.
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant == null || !tenant.IsActive)
            return InactiveSubscriptionMessage;

        if (tenant.SubscriptionEndDate.HasValue && tenant.SubscriptionEndDate < DateTime.UtcNow)
            return InactiveSubscriptionMessage;

        var currentCount = await db.Assets
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId, ct);

        var remaining = Math.Max(0, tenant.MaxAssets - currentCount);
        if (count <= remaining)
            return null;

        return remaining == 0
            ? $"Asset limit reached for your subscription ({tenant.MaxAssets} assets). Please upgrade."
            : $"Adding {count} assets would exceed your subscription's limit of {tenant.MaxAssets}: " +
              $"{currentCount} are already registered, so only {remaining} more can be added. Please upgrade.";
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
