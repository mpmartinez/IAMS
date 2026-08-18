using AssetDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface ITicketNumberAllocator
{
    Task<int> NextAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Allocates the next human-facing ticket number for a tenant.
///
/// This reads MAX + 1, which races under concurrent inserts. That race is caught by the
/// unique index on (TenantId, TicketNumber): TicketService retries the insert, and the
/// second attempt reads the now-higher maximum. A per-tenant database sequence would
/// avoid the retry but needs DDL per tenant, which this app does not do.
/// </summary>
public class TicketNumberAllocator : ITicketNumberAllocator
{
    private readonly AppDbContext _db;

    public TicketNumberAllocator(AppDbContext db) => _db = db;

    public async Task<int> NextAsync(Guid tenantId, CancellationToken ct = default)
    {
        var highest = await _db.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .MaxAsync(t => (int?)t.TicketNumber, ct);

        return (highest ?? 0) + 1;
    }
}
