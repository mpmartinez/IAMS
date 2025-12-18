namespace IAMS.Api.Entities;

/// <summary>
/// Tracks warranty expiration alerts for assets
/// </summary>
public class WarrantyAlert
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>
    /// Type of alert: Expiring (within 90 days) or Expired
    /// </summary>
    public required string AlertType { get; set; }

    /// <summary>
    /// Warranty end date at the time the alert was created
    /// </summary>
    public DateTime WarrantyEndDate { get; set; }

    /// <summary>
    /// Days remaining when alert was created (negative if expired)
    /// </summary>
    public int DaysRemaining { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert was acknowledged by a user
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// User who acknowledged the alert
    /// </summary>
    public string? AcknowledgedByUserId { get; set; }
    public ApplicationUser? AcknowledgedByUser { get; set; }

    public bool IsAcknowledged => AcknowledgedAt.HasValue;
}

public static class WarrantyAlertTypes
{
    public const string Expiring = "Expiring";
    public const string Expired = "Expired";
}
