namespace AssetDesk.Api.Entities;

/// <summary>
/// A unit of IT work: an incident, an equipment request, or a security event report.
/// Generalises the former Maintenance entity.
/// </summary>
public class Ticket : ITenantEntity
{
    public int Id { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Per-tenant display number, rendered as TKT-0183. Distinct from Id, which is global.</summary>
    public int TicketNumber { get; set; }

    public string Type { get; set; } = TicketTypes.Incident;

    /// <summary>What the ticket is about (Hardware, Software, Access, ...), orthogonal to Type.</summary>
    public string Category { get; set; } = TicketCategory.Other;

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = TicketStatus.New;
    public string Priority { get; set; } = TicketPriority.Medium;

    /// <summary>Optional: a Request exists before the asset that will fulfil it.</summary>
    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public string RequesterUserId { get; set; } = "";
    public ApplicationUser? RequesterUser { get; set; }
    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Phase 3 (SLA). Present now so the columns exist before there is history to lose.
    public DateTime? DueAt { get; set; }
    public DateTime? BreachedAt { get; set; }

    public string? Resolution { get; set; }

    /// <summary>Set when a Request is fulfilled, linking the ticket to the assignment it produced.</summary>
    public int? AssetAssignmentId { get; set; }
    public AssetAssignment? AssetAssignment { get; set; }

    public ICollection<TicketAttachment> Attachments { get; set; } = [];
    public ICollection<TicketComment> Comments { get; set; } = [];
}
