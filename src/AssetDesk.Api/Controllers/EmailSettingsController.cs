using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Controllers;

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
    IEmailSettingsResolver resolver,
    IEmailService emailService,
    ILogger<EmailSettingsController> logger) : ControllerBase
{
    /// <summary>
    /// The settings actually in effect, not just the ones saved here. A container that supplies
    /// Smtp__Host and friends as environment variables has working mail with an empty database,
    /// and reading only the database would report that deployment as unconfigured - which is
    /// precisely when someone is on this screen trying to work out why mail is not sending.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<EmailSettingsDto>>> Get(CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(ct);

        var dto = new EmailSettingsDto
        {
            SmtpHost = resolved.Host ?? "",
            SmtpPort = resolved.Port,
            UseSsl = resolved.UseSsl,
            SenderEmail = resolved.SenderEmail ?? "",
            SenderName = resolved.SenderName ?? "",
            Username = resolved.Username ?? "",
            HasPassword = !string.IsNullOrEmpty(resolved.Password),
            IsConfigured = resolved.IsConfigured,
            Source = resolved.Source switch
            {
                EmailSettingsSource.Database => "database",
                EmailSettingsSource.Configuration => "configuration",
                _ => "none"
            }
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
            "AssetDesk - SMTP test",
            "<h2>SMTP test successful</h2><p>Your AssetDesk email settings are working. Password reset emails will be delivered.</p>",
            ct);

        return sent
            ? Ok(ApiResponse<object>.Ok(new { }, $"Test email sent to {dto.TestEmail}."))
            : BadRequest(ApiResponse<object>.Fail("Could not send the test email. Check the settings above and the API logs."));
    }
}
