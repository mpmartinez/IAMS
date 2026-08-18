using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAMS.Api.Controllers;

/// <summary>
/// The platform's SMTP server, edited at runtime so a deployment can start sending
/// password-reset mail without a redeploy. Global, not tenant-scoped - one mail server for
/// everyone - so it is SuperAdmin-only, like /api/lookups writes and /api/tenants.
///
/// The stored password is write-only over this API: it goes in on PUT and never comes back
/// out on GET.
/// </summary>
[ApiController]
[Route("api/settings/email")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "SuperAdmin")]
public class EmailSettingsController(
    ISystemSettingService settings,
    IEmailService emailService,
    ILogger<EmailSettingsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<EmailSettingsDto>>> Get(CancellationToken ct)
    {
        var stored = await settings.GetByPrefixAsync(SystemSettingKeys.EmailPrefix, ct);

        string? Value(string key) =>
            stored.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var dto = new EmailSettingsDto
        {
            SmtpHost = Value(SystemSettingKeys.SmtpHost) ?? "",
            SmtpPort = int.TryParse(Value(SystemSettingKeys.SmtpPort), out var port) ? port : 587,
            UseSsl = !bool.TryParse(Value(SystemSettingKeys.UseSsl), out var ssl) || ssl,
            SenderEmail = Value(SystemSettingKeys.SenderEmail) ?? "",
            SenderName = Value(SystemSettingKeys.SenderName) ?? "",
            Username = Value(SystemSettingKeys.Username) ?? "",
            HasPassword = Value(SystemSettingKeys.Password) is not null,
            IsConfigured = Value(SystemSettingKeys.SmtpHost) is not null
        };

        return Ok(ApiResponse<EmailSettingsDto>.Ok(dto));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<object>>> Update(UpdateEmailSettingsDto dto, CancellationToken ct)
    {
        if (dto.SmtpPort is < 1 or > 65535)
            return BadRequest(ApiResponse<object>.Fail("SMTP port must be between 1 and 65535."));

        if (!string.IsNullOrWhiteSpace(dto.SenderEmail) && !dto.SenderEmail.Contains('@'))
            return BadRequest(ApiResponse<object>.Fail("Sender email is not a valid email address."));

        var updates = new Dictionary<string, string?>
        {
            [SystemSettingKeys.SmtpHost] = dto.SmtpHost?.Trim(),
            [SystemSettingKeys.SmtpPort] = dto.SmtpPort.ToString(),
            [SystemSettingKeys.UseSsl] = dto.UseSsl ? "true" : "false",
            [SystemSettingKeys.SenderEmail] = dto.SenderEmail?.Trim(),
            [SystemSettingKeys.SenderName] = dto.SenderName?.Trim(),
            [SystemSettingKeys.Username] = dto.Username?.Trim()
        };

        // Blank means "keep what is stored", so the admin can edit the host without having to
        // re-type the password (which the GET above deliberately never gave them back).
        // ClearPassword is the explicit escape hatch for actually removing it.
        if (!string.IsNullOrEmpty(dto.Password))
            updates[SystemSettingKeys.Password] = dto.Password;
        else if (dto.ClearPassword)
            updates[SystemSettingKeys.Password] = null;

        await settings.SetManyAsync(updates, ct);

        logger.LogInformation(
            "Email settings updated by {UserId} from {Ip}. Host={Host}, Port={Port}",
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(),
            dto.SmtpHost, dto.SmtpPort);

        return Ok(ApiResponse<object>.Ok(new { }, "Email settings saved"));
    }

    /// <summary>
    /// Sends a test message with the settings currently stored, so an admin can confirm
    /// password-reset mail will go out before a user needs it. Save first - this reads the
    /// stored values, not the unsaved form.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<object>>> Test(TestEmailDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.TestEmail) || !dto.TestEmail.Contains('@'))
            return BadRequest(ApiResponse<object>.Fail("Enter a valid recipient email address."));

        var sent = await emailService.SendEmailAsync(
            dto.TestEmail,
            "IAMS - SMTP test",
            "<h2>SMTP test successful</h2><p>Your IAMS email settings are working. Password reset emails will be delivered.</p>",
            ct);

        return sent
            ? Ok(ApiResponse<object>.Ok(new { }, $"Test email sent to {dto.TestEmail}."))
            : BadRequest(ApiResponse<object>.Fail("Could not send the test email. Check the settings above and the API logs."));
    }
}
