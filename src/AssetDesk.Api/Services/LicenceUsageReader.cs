using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public sealed record LicenceUsage(int SeatsOwned, int SeatsAssigned, int Reclaimable, decimal SpendInPesos)
{
    public static readonly LicenceUsage None = new(0, 0, 0, 0m);

    public int OverAssigned => Math.Max(0, SeatsAssigned - SeatsOwned);
}

public interface ILicenceUsageReader
{
    /// <summary>Usage per licence id, for every licence in the tenant that has any, or just one.</summary>
    Task<Dictionary<int, LicenceUsage>> ReadAsync(
        Guid tenantId, int? licenceId = null, CancellationToken ct = default);
}

/// <summary>
/// The one place seats owned, seats assigned, reclaimable seats and spend are worked out. The
/// licence screens and the compliance report both read through here, so the two cannot disagree
/// about what "over-assigned" means.
/// </summary>
public class LicenceUsageReader(AppDbContext db) : ILicenceUsageReader
{
    public async Task<Dictionary<int, LicenceUsage>> ReadAsync(
        Guid tenantId, int? licenceId = null, CancellationToken ct = default)
    {
        var entitlements = await db.LicenceEntitlements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && (licenceId == null || e.SoftwareLicenceId == licenceId))
            .Select(e => new { e.SoftwareLicenceId, e.SeatsAdded, e.Cost, e.ExchangeRate })
            .ToListAsync(ct);

        var activeSeats = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                     && s.ReleasedAt == null
                     && (licenceId == null || s.SoftwareLicenceId == licenceId))
            .Select(s => new
            {
                s.SoftwareLicenceId,
                UserIsActive = s.User == null ? (bool?)null : s.User.IsActive,
                AssetStatus = s.Asset == null ? null : s.Asset.Status
            })
            .ToListAsync(ct);

        // Spend is summed here rather than in SQL: SQLite, which the test suite runs on, cannot
        // aggregate a decimal, and the report and the screens must agree to the centavo.
        var owned = entitlements.ToLookup(e => e.SoftwareLicenceId);
        var seats = activeSeats.ToLookup(s => s.SoftwareLicenceId);

        return owned.Select(g => g.Key)
            .Union(seats.Select(g => g.Key))
            .ToDictionary(id => id, id => new LicenceUsage(
                SeatsOwned: owned[id].Sum(e => e.SeatsAdded),
                SeatsAssigned: seats[id].Count(),
                Reclaimable: seats[id].Count(s => LicenceRules.IsReclaimable(s.UserIsActive, s.AssetStatus)),
                SpendInPesos: owned[id].Sum(e => e.Cost * e.ExchangeRate)));
    }
}
