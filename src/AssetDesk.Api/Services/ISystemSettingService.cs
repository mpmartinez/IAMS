namespace AssetDesk.Api.Services;

/// <summary>
/// Read/write access to the global SystemSettings table. Values are plain strings; callers
/// parse them. See SystemSettingKeys for the key vocabulary.
/// </summary>
public interface ISystemSettingService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Every setting whose key starts with <paramref name="prefix"/>, keyed by full key.</summary>
    Task<Dictionary<string, string?>> GetByPrefixAsync(string prefix, CancellationToken ct = default);

    Task SetAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>Upserts a batch in one round trip. Keys absent from the dictionary are left alone.</summary>
    Task SetManyAsync(IDictionary<string, string?> settings, CancellationToken ct = default);
}
