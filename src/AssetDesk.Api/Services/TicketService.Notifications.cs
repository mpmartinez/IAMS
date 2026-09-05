using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Services;

public partial class TicketService
{
    /// <summary>
    /// How much of a free-text note (a resolution, a comment body) a notification carries.
    /// The bell shows two clamped lines, so anything longer is never read there - the link
    /// to the ticket is what the rest of the text is for.
    /// </summary>
    private const int NotificationExcerptLength = 140;

    /// <summary>Tells the person who filed a ticket that something happened on it.</summary>
    private Task NotifyRequesterAsync(
        Ticket ticket, string? actorUserId, string title, string message, string type)
        => NotifyAboutTicketAsync(ticket.RequesterUserId, ticket, actorUserId, title, message, type);

    /// <summary>
    /// Tells whoever is currently holding the ticket. A no-op while it is unassigned, which is
    /// why callers need not check first.
    /// </summary>
    private Task NotifyAssigneeAsync(
        Ticket ticket, string? actorUserId, string title, string message, string type)
        => NotifyAboutTicketAsync(ticket.AssignedToUserId, ticket, actorUserId, title, message, type);

    /// <summary>
    /// Tells one person about something that happened on a ticket.
    ///
    /// Best-effort by design, and deliberately never throws: by the time this runs the caller
    /// has already committed the work the notification is about, so a failure here must not
    /// turn a successful resolve into a 500 and invite the operator to resolve the ticket
    /// twice. A dropped bell entry is recoverable - the ticket itself shows the same state.
    ///
    /// Callers pass the actor so nobody is told about their own action: staff who file and
    /// then fix their own ticket would otherwise notify themselves.
    /// </summary>
    private async Task NotifyAboutTicketAsync(
        string? userId, Ticket ticket, string? actorUserId, string title, string message, string type)
    {
        if (_notifications is null) return;
        if (string.IsNullOrEmpty(userId)) return;
        if (actorUserId is not null && actorUserId == userId) return;

        try
        {
            await _notifications.CreateNotificationAsync(new CreateNotificationDto
            {
                // Explicit, not left to AppDbContext's ambient stamp: the notification is
                // written through a fresh DI scope, and a tenant read from the JWT claim is
                // not something this method can assume is there.
                TenantId = ticket.TenantId,
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                Link = $"/tickets/{ticket.Id}",
                RelatedEntityType = nameof(Ticket),
                RelatedEntityId = ticket.Id
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not notify {UserId} about ticket {TicketId}; the ticket change itself stands.",
                userId, ticket.Id);
        }
    }

    /// <summary>The display reference, matching TicketDto.Reference.</summary>
    private static string Reference(Ticket ticket) => $"TKT-{ticket.TicketNumber:D4}";

    private static string Excerpt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= NotificationExcerptLength
            ? collapsed
            : collapsed[..NotificationExcerptLength].TrimEnd() + "…";
    }
}
