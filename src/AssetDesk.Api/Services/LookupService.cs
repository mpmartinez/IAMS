using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface ILookupService
{
    /// <summary>
    /// True when <paramref name="value"/> is a currently active value of
    /// <paramref name="lookupType"/> - the check every "is this a valid DeviceType/Currency/
    /// Category" validation site should use once that field is admin-editable data instead of
    /// a compile-time constant.
    /// </summary>
    Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default);

    /// <summary>
    /// The active values of <paramref name="lookupType"/>, in display order - what the small
    /// "give me the dropdown options" convenience endpoints (device-types, currencies,
    /// attachment categories, ...) return instead of a compile-time constant array.
    /// </summary>
    Task<List<string>> GetActiveValuesAsync(string lookupType, CancellationToken ct = default);
}

/// <summary>
/// Reads editable reference data (see <see cref="LookupTypes"/>) from the LookupValue table.
/// LookupsController is the only place that writes to it.
/// </summary>
public class LookupService(AppDbContext db) : ILookupService
{
    public async Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default)
    {
        var hasAnyRows = await db.LookupValues.AnyAsync(l => l.LookupType == lookupType, ct);

        if (!hasAnyRows)
        {
            // The table has never been seeded for this type - a deployment whose database
            // predates the seeding migration (EnsureCreated() actually does apply the
            // LookupValue HasData rows, same as migrations, so this only bites a database that
            // has neither run yet). Fall back to the compile-time constant list so validation
            // behaves exactly as it did before lookups existed, rather than rejecting every
            // value outright.
            return LookupTypes.FallbackValues(lookupType).Contains(value);
        }

        return await db.LookupValues
            .AnyAsync(l => l.LookupType == lookupType && l.Value == value && l.IsActive, ct);
    }

    public async Task<List<string>> GetActiveValuesAsync(string lookupType, CancellationToken ct = default)
    {
        var hasAnyRows = await db.LookupValues.AnyAsync(l => l.LookupType == lookupType, ct);

        if (!hasAnyRows)
        {
            // Same unseeded-table fallback as IsActiveValueAsync - see its comment. Checked
            // against ANY row for the type, not just active ones: a type an admin deactivated
            // down to zero active values must return empty, not silently resurrect the
            // constant list.
            return [.. LookupTypes.FallbackValues(lookupType)];
        }

        return await db.LookupValues
            .Where(l => l.LookupType == lookupType && l.IsActive)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Label)
            .Select(l => l.Value)
            .ToListAsync(ct);
    }
}
