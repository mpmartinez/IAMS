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
