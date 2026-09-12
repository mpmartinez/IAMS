namespace AssetDesk.Api.Entities;

public class PurchaseOrder : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Per-tenant display number, rendered PO-0042. Distinct from Id, which is global.</summary>
    public int PoNumber { get; set; }

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>The currency the order is placed in. The rate lives on each receipt, not here.</summary>
    public string Currency { get; set; } = Currencies.PHP;

    public string Status { get; set; } = PurchaseOrderStatus.Draft;

    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public DateTime? ExpectedDate { get; set; }
    public string? Notes { get; set; }

    public required string CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public int Id { get; set; }

    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public required string DeviceType { get; set; }
    public string? Description { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Running total maintained by the receive operation, not derived by summing receipt lines.
    /// It is what the over-receipt guard reads inside the transaction, and a derived sum would
    /// have to be recomputed under the same lock to be safe.
    /// </summary>
    public int ReceivedQuantity { get; set; }

    public int OutstandingQuantity => Quantity - ReceivedQuantity;
}

public static class PurchaseOrderStatus
{
    public const string Draft = "Draft";
    public const string Ordered = "Ordered";
    public const string PartiallyReceived = "PartiallyReceived";
    public const string Received = "Received";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All = [Draft, Ordered, PartiallyReceived, Received, Cancelled];

    public static bool IsValid(string status) => All.Contains(status);
}

/// <summary>
/// The single source of truth for how a purchase order may move between statuses. Shaped after
/// TicketWorkflow, which is the house pattern.
///
/// PartiallyReceived and Received are produced by <see cref="StatusFor"/> from the quantities,
/// never set by hand: a status a user could set independently of what has arrived would
/// immediately disagree with it.
/// </summary>
public static class PurchaseOrderWorkflow
{
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [PurchaseOrderStatus.Draft] = [PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Cancelled],
        [PurchaseOrderStatus.Ordered] =
        [
            PurchaseOrderStatus.PartiallyReceived,
            PurchaseOrderStatus.Received,
            PurchaseOrderStatus.Cancelled
        ],
        [PurchaseOrderStatus.PartiallyReceived] =
        [
            PurchaseOrderStatus.Received,
            PurchaseOrderStatus.Cancelled
        ],
        [PurchaseOrderStatus.Received] = [],
        [PurchaseOrderStatus.Cancelled] = []
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>Goods can only be received against an order that has been placed and is not finished.</summary>
    public static bool IsOpen(string status) =>
        status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>
    /// The status the quantities imply. Draft, Received and Cancelled are all returned unchanged:
    /// Draft has not been ordered yet, so it is not quantity-driven either, and Received/Cancelled
    /// are terminal - receiving is refused against any of the three anyway, but this must not
    /// drag one forward or back if it is ever asked.
    /// </summary>
    public static string StatusFor(int totalOrdered, int totalReceived, string currentStatus)
    {
        if (currentStatus is PurchaseOrderStatus.Cancelled
            or PurchaseOrderStatus.Draft
            or PurchaseOrderStatus.Received)
            return currentStatus;

        if (totalReceived <= 0) return PurchaseOrderStatus.Ordered;
        return totalReceived >= totalOrdered
            ? PurchaseOrderStatus.Received
            : PurchaseOrderStatus.PartiallyReceived;
    }
}
