using Microsoft.JSInterop;
using IAMS.Shared.DTOs;
using System.Text.Json;

namespace IAMS.Web.Services;

public class OfflineStorageService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private bool _initialized;

    public OfflineStorageService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            await _js.InvokeVoidAsync("iamsOffline.init");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize offline storage: {ex.Message}");
        }
    }

    // Asset operations
    public async Task SaveAssetsAsync(List<AssetDto> assets)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.saveAssets", assets);
    }

    public async Task<List<AssetDto>> GetAssetsAsync()
    {
        await InitializeAsync();
        var result = await _js.InvokeAsync<List<AssetDto>>("iamsOffline.getAssets");
        return result ?? new List<AssetDto>();
    }

    public async Task<AssetDto?> GetAssetAsync(int id)
    {
        await InitializeAsync();
        return await _js.InvokeAsync<AssetDto?>("iamsOffline.getAsset", id);
    }

    public async Task<AssetDto?> GetAssetByTagAsync(string assetTag)
    {
        await InitializeAsync();
        return await _js.InvokeAsync<AssetDto?>("iamsOffline.getAssetByTag", assetTag);
    }

    public async Task SaveAssetAsync(AssetDto asset)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.saveAsset", asset);
    }

    public async Task DeleteAssetAsync(int id)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.deleteAsset", id);
    }

    public async Task ClearAssetsAsync()
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.clearAssets");
    }

    // Pending actions
    public async Task<int> AddPendingActionAsync(PendingAction action)
    {
        await InitializeAsync();
        return await _js.InvokeAsync<int>("iamsOffline.addPendingAction", action);
    }

    public async Task<List<PendingAction>> GetPendingActionsAsync()
    {
        await InitializeAsync();
        var result = await _js.InvokeAsync<List<PendingAction>>("iamsOffline.getPendingActions");
        return result ?? new List<PendingAction>();
    }

    public async Task<int> GetPendingActionCountAsync()
    {
        await InitializeAsync();
        return await _js.InvokeAsync<int>("iamsOffline.getPendingActionCount");
    }

    public async Task MarkActionSyncedAsync(int id)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.markActionSynced", id);
    }

    public async Task DeletePendingActionAsync(int id)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.deletePendingAction", id);
    }

    public async Task ClearSyncedActionsAsync()
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.clearSyncedActions");
    }

    // Metadata
    public async Task SetLastSyncTimeAsync(DateTime? time = null)
    {
        await InitializeAsync();
        await _js.InvokeVoidAsync("iamsOffline.setLastSyncTime", time?.ToString("o"));
    }

    public async Task<DateTime?> GetLastSyncTimeAsync()
    {
        await InitializeAsync();
        var result = await _js.InvokeAsync<string?>("iamsOffline.getLastSyncTime");
        return result != null ? DateTime.Parse(result) : null;
    }

    // Storage info
    public async Task<OfflineStorageInfo> GetStorageInfoAsync()
    {
        await InitializeAsync();
        return await _js.InvokeAsync<OfflineStorageInfo>("iamsOffline.getStorageInfo");
    }

    // Network status
    public async Task<bool> IsOnlineAsync()
    {
        await InitializeAsync();
        return await _js.InvokeAsync<bool>("iamsOffline.isOnline");
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup if needed
    }
}

public class PendingAction
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string? Payload { get; set; }
    public int? AssetId { get; set; }
    public string? AssetTag { get; set; }
    public string Timestamp { get; set; } = "";
    public bool Synced { get; set; }
    public string? SyncedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
}

public class OfflineStorageInfo
{
    public int AssetCount { get; set; }
    public int PendingActionCount { get; set; }
    public string? LastSyncTime { get; set; }
    public bool IsOnline { get; set; }
}
