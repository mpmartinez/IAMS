namespace AssetDesk.Shared.DTOs;

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
    /// <summary>
    /// The tenant the notification belongs to. Server-side callers set this explicitly:
    /// AppDbContext only stamps the tenant from the JWT claim when the column is still empty,
    /// and that ambient stamp is unavailable off the request thread - a notification written
    /// without a tenant is hidden by the query filter forever, with nothing to show it failed.
    ///
    /// Never bound from a request body; the public endpoint takes its own request record.
    /// </summary>
    public Guid? TenantId { get; init; }

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
