namespace IAMS.Api.Services;

public interface IEmailService
{
    Task<bool> SendPasswordResetEmailAsync(string toEmail, string resetUrl, CancellationToken ct = default);
    Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent, CancellationToken ct = default);
}
