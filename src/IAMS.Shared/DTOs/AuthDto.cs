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
    public TenantSummaryDto? Tenant { get; init; }
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

// Deliberately narrower than UpdateUserDto: Role, Email, IsActive and TenantId are absent from
// this contract, so a crafted payload has nothing to bind to and a user cannot escalate
// themselves through the self-service endpoint. Do not widen it to share UpdateUserDto.
public record UpdateProfileDto
{
    public required string FullName { get; init; }
    public string? Department { get; init; }
}
