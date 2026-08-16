namespace IAMS.Api.Entities;

public class TicketComment : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public string Body { get; set; } = "";

    /// <summary>Staff-only. Filtered out server-side before a requester ever sees the ticket.</summary>
    public bool IsInternal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
