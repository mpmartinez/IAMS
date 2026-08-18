namespace IAMS.Api.Entities;

/// <summary>
/// Platform-wide key/value configuration a SuperAdmin edits at runtime. Deliberately global:
/// no TenantId, no ITenantEntity, no query filter - the same way LookupValue is global.
///
/// This exists because the SMTP credentials that password-reset mail depends on are secrets
/// and differ per deployment, so they cannot be committed to appsettings.json. Before this,
/// Smtp:Host was blank in every checkout and forgot-password silently sent nothing.
///
/// Keys are namespaced by prefix so a whole group can be read in one query, e.g. "email:".
/// </summary>
public class SystemSetting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The "email:" settings group, read by SmtpEmailService and written by EmailSettingsController.</summary>
public static class SystemSettingKeys
{
    public const string EmailPrefix = "email:";

    public const string SmtpHost = "email:smtp_host";
    public const string SmtpPort = "email:smtp_port";
    public const string UseSsl = "email:use_ssl";
    public const string SenderEmail = "email:sender_email";
    public const string SenderName = "email:sender_name";
    public const string Username = "email:username";
    public const string Password = "email:password";
}
