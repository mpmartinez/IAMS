namespace IAMS.Shared.DTOs;

public record LoginDto
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

public record LoginResponseDto
{
    public required string Token { get; init; }
    public required UserDto User { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public record ChangePasswordDto
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}
