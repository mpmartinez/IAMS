namespace IAMS.Shared.DTOs;

public record NotificationDto
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Type { get; init; } = "Info";
    public string? Link { get; init; }
    public string? RelatedEntityType { get; init; }
    public int? RelatedEntityId { get; init; }
    public bool IsRead { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - CreatedAt;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
            return CreatedAt.ToString("MMM d");
        }
    }
}

public record CreateNotificationDto
{
    public string UserId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Type { get; init; } = "Info";
    public string? Link { get; init; }
    public string? RelatedEntityType { get; init; }
    public int? RelatedEntityId { get; init; }
}

public record NotificationCountDto
{
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }
}
