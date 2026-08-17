namespace IAMS.Shared.DTOs;

public record RoleDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsBuiltIn { get; init; }
    public int UserCount { get; init; }
    public required List<string> Permissions { get; init; }
}

public record AssignableRoleDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record CreateRoleDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> Permissions { get; init; } = [];
}

public record UpdateRoleDto
{
    public string? Description { get; init; }
    public required List<string> Permissions { get; init; }
}

public record PermissionDto
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
}

public record PermissionGroupDto
{
    public required string Group { get; init; }
    public required List<PermissionDto> Permissions { get; init; }
}
