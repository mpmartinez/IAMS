namespace IAMS.Shared.DTOs;

public record TicketListItemDto
{
    public int Id { get; init; }
    public int TicketNumber { get; init; }
    public required string Type { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public int? AssetId { get; init; }
    public string? AssetTag { get; init; }
    public string? RequesterName { get; init; }
    public string? RequesterDepartment { get; init; }
    public string? AssignedToName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DueAt { get; init; }

    public string Reference => $"TKT-{TicketNumber:D4}";
}

public record TicketDto : TicketListItemDto
{
    public string? Description { get; init; }
    public string? Resolution { get; init; }
    public string? RequesterUserId { get; init; }
    public string? AssignedToUserId { get; init; }
    public string? AssetName { get; init; }
    public string? AssetStatus { get; init; }
    public DateTime? WarrantyEndDate { get; init; }
    public DateTime? AssignedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public int? AssetAssignmentId { get; init; }
    public List<TicketCommentDto> Comments { get; init; } = [];
}

public record TicketCommentDto
{
    public int Id { get; init; }
    public required string Body { get; init; }
    public bool IsInternal { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorUserId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record TicketSummaryDto
{
    public int Open { get; init; }
    public int Unassigned { get; init; }
    public int InProgress { get; init; }
    public int Overdue { get; init; }
}

public record CreateTicketRequest
{
    public required string Type { get; init; }
    public string Category { get; init; } = "Other";
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string Priority { get; init; } = "Medium";
    public int? AssetId { get; init; }
}

public record AssignTicketRequest
{
    public required string AssignedToUserId { get; init; }
}

public record ChangeTicketStatusRequest
{
    public required string Status { get; init; }
}

public record ResolveTicketRequest
{
    public required string Resolution { get; init; }
}

public record FulfilTicketRequest
{
    public int AssetId { get; init; }
    public required string Resolution { get; init; }
}

public record AddTicketCommentRequest
{
    public required string Body { get; init; }
    public bool IsInternal { get; init; }
}
