namespace IAMS.Shared.DTOs;

/// <summary>
/// The SMTP configuration as shown on the admin screen. Note the absence of Password: the
/// stored secret is never sent to a client, only the HasPassword flag that tells the UI
/// whether one is already on file.
/// </summary>
public record EmailSettingsDto
{
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string SenderEmail { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string Username { get; init; } = "";

    /// <summary>True when a password is stored, so the UI can offer "leave blank to keep".</summary>
    public bool HasPassword { get; init; }

    /// <summary>True once a host is set - i.e. password-reset mail can actually go out.</summary>
    public bool IsConfigured { get; init; }
}

public record UpdateEmailSettingsDto
{
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public bool UseSsl { get; init; } = true;
    public string? SenderEmail { get; init; }
    public string? SenderName { get; init; }
    public string? Username { get; init; }

    /// <summary>Leave null or empty to keep the password already stored.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Removes the stored password outright. Without this there is no way to take a credential
    /// back off the server - an empty Password means "keep the current one", so an admin moving
    /// to a relay that needs no auth could never drop the old provider's password. Ignored when
    /// Password is also supplied: setting a new one already replaces the old.
    /// </summary>
    public bool ClearPassword { get; init; }
}

public record TestEmailDto
{
    public required string TestEmail { get; init; }
}
