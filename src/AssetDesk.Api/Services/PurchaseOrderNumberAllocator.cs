using AssetDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IPurchaseOrderNumberAllocator
{
    Task<int> NextAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Mirrors TicketNumberAllocator exactly, including its race: max + 1 is a read-then-write, so
/// two concurrent creations can pick the same number. The unique index on (TenantId, PoNumber)
/// is what actually holds - it turns a lost race into a failed insert rather than a duplicate
/// number, the same way (TenantId, TicketNumber) does for tickets.
///
/// IgnoreQueryFilters plus an explicit tenant filter, because the caller may be a super admin
/// whose filter would otherwise admit every tenant's rows and return the global maximum.
/// </summary>
public class PurchaseOrderNumberAllocator(AppDbContext db) : IPurchaseOrderNumberAllocator
{
    public async Task<int> NextAsync(Guid tenantId, CancellationToken ct = default)
    {
        var highest = await db.PurchaseOrders
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId)
            .MaxAsync(p => (int?)p.PoNumber, ct);

        return (highest ?? 0) + 1;
    }
}
