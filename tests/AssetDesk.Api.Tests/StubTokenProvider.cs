using AssetDesk.Api.Entities;
using Microsoft.AspNetCore.Identity;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Stands in for Identity's DataProtectorTokenProvider. GeneratePasswordResetTokenAsync resolves
/// the "Default" provider, and a hand-built UserManager - which is what these tests use instead
/// of mocking - has none registered, so without this every path that mints a password-set token
/// dies on NotSupportedException. The real provider's cryptography is Identity's to test; what
/// matters here is that a token reaches the link.
/// </summary>
public class StubTokenProvider : IUserTwoFactorTokenProvider<ApplicationUser>
{
    public Task<string> GenerateAsync(string purpose, UserManager<ApplicationUser> manager, ApplicationUser user)
        => Task.FromResult($"reset-token-{user.Id}");

    public Task<bool> ValidateAsync(
        string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user)
        => Task.FromResult(token == $"reset-token-{user.Id}");

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        => Task.FromResult(false);
}
