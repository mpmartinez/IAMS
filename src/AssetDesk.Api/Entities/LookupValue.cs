namespace AssetDesk.Api.Entities;

/// <summary>
/// A single admin-editable reference-data value (e.g. one device type, one currency).
/// Deliberately NOT an <see cref="ITenantEntity"/> and carries no query filter: the owner
/// wants one shared vocabulary across every tenant, managed centrally by a SuperAdmin - see
/// LookupsController.
///
/// Rows are never deleted, only deactivated. Assets, tickets and attachments store the raw
/// <see cref="Value"/> string on their own row (e.g. Asset.DeviceType), so removing a row
/// out from under existing data would orphan it. <see cref="IsActive"/> = false hides a value
/// from new records while leaving history intact.
/// </summary>
public class LookupValue
{
    public int Id { get; set; }

    /// <summary>Which vocabulary this value belongs to - see <see cref="LookupTypes"/>.</summary>
    public required string LookupType { get; set; }

    /// <summary>
    /// The stable code stored on the owning record (e.g. Asset.DeviceType). Immutable after
    /// creation: changing it would silently disagree with every row that already stored the
    /// old value. Only <see cref="Label"/> may be edited.
    /// </summary>
    public required string Value { get; set; }

    /// <summary>The human-readable text shown in the UI. Freely editable.</summary>
    public required string Label { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// The catalogue of lookup vocabularies. Split into two groups:
///
/// <see cref="Editable"/> types are pure labels - only ever displayed or filtered on - so an
/// admin can freely add, rename or deactivate values through LookupsController.
///
/// <see cref="Locked"/> types are branched on by exact string value elsewhere in this codebase
/// (workflow transition tables, fulfilment gates, priority ranking). They are still seeded into
/// the LookupValue table and readable through the same endpoint - so an admin can see the whole
/// vocabulary in one place - but LookupsController rejects any write against them, server-side,
/// regardless of what a client sends.
/// </summary>
public static class LookupTypes
{
    public const string DeviceType = "DeviceType";
    public const string Currency = "Currency";
    public const string AttachmentCategory = "AttachmentCategory";
    public const string TicketAttachmentCategory = "TicketAttachmentCategory";
    public const string TicketCategory = "TicketCategory";

    public const string TicketStatus = "TicketStatus";
    public const string TicketType = "TicketType";
    public const string AssetStatus = "AssetStatus";
    public const string TicketPriority = "TicketPriority";

    public static readonly string[] Editable =
        [DeviceType, Currency, AttachmentCategory, TicketAttachmentCategory, TicketCategory];

    public static readonly string[] Locked =
        [TicketStatus, TicketType, AssetStatus, TicketPriority];

    public static readonly string[] All = [.. Editable, .. Locked];

    public static bool IsValidType(string type) => All.Contains(type);

    public static bool IsEditable(string type) => Editable.Contains(type);

    public static string DisplayName(string type) => type switch
    {
        DeviceType => "Device Types",
        Currency => "Currencies",
        AttachmentCategory => "Attachment Categories",
        TicketAttachmentCategory => "Ticket Attachment Categories",
        TicketCategory => "Ticket Categories",
        TicketStatus => "Ticket Statuses",
        TicketType => "Ticket Types",
        AssetStatus => "Asset Statuses",
        TicketPriority => "Ticket Priorities",
        _ => type
    };

    /// <summary>Null for an editable type. Explains, for the admin screen, why a locked type
    /// cannot be edited here.</summary>
    public static string? LockedReason(string type) => type switch
    {
        TicketStatus =>
            "TicketWorkflow.CanTransition hardcodes which statuses a ticket may move between. " +
            "Adding or renaming a status here would not teach the workflow engine about it.",
        TicketType =>
            "\"Request\" triggers asset fulfilment and \"SecurityEvent\" forces a priority floor - " +
            "code branches on these exact values.",
        AssetStatus =>
            "\"Available\" gates fulfilment, \"Maintenance\" drives restore-on-resolve, and " +
            "\"Lost\" is set by return processing - code branches on these exact values.",
        TicketPriority =>
            "\"High\" is the security-event priority floor and the values are rank-ordered - " +
            "code branches on these exact values.",
        _ => null
    };

    /// <summary>
    /// The compile-time values for a lookup type, used both to seed the migration and as the
    /// validation fallback when the LookupValue table has no rows for a type (an unseeded
    /// database, or a test database created with EnsureCreated instead of migrations) - see
    /// LookupService.IsActiveValueAsync.
    /// </summary>
    public static string[] FallbackValues(string type) => type switch
    {
        // Fully qualified: inside this class, the unqualified name TicketCategory /
        // TicketStatus / TicketPriority / AssetStatus would resolve to the const string
        // field declared above with that same identifier, not the sibling constant class -
        // C# simple-name lookup prefers the member. global:: sidesteps the shadowing.
        DeviceType => DeviceTypes.All,
        Currency => Currencies.All,
        AttachmentCategory => AttachmentCategories.All,
        TicketAttachmentCategory => TicketAttachmentCategories.All,
        TicketCategory => global::AssetDesk.Api.Entities.TicketCategory.All,
        TicketStatus => global::AssetDesk.Api.Entities.TicketStatus.All,
        TicketType => TicketTypes.All,
        AssetStatus => global::AssetDesk.Api.Entities.AssetStatus.All,
        TicketPriority => global::AssetDesk.Api.Entities.TicketPriority.All,
        _ => []
    };
}
