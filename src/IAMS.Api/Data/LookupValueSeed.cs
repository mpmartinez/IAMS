using IAMS.Api.Entities;

namespace IAMS.Api.Data;

/// <summary>
/// The rows AppDbContext.OnModelCreating hands to LookupValue's HasData call. Mirrors every
/// value in DeviceTypes, Currencies, AttachmentCategories, TicketAttachmentCategories,
/// TicketCategory, TicketStatus, TicketTypes, AssetStatus and TicketPriority 1:1, so the
/// generated migration's day-one behaviour is identical to the hardcoded constants it replaces.
///
/// Ids and CreatedAt are fixed literals, not generated at runtime: HasData snapshots its values
/// into the migration at generation time, and a value that changed between runs (an identity
/// column, DateTime.UtcNow) would make `dotnet ef migrations add` see model drift on every
/// future migration.
/// </summary>
public static class LookupValueSeed
{
    private static readonly DateTime SeededAt = new(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);

    public static readonly LookupValue[] Rows =
    [
        // DeviceType (editable)
        Row(1, LookupTypes.DeviceType, DeviceTypes.Laptop, "Laptop", 0),
        Row(2, LookupTypes.DeviceType, DeviceTypes.Desktop, "Desktop", 1),
        Row(3, LookupTypes.DeviceType, DeviceTypes.Monitor, "Monitor", 2),
        Row(4, LookupTypes.DeviceType, DeviceTypes.Phone, "Phone", 3),
        Row(5, LookupTypes.DeviceType, DeviceTypes.Tablet, "Tablet", 4),
        Row(6, LookupTypes.DeviceType, DeviceTypes.Printer, "Printer", 5),
        Row(7, LookupTypes.DeviceType, DeviceTypes.Network, "Network", 6),
        Row(8, LookupTypes.DeviceType, DeviceTypes.Server, "Server", 7),
        Row(9, LookupTypes.DeviceType, DeviceTypes.Peripheral, "Peripheral", 8),
        Row(10, LookupTypes.DeviceType, DeviceTypes.Software, "Software", 9),
        Row(11, LookupTypes.DeviceType, DeviceTypes.Other, "Other", 10),

        // Currency (editable)
        Row(12, LookupTypes.Currency, Currencies.USD, "USD", 0),
        Row(13, LookupTypes.Currency, Currencies.EUR, "EUR", 1),
        Row(14, LookupTypes.Currency, Currencies.GBP, "GBP", 2),
        Row(15, LookupTypes.Currency, Currencies.PHP, "PHP", 3),
        Row(16, LookupTypes.Currency, Currencies.JPY, "JPY", 4),
        Row(17, LookupTypes.Currency, Currencies.CAD, "CAD", 5),
        Row(18, LookupTypes.Currency, Currencies.AUD, "AUD", 6),

        // AttachmentCategory (editable)
        Row(19, LookupTypes.AttachmentCategory, AttachmentCategories.Receipt, "Receipt", 0),
        Row(20, LookupTypes.AttachmentCategory, AttachmentCategories.Photo, "Photo", 1),
        Row(21, LookupTypes.AttachmentCategory, AttachmentCategories.WarrantyDocument, "Warranty Document", 2),
        Row(22, LookupTypes.AttachmentCategory, AttachmentCategories.Manual, "Manual", 3),
        Row(23, LookupTypes.AttachmentCategory, AttachmentCategories.Other, "Other", 4),

        // TicketAttachmentCategory (editable)
        Row(24, LookupTypes.TicketAttachmentCategory, TicketAttachmentCategories.BeforePhoto, "Before Photo", 0),
        Row(25, LookupTypes.TicketAttachmentCategory, TicketAttachmentCategories.AfterPhoto, "After Photo", 1),
        Row(26, LookupTypes.TicketAttachmentCategory, TicketAttachmentCategories.Receipt, "Receipt", 2),
        Row(27, LookupTypes.TicketAttachmentCategory, TicketAttachmentCategories.Document, "Document", 3),
        Row(28, LookupTypes.TicketAttachmentCategory, TicketAttachmentCategories.Other, "Other", 4),

        // TicketCategory (editable)
        Row(29, LookupTypes.TicketCategory, TicketCategory.Hardware, "Hardware", 0),
        Row(30, LookupTypes.TicketCategory, TicketCategory.Software, "Software", 1),
        Row(31, LookupTypes.TicketCategory, TicketCategory.Access, "Access", 2),
        Row(32, LookupTypes.TicketCategory, TicketCategory.Network, "Network", 3),
        Row(33, LookupTypes.TicketCategory, TicketCategory.Security, "Security", 4),
        Row(34, LookupTypes.TicketCategory, TicketCategory.Other, "Other", 5),

        // TicketStatus (locked - read-only, see LookupTypes.LockedReason)
        Row(35, LookupTypes.TicketStatus, TicketStatus.New, "New", 0),
        Row(36, LookupTypes.TicketStatus, TicketStatus.Assigned, "Assigned", 1),
        Row(37, LookupTypes.TicketStatus, TicketStatus.InProgress, "In Progress", 2),
        Row(38, LookupTypes.TicketStatus, TicketStatus.OnHold, "On Hold", 3),
        Row(39, LookupTypes.TicketStatus, TicketStatus.Resolved, "Resolved", 4),
        Row(40, LookupTypes.TicketStatus, TicketStatus.Closed, "Closed", 5),
        Row(41, LookupTypes.TicketStatus, TicketStatus.Cancelled, "Cancelled", 6),

        // TicketType (locked)
        Row(42, LookupTypes.TicketType, TicketTypes.Incident, "Incident", 0),
        Row(43, LookupTypes.TicketType, TicketTypes.Request, "Request", 1),
        Row(44, LookupTypes.TicketType, TicketTypes.SecurityEvent, "Security Event", 2),

        // AssetStatus (locked)
        Row(45, LookupTypes.AssetStatus, AssetStatus.Available, "Available", 0),
        Row(46, LookupTypes.AssetStatus, AssetStatus.InUse, "In Use", 1),
        Row(47, LookupTypes.AssetStatus, AssetStatus.Maintenance, "Maintenance", 2),
        Row(48, LookupTypes.AssetStatus, AssetStatus.Retired, "Retired", 3),
        Row(49, LookupTypes.AssetStatus, AssetStatus.Lost, "Lost", 4),

        // TicketPriority (locked)
        Row(50, LookupTypes.TicketPriority, TicketPriority.Low, "Low", 0),
        Row(51, LookupTypes.TicketPriority, TicketPriority.Medium, "Medium", 1),
        Row(52, LookupTypes.TicketPriority, TicketPriority.High, "High", 2),
        Row(53, LookupTypes.TicketPriority, TicketPriority.Critical, "Critical", 3),
    ];

    private static LookupValue Row(int id, string lookupType, string value, string label, int sortOrder) => new()
    {
        Id = id,
        LookupType = lookupType,
        Value = value,
        Label = label,
        SortOrder = sortOrder,
        IsActive = true,
        CreatedAt = SeededAt
    };
}
