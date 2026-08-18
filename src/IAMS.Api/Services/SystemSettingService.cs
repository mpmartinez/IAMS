using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public class SystemSettingService(AppDbContext db) : ISystemSettingService
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var setting = await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        return setting?.Value;
    }

    public async Task<Dictionary<string, string?>> GetByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        return await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith(prefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
    }

    public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
        SetManyAsync(new Dictionary<string, string?> { [key] = value }, ct);

    public async Task SetManyAsync(IDictionary<string, string?> settings, CancellationToken ct = default)
    {
        if (settings.Count == 0)
            return;

        var keys = settings.Keys.ToList();
        var existing = await db.SystemSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s, ct);

        foreach (var (key, value) in settings)
        {
            if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
