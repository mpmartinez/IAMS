using IAMS.Api.Entities;
using IAMS.Shared.DTOs;

namespace IAMS.Api.Mapping;

public static class TicketMapping
{
    public static TicketListItemDto ToListItem(this Ticket t) => new()
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        Type = t.Type,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        AssetId = t.AssetId,
        AssetTag = t.Asset?.AssetTag,
        RequesterName = t.RequesterUser?.FullName,
        RequesterDepartment = t.RequesterUser?.Department,
        AssignedToName = t.AssignedToUser?.FullName,
        CreatedAt = t.CreatedAt,
        DueAt = t.DueAt
    };

    public static TicketDto ToDto(this Ticket t, bool includeInternalComments) => new()
    {
        Id = t.Id,
        TicketNumber = t.TicketNumber,
        Type = t.Type,
        Title = t.Title,
        Status = t.Status,
        Priority = t.Priority,
        Description = t.Description,
        Resolution = t.Resolution,
        AssetId = t.AssetId,
        AssetTag = t.Asset?.AssetTag,
        AssetName = t.Asset?.DisplayName,
        AssetStatus = t.Asset?.Status,
        WarrantyEndDate = t.Asset?.WarrantyEndDate,
        RequesterUserId = t.RequesterUserId,
        RequesterName = t.RequesterUser?.FullName,
        RequesterDepartment = t.RequesterUser?.Department,
        AssignedToUserId = t.AssignedToUserId,
        AssignedToName = t.AssignedToUser?.FullName,
        CreatedAt = t.CreatedAt,
        AssignedAt = t.AssignedAt,
        StartedAt = t.StartedAt,
        ResolvedAt = t.ResolvedAt,
        ClosedAt = t.ClosedAt,
        DueAt = t.DueAt,
        AssetAssignmentId = t.AssetAssignmentId,
        Comments = t.Comments
            .Where(c => includeInternalComments || !c.IsInternal)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.ToDto())
            .ToList()
    };

    public static TicketCommentDto ToDto(this TicketComment c) => new()
    {
        Id = c.Id,
        Body = c.Body,
        IsInternal = c.IsInternal,
        AuthorName = c.User?.FullName,
        AuthorUserId = c.UserId,
        CreatedAt = c.CreatedAt
    };
}
