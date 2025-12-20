using IAMS.Shared.DTOs;
using Microsoft.JSInterop;

namespace IAMS.Web.Services;

public class NotificationService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ApiClient _apiClient;
    private readonly ITokenProvider _tokenProvider;
    private DotNetObjectReference<NotificationService>? _dotNetRef;
    private bool _isConnected;
    private List<NotificationDto> _notifications = new();
    private int _unreadCount;

    public event Func<Task>? OnNotificationsChanged;
    public event Func<NotificationDto, Task>? OnNewNotification;
    public event Func<bool, Task>? OnConnectionChanged;

    public bool IsConnected => _isConnected;
    public IReadOnlyList<NotificationDto> Notifications => _notifications;
    public int UnreadCount => _unreadCount;

    public NotificationService(IJSRuntime js, ApiClient apiClient, ITokenProvider tokenProvider)
    {
        _js = js;
        _apiClient = apiClient;
        _tokenProvider = tokenProvider;
    }

    public async Task InitializeAsync()
    {
        // Load initial notifications
        await RefreshNotificationsAsync();

        // Start SSE connection
        await StartSseConnectionAsync();
    }

    public async Task RefreshNotificationsAsync()
    {
        try
        {
            _notifications = await _apiClient.GetNotificationsAsync() ?? new();
            var count = await _apiClient.GetNotificationCountAsync();
            _unreadCount = count?.UnreadCount ?? 0;
            await NotifyChanged();
        }
        catch
        {
            // Silently fail if not authenticated
        }
    }

    private async Task StartSseConnectionAsync()
    {
        try
        {
            var token = await _tokenProvider.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return;

            _dotNetRef = DotNetObjectReference.Create(this);

            // Build the SSE URL with token as query parameter (since EventSource doesn't support headers)
            var baseUrl = _apiClient.GetBaseUrl();
            var sseUrl = $"{baseUrl}/api/notifications/stream?access_token={token}";

            await _js.InvokeVoidAsync("notificationService.start", sseUrl, _dotNetRef);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start SSE: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("notificationService.stop");
        }
        catch
        {
            // Ignore errors during cleanup
        }
    }

    [JSInvokable]
    public async Task OnConnected()
    {
        _isConnected = true;
        if (OnConnectionChanged is not null)
            await OnConnectionChanged(true);
    }

    [JSInvokable]
    public async Task OnDisconnected()
    {
        _isConnected = false;
        if (OnConnectionChanged is not null)
            await OnConnectionChanged(false);
    }

    [JSInvokable]
    public async Task OnReconnecting()
    {
        await StartSseConnectionAsync();
    }

    [JSInvokable]
    public async Task OnNotificationReceived(NotificationDto notification)
    {
        // Add to the top of the list
        _notifications.Insert(0, notification);
        _unreadCount++;

        if (OnNewNotification is not null)
            await OnNewNotification(notification);

        await NotifyChanged();
    }

    public async Task MarkAsReadAsync(int notificationId)
    {
        await _apiClient.MarkNotificationAsReadAsync(notificationId);

        var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
        if (notification is not null && !notification.IsRead)
        {
            // Update local state
            var index = _notifications.IndexOf(notification);
            _notifications[index] = notification with { IsRead = true, ReadAt = DateTime.UtcNow };
            _unreadCount = Math.Max(0, _unreadCount - 1);
            await NotifyChanged();
        }
    }

    public async Task MarkAllAsReadAsync()
    {
        await _apiClient.MarkAllNotificationsAsReadAsync();

        // Update local state
        _notifications = _notifications.Select(n => n with { IsRead = true, ReadAt = DateTime.UtcNow }).ToList();
        _unreadCount = 0;
        await NotifyChanged();
    }

    public async Task DeleteNotificationAsync(int notificationId)
    {
        await _apiClient.DeleteNotificationAsync(notificationId);

        var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
        if (notification is not null)
        {
            _notifications.Remove(notification);
            if (!notification.IsRead)
                _unreadCount = Math.Max(0, _unreadCount - 1);
            await NotifyChanged();
        }
    }

    private async Task NotifyChanged()
    {
        if (OnNotificationsChanged is not null)
            await OnNotificationsChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _dotNetRef?.Dispose();
    }
}
