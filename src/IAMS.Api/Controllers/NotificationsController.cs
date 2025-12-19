using System.Security.Claims;
using System.Text.Json;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>
    /// Get notifications for current user
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<NotificationDto>>>> GetNotifications([FromQuery] int take = 20)
    {
        var notifications = await _notificationService.GetUserNotificationsAsync(GetUserId(), take);
        return Ok(ApiResponse<List<NotificationDto>>.Ok(notifications));
    }

    /// <summary>
    /// Get notification count for current user
    /// </summary>
    [HttpGet("count")]
    public async Task<ActionResult<ApiResponse<NotificationCountDto>>> GetCount()
    {
        var count = await _notificationService.GetUserNotificationCountAsync(GetUserId());
        return Ok(ApiResponse<NotificationCountDto>.Ok(count));
    }

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    [HttpPost("{id:int}/read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAsRead(int id)
    {
        await _notificationService.MarkAsReadAsync(id, GetUserId());
        return Ok(ApiResponse<bool>.Ok(true, "Notification marked as read"));
    }

    /// <summary>
    /// Mark all notifications as read
    /// </summary>
    [HttpPost("read-all")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAllAsRead()
    {
        await _notificationService.MarkAllAsReadAsync(GetUserId());
        return Ok(ApiResponse<bool>.Ok(true, "All notifications marked as read"));
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(int id)
    {
        await _notificationService.DeleteNotificationAsync(id, GetUserId());
        return Ok(ApiResponse<bool>.Ok(true, "Notification deleted"));
    }

    /// <summary>
    /// Server-Sent Events endpoint for real-time notifications
    /// </summary>
    [HttpGet("stream")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task StreamNotifications(CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no"); // For nginx

        var userId = GetUserId();

        try
        {
            // Send initial heartbeat
            await SendEventAsync("connected", new { userId, timestamp = DateTime.UtcNow });

            // Keep connection alive with periodic heartbeats
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = SendHeartbeatsAsync(heartbeatCts.Token);

            await foreach (var notification in _notificationService.SubscribeAsync(userId, cancellationToken))
            {
                await SendEventAsync("notification", notification);
            }

            heartbeatCts.Cancel();
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - expected behavior
        }
    }

    private async Task SendEventAsync<T>(string eventType, T data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await Response.WriteAsync($"event: {eventType}\n");
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }

    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await SendEventAsync("heartbeat", new { timestamp = DateTime.UtcNow });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    /// <summary>
    /// Create a test notification (Admin only - for testing)
    /// </summary>
    [HttpPost("test")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<NotificationDto>>> CreateTestNotification([FromBody] CreateTestNotificationRequest request)
    {
        // Validate link - must start with "/" or be null/empty
        var link = request.Link;
        if (!string.IsNullOrEmpty(link) && !link.StartsWith("/"))
        {
            link = null; // Ignore invalid links like "string" placeholder
        }

        var dto = new CreateNotificationDto
        {
            UserId = GetUserId(),
            Title = request.Title ?? "Test Notification",
            Message = request.Message ?? "This is a test notification",
            Type = request.Type ?? "Info",
            Link = link
        };

        var notification = await _notificationService.CreateNotificationAsync(dto);
        return Ok(ApiResponse<NotificationDto>.Ok(notification, "Notification created"));
    }
}

public record CreateTestNotificationRequest
{
    /// <summary>Notification title</summary>
    /// <example>New Asset Added</example>
    public string? Title { get; init; }

    /// <summary>Notification message</summary>
    /// <example>A new laptop has been added to inventory</example>
    public string? Message { get; init; }

    /// <summary>Type: Info, Warning, Error, or Success</summary>
    /// <example>Info</example>
    public string? Type { get; init; }

    /// <summary>Link to navigate to (must start with /)</summary>
    /// <example>/assets</example>
    public string? Link { get; init; }
}
