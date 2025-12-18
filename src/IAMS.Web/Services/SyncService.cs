using System.Net.Http.Json;
using System.Text.Json;
using IAMS.Shared.DTOs;

namespace IAMS.Web.Services;

public class SyncService : IAsyncDisposable
{
    private readonly OfflineStorageService _offlineStorage;
    private readonly NetworkStatusService _networkStatus;
    private readonly ApiClient _apiClient;
    private readonly ILogger<SyncService> _logger;

    private bool _isSyncing;
    private CancellationTokenSource? _syncCts;

    public event Func<SyncStatus, Task>? OnSyncStatusChanged;
    public event Func<int, Task>? OnPendingCountChanged;

    public SyncStatus Status { get; private set; } = new();

    public SyncService(
        OfflineStorageService offlineStorage,
        NetworkStatusService networkStatus,
        ApiClient apiClient,
        ILogger<SyncService> logger)
    {
        _offlineStorage = offlineStorage;
        _networkStatus = networkStatus;
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await _offlineStorage.InitializeAsync();
        await _networkStatus.InitializeAsync();

        // Subscribe to network status changes
        _networkStatus.OnStatusChanged += OnNetworkStatusChangedAsync;

        // Update initial status
        await UpdateStatusAsync();

        // If online, trigger sync
        if (_networkStatus.IsOnline)
        {
            _ = SyncAsync();
        }
    }

    private async Task OnNetworkStatusChangedAsync(bool isOnline)
    {
        await UpdateStatusAsync();

        if (isOnline && !_isSyncing)
        {
            await SyncAsync();
        }
    }

    public async Task UpdateStatusAsync()
    {
        var info = await _offlineStorage.GetStorageInfoAsync();

        Status = new SyncStatus
        {
            IsOnline = _networkStatus.IsOnline,
            IsSyncing = _isSyncing,
            PendingActionCount = info.PendingActionCount,
            CachedAssetCount = info.AssetCount,
            LastSyncTime = info.LastSyncTime != null ? DateTime.Parse(info.LastSyncTime) : null
        };

        if (OnSyncStatusChanged != null)
        {
            await OnSyncStatusChanged.Invoke(Status);
        }
    }

    public async Task<bool> SyncAsync()
    {
        if (_isSyncing || !_networkStatus.IsOnline)
        {
            return false;
        }

        _isSyncing = true;
        _syncCts = new CancellationTokenSource();

        try
        {
            await UpdateStatusAsync();

            // 1. Sync pending actions first
            await SyncPendingActionsAsync(_syncCts.Token);

            // 2. Refresh asset cache from server
            await RefreshAssetCacheAsync(_syncCts.Token);

            // 3. Update last sync time
            await _offlineStorage.SetLastSyncTimeAsync(DateTime.UtcNow);

            // 4. Clean up synced actions
            await _offlineStorage.ClearSyncedActionsAsync();

            _logger.LogInformation("Sync completed successfully");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync was cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            return false;
        }
        finally
        {
            _isSyncing = false;
            _syncCts?.Dispose();
            _syncCts = null;
            await UpdateStatusAsync();
        }
    }

