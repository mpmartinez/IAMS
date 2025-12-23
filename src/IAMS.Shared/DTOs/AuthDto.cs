namespace IAMS.Shared.DTOs;

public record LoginDto
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

public record LoginResponseDto
{
    public required string Token { get; init; }
    public required string RefreshToken { get; init; }
    public required UserDto User { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime RefreshTokenExpiresAt { get; init; }
}

public record RefreshTokenRequestDto
{
    public required string RefreshToken { get; init; }
}

public record ChangePasswordDto
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}

public record ForgotPasswordDto
{
    public required string Email { get; init; }
}

public record ResetPasswordDto
{
    public required string Email { get; init; }
    public required string Token { get; init; }
    public required string NewPassword { get; init; }
}
