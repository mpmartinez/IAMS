namespace IAMS.Api.Services;

public interface IEmailService
{
    Task<bool> SendPasswordResetEmailAsync(string toEmail, string resetUrl);
    Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent);
}
