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
