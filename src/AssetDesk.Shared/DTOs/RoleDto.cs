using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

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
    // Matches AspNetRoles.Name (ASP.NET Identity's default NameMaxLength) and
    // ApplicationRole.Description's HasMaxLength(500) in AppDbContext. SQLite ignores column
    // lengths, so only these attributes - validated by [ApiController] before the action runs -
    // stand between an over-long value and a raw Npgsql 22001 error turning into an unhandled 500
    // in production.
    [StringLength(256)]
    public required string Name { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    public List<string> Permissions { get; init; } = [];
}

public record UpdateRoleDto
{
    // Deliberately has no Name property. That omission is the only thing preventing built-in
    // roles from being renamed through this endpoint - RolesController.UpdateRole has no other
    // guard against it. If a future change adds Name here, it must come with a check that
    // built-in roles reject a rename, or a shared role name silently changes for every tenant.
    [StringLength(500)]
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
