using System.Collections.Concurrent;
using System.Threading.Channels;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public interface INotificationService
{
    Task<NotificationDto> CreateNotificationAsync(CreateNotificationDto dto);
    Task<List<NotificationDto>> GetUserNotificationsAsync(string userId, int take = 20);
    Task<NotificationCountDto> GetUserNotificationCountAsync(string userId);
    Task MarkAsReadAsync(int notificationId, string userId);
    Task MarkAllAsReadAsync(string userId);
    Task DeleteNotificationAsync(int notificationId, string userId);
    IAsyncEnumerable<NotificationDto> SubscribeAsync(string userId, CancellationToken cancellationToken);
    Task BroadcastToUserAsync(string userId, NotificationDto notification);
}

public class NotificationService : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, Channel<NotificationDto>> _userChannels = new();

    public NotificationService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<NotificationDto> CreateNotificationAsync(CreateNotificationDto dto)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var notification = new Notification
        {
            UserId = dto.UserId,
            Title = dto.Title,
            Message = dto.Message,
            Type = dto.Type,
            Link = dto.Link,
            RelatedEntityType = dto.RelatedEntityType,
            RelatedEntityId = dto.RelatedEntityId,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        var notificationDto = MapToDto(notification);

        // Broadcast to connected user
        await BroadcastToUserAsync(dto.UserId, notificationDto);

        return notificationDto;
    }

    public async Task<List<NotificationDto>> GetUserNotificationsAsync(string userId, int take = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var notifications = await db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .ToListAsync();

        return notifications.Select(MapToDto).ToList();
    }

    public async Task<NotificationCountDto> GetUserNotificationCountAsync(string userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var unreadCount = await db.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);

        var totalCount = await db.Notifications
            .CountAsync(n => n.UserId == userId);

        return new NotificationCountDto
        {
            UnreadCount = unreadCount,
            TotalCount = totalCount
        };
    }

    public async Task MarkAsReadAsync(int notificationId, string userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

        if (notification is not null && !notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task MarkAllAsReadAsync(string userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow));
    }

    public async Task DeleteNotificationAsync(int notificationId, string userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId)
            .ExecuteDeleteAsync();
    }

    public async IAsyncEnumerable<NotificationDto> SubscribeAsync(
        string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = _userChannels.GetOrAdd(userId, _ => Channel.CreateUnbounded<NotificationDto>());

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return notification;
            }
        }
        finally
        {
            // Clean up channel when user disconnects
            if (_userChannels.TryRemove(userId, out var removedChannel))
            {
                removedChannel.Writer.TryComplete();
            }
        }
    }

    public async Task BroadcastToUserAsync(string userId, NotificationDto notification)
    {
        if (_userChannels.TryGetValue(userId, out var channel))
        {
            await channel.Writer.WriteAsync(notification);
        }
    }

    private static NotificationDto MapToDto(Notification notification)
    {
        return new NotificationDto
        {
            Id = notification.Id,
            Title = notification.Title,
            Message = notification.Message,
            Type = notification.Type,
            Link = notification.Link,
            RelatedEntityType = notification.RelatedEntityType,
            RelatedEntityId = notification.RelatedEntityId,
            IsRead = notification.IsRead,
            CreatedAt = notification.CreatedAt,
            ReadAt = notification.ReadAt
        };
    }
}
