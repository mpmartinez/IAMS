namespace IAMS.Shared.DTOs;

public record UserDto
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string FullName { get; init; }
    public string? Department { get; init; }
    public required string Role { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }

    // Multi-tenancy
    public Guid TenantId { get; init; }
    public string? TenantName { get; init; }
    public bool IsTenantAdmin { get; init; }
    public bool IsSuperAdmin { get; init; }
}

public record CreateUserDto
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string FullName { get; init; }
    public string? Department { get; init; }
    public string Role { get; init; } = "Staff";
}

public record UpdateUserDto
{
    public string? Email { get; init; }
    public string? FullName { get; init; }
    public string? Department { get; init; }
    public string? Role { get; init; }
    public bool? IsActive { get; init; }
}
