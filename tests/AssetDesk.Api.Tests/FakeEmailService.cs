using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Records what would have been mailed instead of sending it. <see cref="ShouldSucceed"/> flips
/// the return value so a test can assert the "SMTP is down" path without an SMTP server that is
/// actually down - that path matters here, because the endpoint deliberately refuses to lock an
/// account it could not deliver a reset link for.
/// </summary>
public class FakeEmailService : IEmailService
{
    public bool ShouldSucceed { get; set; } = true;

    /// <summary>Every delivery attempt, successful or not, in order.</summary>
    public List<(string To, string ResetUrl)> PasswordResets { get; } = [];

    public Task<bool> SendPasswordResetEmailAsync(string toEmail, string resetUrl, CancellationToken ct = default)
    {
        PasswordResets.Add((toEmail, resetUrl));
        return Task.FromResult(ShouldSucceed);
    }

    /// Nothing under test sends general mail; fail loudly rather than silently returning true.
    public Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent, CancellationToken ct = default)
        => throw new NotSupportedException();
}
