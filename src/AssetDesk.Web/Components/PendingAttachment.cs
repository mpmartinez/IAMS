namespace AssetDesk.Web.Components;

/// <summary>
/// A file chosen in the browser but not yet uploaded. Attachments are keyed by ticket id, so a
/// page that creates a ticket has to hold its files here until the ticket exists.
/// Extracted from Pages/Tickets/Report.razor, where it was a private nested PendingFile.
/// </summary>
public sealed class PendingAttachment
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Data { get; init; }

    /// A data: URL for images, used for the thumbnail. Null for non-images.
    public string? PreviewUrl { get; init; }
}
