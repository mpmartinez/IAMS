namespace AssetDesk.Web.Components;

/// <summary>
/// Values every page that uploads a ticket attachment needs. One definition so the three upload
/// sites (Report, the New Ticket dialog, the ticket detail page) cannot disagree.
/// </summary>
public static class TicketAttachmentDefaults
{
    /// Mirrors AssetDesk.Api.Entities.TicketAttachmentCategories.Other. The API requires a category and
    /// the picker does not expose the lookup, so every upload from the Web client sends this one.
    public const string Category = "Other";
}
