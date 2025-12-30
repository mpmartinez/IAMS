using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace IAMS.Api.Services;

public class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string resetUrl)
    {
        var appName = _config["Email:AppName"] ?? "IAMS";
        var subject = $"Reset Your {appName} Password";

        var htmlContent = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
</head>
<body style='font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, ""Helvetica Neue"", Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <div style='background: linear-gradient(135deg, #4f46e5 0%, #6366f1 100%); padding: 30px; border-radius: 12px 12px 0 0; text-align: center;'>
        <h1 style='color: white; margin: 0; font-size: 24px;'>{appName}</h1>
    </div>
    <div style='background: #ffffff; padding: 30px; border: 1px solid #e5e7eb; border-top: none; border-radius: 0 0 12px 12px;'>
        <h2 style='color: #1f2937; margin-top: 0;'>Password Reset Request</h2>
        <p style='color: #4b5563;'>We received a request to reset your password. Click the button below to create a new password:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{resetUrl}' style='background: #4f46e5; color: white; padding: 14px 28px; text-decoration: none; border-radius: 8px; font-weight: 600; display: inline-block;'>Reset Password</a>
        </div>
        <p style='color: #6b7280; font-size: 14px;'>This link will expire in 24 hours.</p>
        <p style='color: #6b7280; font-size: 14px;'>If you didn't request this password reset, you can safely ignore this email.</p>
        <hr style='border: none; border-top: 1px solid #e5e7eb; margin: 20px 0;'>
        <p style='color: #9ca3af; font-size: 12px; text-align: center;'>This email was sent by {appName}. Please do not reply to this email.</p>
    </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, subject, htmlContent);
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent)
    {
        var host = _config["Smtp:Host"];
        var portStr = _config["Smtp:Port"];
        var username = _config["Smtp:Username"];
        var password = _config["Smtp:Password"];
        var fromEmail = _config["Smtp:FromEmail"];
        var fromName = _config["Smtp:FromName"] ?? "IAMS";
        var useSsl = _config.GetValue<bool>("Smtp:UseSsl", true);

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr))
        {
            _logger.LogWarning("SMTP is not configured. Email not sent to {Email}", toEmail);
            return false;
        }

        if (!int.TryParse(portStr, out var port))
        {
            _logger.LogError("Invalid SMTP port configuration: {Port}", portStr);
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail ?? username));
            message.To.Add(new MailboxAddress("", toEmail));
            message.Subject = subject;

            message.Body = new TextPart("html")
            {
                Text = htmlContent
            };

            using var client = new SmtpClient();

            // Determine security option based on port and config
            SecureSocketOptions secureOption;
            if (port == 465)
            {
                // Port 465 uses implicit SSL/TLS
                secureOption = SecureSocketOptions.SslOnConnect;
            }
            else if (port == 587)
            {
                // Port 587 uses STARTTLS
                secureOption = SecureSocketOptions.StartTls;
            }
            else
            {
                // Use config setting for other ports
                secureOption = useSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
            }

            await client.ConnectAsync(host, port, secureOption);

            if (!string.IsNullOrEmpty(username))
            {
                await client.AuthenticateAsync(username, password);
            }

            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
            return false;
        }
    }
}
