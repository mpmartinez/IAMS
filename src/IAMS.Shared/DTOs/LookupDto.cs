namespace IAMS.Shared.DTOs;

public record LookupValueDto
{
    public int Id { get; init; }
    public required string LookupType { get; init; }
    public required string Value { get; init; }
    public required string Label { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Metadata for a lookup vocabulary - used to drive the "pick a lookup type" selector
/// on the admin screen and to explain why a locked type cannot be edited there.</summary>
public record LookupTypeDto
{
    public required string Type { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEditable { get; init; }
    public string? LockedReason { get; init; }
}

public record CreateLookupValueDto
{
    public required string LookupType { get; init; }
    public required string Value { get; init; }
    public required string Label { get; init; }
    public int? SortOrder { get; init; }
}

public record UpdateLookupValueDto
{
    public string? Label { get; init; }
    public int? SortOrder { get; init; }
    public bool? IsActive { get; init; }
}

/// <summary>
/// The lookup type key strings, mirrored from IAMS.Api.Entities.LookupTypes so Web call sites
/// (ApiClient, the admin screen) do not have to spell them as string literals. The API is the
/// source of truth for which of these are editable vs locked - see LookupTypeDto - this class
/// exists only so both sides agree on the identifiers.
/// </summary>
public static class LookupTypeNames
{
    public const string DeviceType = "DeviceType";
    public const string Currency = "Currency";
    public const string AttachmentCategory = "AttachmentCategory";
    public const string TicketAttachmentCategory = "TicketAttachmentCategory";
    public const string TicketCategory = "TicketCategory";

    public const string TicketStatus = "TicketStatus";
    public const string TicketType = "TicketType";
    public const string AssetStatus = "AssetStatus";
    public const string TicketPriority = "TicketPriority";
}
