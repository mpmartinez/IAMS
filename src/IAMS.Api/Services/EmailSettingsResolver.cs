using IAMS.Api.Entities;

namespace IAMS.Api.Services;

/// <summary>Where the effective SMTP configuration came from.</summary>
public enum EmailSettingsSource
{
    /// <summary>Nothing is configured anywhere - mail cannot send.</summary>
    None,

    /// <summary>From Smtp:* in configuration, i.e. the Smtp__* environment variables in a
    /// container deployment. Nothing has been saved in the database to override it.</summary>
    Configuration,

    /// <summary>From the SystemSettings table, saved by a SuperAdmin on the admin screen.</summary>
    Database
}

public record ResolvedEmailSettings
{
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? SenderEmail { get; init; }
    public string? SenderName { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public EmailSettingsSource Source { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// The single place that decides which SMTP settings win. Both the mail sender and the admin
/// screen go through it, deliberately: when they each resolved their own, the admin screen read
/// only the database and so reported "not configured" on a deployment whose mail was working
/// fine off Smtp__* environment variables - the screen contradicted reality while someone was
/// using it to debug exactly that.
/// </summary>
public interface IEmailSettingsResolver
{
    Task<ResolvedEmailSettings> ResolveAsync(CancellationToken ct = default);
}

public class EmailSettingsResolver(ISystemSettingService settings, IConfiguration config) : IEmailSettingsResolver
{
    public async Task<ResolvedEmailSettings> ResolveAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetByPrefixAsync(SystemSettingKeys.EmailPrefix, ct);

        // A blank stored value falls through to configuration rather than overriding it with
        // nothing - an admin clearing a field should not silently break a configured server.
        bool FromDb(string key, out string value)
        {
            value = "";
            if (!stored.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                return false;

            value = v;
            return true;
        }

        // Host is the deciding field: it is what makes mail send at all, so whichever layer
        // supplied it is the layer this deployment is really running on.
        var hostFromDb = FromDb(SystemSettingKeys.SmtpHost, out var dbHost);
        var host = hostFromDb ? dbHost : config["Smtp:Host"];

        var source = string.IsNullOrWhiteSpace(host)
            ? EmailSettingsSource.None
            : hostFromDb
                ? EmailSettingsSource.Database
                : EmailSettingsSource.Configuration;

        // Resolution is atomic once the host comes from configuration. Per-field fallback would
        // pair that host with whatever the database still holds - clear only the host on a
        // fully-populated row and you get the environment's server addressed on the database's
        // port, under the database's credentials, which matches neither server. When the
        // database supplies the host it stays authoritative, and configuration only fills in
        // fields it left blank.
        string? Resolve(string settingKey, string configKey) =>
            source == EmailSettingsSource.Configuration
                ? config[configKey]
                : FromDb(settingKey, out var v) ? v : config[configKey];

        return new ResolvedEmailSettings
        {
            Host = host,
            Port = int.TryParse(Resolve(SystemSettingKeys.SmtpPort, "Smtp:Port"), out var port) ? port : 587,
            UseSsl = !bool.TryParse(Resolve(SystemSettingKeys.UseSsl, "Smtp:UseSsl"), out var ssl) || ssl,
            SenderEmail = Resolve(SystemSettingKeys.SenderEmail, "Smtp:FromEmail"),
            SenderName = Resolve(SystemSettingKeys.SenderName, "Smtp:FromName"),
            Username = Resolve(SystemSettingKeys.Username, "Smtp:Username"),
            Password = Resolve(SystemSettingKeys.Password, "Smtp:Password"),
            Source = source
        };
    }
}
