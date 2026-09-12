using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IAssetTagGenerator
{
    /// <param name="sequenceCache">
    /// Carried across a batch so creating many assets at once does not re-query per row. Pass a
    /// fresh dictionary for a single asset; pass one dictionary for a whole import or receipt.
    /// </param>
    Task<string> NextAsync(string deviceType, Dictionary<string, int> sequenceCache, CancellationToken ct = default);
}

/// <summary>
/// The one place asset tags are made. Format is PREFIX-yyyyMMdd-NNNN, e.g. LAP-20251218-0001.
///
/// This existed twice before - AssetsController and AssetImportService each had a private copy,
/// identical apart from the importer's sequence cache. Goods receipt would have been the third,
/// and the multi-currency work already showed what a fourth copy costs: the same aggregation
/// shipped in four places, two were missed, and the final review caught it as a Critical.
/// </summary>
public class AssetTagGenerator(AppDbContext db) : IAssetTagGenerator
{
    public static string PrefixFor(string deviceType) => deviceType switch
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

    public async Task<string> NextAsync(
        string deviceType, Dictionary<string, int> sequenceCache, CancellationToken ct = default)
    {
        var baseTag = $"{PrefixFor(deviceType)}-{DateTime.UtcNow:yyyyMMdd}-";

        if (!sequenceCache.TryGetValue(baseTag, out var current))
        {
            var todayTags = await db.Assets
                .Where(a => a.AssetTag.StartsWith(baseTag))
                .Select(a => a.AssetTag)
                .ToListAsync(ct);

            current = 0;
            foreach (var tag in todayTags)
            {
                if (int.TryParse(tag.Replace(baseTag, ""), out var seq) && seq > current)
                    current = seq;
            }
        }

        current++;
        sequenceCache[baseTag] = current;
        return $"{baseTag}{current:D4}";
    }
}