    private async Task SyncPendingActionsAsync(CancellationToken cancellationToken)
    {
        var pendingActions = await _offlineStorage.GetPendingActionsAsync();

        foreach (var action in pendingActions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var success = await ExecutePendingActionAsync(action);

                if (success)
                {
                    await _offlineStorage.MarkActionSyncedAsync(action.Id);
                    _logger.LogInformation("Synced action {ActionId} ({Type})", action.Id, action.Type);
                }
                else
                {
                    action.RetryCount++;
                    if (action.RetryCount >= 3)
                    {
                        action.ErrorMessage = "Max retries exceeded";
                        await _offlineStorage.MarkActionSyncedAsync(action.Id); // Mark as synced to stop retrying
                    }
                }

                if (OnPendingCountChanged != null)
                {
                    var count = await _offlineStorage.GetPendingActionCountAsync();
                    await OnPendingCountChanged.Invoke(count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync action {ActionId}", action.Id);
                action.ErrorMessage = ex.Message;
                action.RetryCount++;
            }
        }
    }

    private async Task<bool> ExecutePendingActionAsync(PendingAction action)
    {
        try
        {
            switch (action.Type)
            {
                case "AssignAsset":
                    if (action.AssetId.HasValue && !string.IsNullOrEmpty(action.Payload))
                    {
                        var assignDto = JsonSerializer.Deserialize<AssignAssetRequest>(action.Payload);
                        if (assignDto != null)
                        {
                            var result = await _apiClient.AssignAssetAsync(action.AssetId.Value, assignDto.UserId, assignDto.Notes);
                            return result.Success;
                        }
                    }
                    break;

                case "UnassignAsset":
                case "QuickCheckIn":
                    if (action.AssetId.HasValue)
                    {
                        var notes = action.Payload ?? "Returned (synced from offline)";
                        var result = await _apiClient.ReturnAssetAsync(action.AssetId.Value, notes);
                        return result.Success;
                    }
                    else if (!string.IsNullOrEmpty(action.AssetTag))
                    {
                        var asset = await _apiClient.GetAssetByTagAsync(action.AssetTag);
                        if (asset != null)
                        {
                            var result = await _apiClient.ReturnAssetAsync(asset.Id, "Quick check-in (synced from offline)");
                            return result.Success;
                        }
                    }
                    break;

                case "UpdateAssetStatus":
                    if (action.AssetId.HasValue && !string.IsNullOrEmpty(action.Payload))
                    {
                        var statusDto = JsonSerializer.Deserialize<UpdateStatusRequest>(action.Payload);
                        if (statusDto != null)
                        {
                            return await _apiClient.UpdateAssetStatusAsync(action.AssetId.Value, statusDto.Status);
                        }
                    }
                    break;

                case "QuickCheckOut":
                    if (!string.IsNullOrEmpty(action.AssetTag) && !string.IsNullOrEmpty(action.Payload))
                    {
                        var checkoutDto = JsonSerializer.Deserialize<QuickCheckoutRequest>(action.Payload);
                        if (checkoutDto != null)
                        {
                            var asset = await _apiClient.GetAssetByTagAsync(action.AssetTag);
                            if (asset != null)
                            {
                                var result = await _apiClient.AssignAssetAsync(asset.Id, checkoutDto.UserId, "Quick check-out (synced from offline)");
                                return result.Success;
                            }
                        }
                    }
                    break;

                default:
                    _logger.LogWarning("Unknown pending action type: {Type}", action.Type);
                    return true; // Mark as synced to remove from queue
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing pending action {Type}", action.Type);
            return false;
        }

        return false;
    }

    private async Task RefreshAssetCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Fetch all assets (for small datasets) or paginate for large ones
            var response = await _apiClient.GetAssetsAsync(pageSize: 1000);

            if (response?.Items != null)
            {
                await _offlineStorage.ClearAssetsAsync();
                await _offlineStorage.SaveAssetsAsync(response.Items.ToList());
                _logger.LogInformation("Cached {Count} assets for offline use", response.Items.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh asset cache");
        }
    }

    public async Task QueueActionAsync(string type, int? assetId = null, string? assetTag = null, object? payload = null)
    {
        var action = new PendingAction
        {
            Type = type,
            AssetId = assetId,
            AssetTag = assetTag,
            Payload = payload != null ? JsonSerializer.Serialize(payload) : null
        };

        await _offlineStorage.AddPendingActionAsync(action);
        await UpdateStatusAsync();

        if (OnPendingCountChanged != null)
        {
            var count = await _offlineStorage.GetPendingActionCountAsync();
            await OnPendingCountChanged.Invoke(count);
        }

        // If online, try to sync immediately
        if (_networkStatus.IsOnline)
        {
            _ = SyncAsync();
        }
    }

    public void CancelSync()
    {
        _syncCts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _networkStatus.OnStatusChanged -= OnNetworkStatusChangedAsync;
        _syncCts?.Cancel();
        _syncCts?.Dispose();
    }
}

public class SyncStatus
{
    public bool IsOnline { get; set; }
    public bool IsSyncing { get; set; }
    public int PendingActionCount { get; set; }
    public int CachedAssetCount { get; set; }
    public DateTime? LastSyncTime { get; set; }
    public string? LastError { get; set; }
}

// Request DTOs for deserialization
internal class AssignAssetRequest
{
    public string UserId { get; set; } = "";
    public string? Notes { get; set; }
}

internal class UpdateStatusRequest
{
    public string Status { get; set; } = "";
}

internal class QuickCheckoutRequest
{
    public string UserId { get; set; } = "";
}
