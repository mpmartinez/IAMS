namespace AssetDesk.Api.Entities;

/// <summary>
/// What actually arrived against a purchase order, on one occasion. The exchange rate lives
/// here rather than on the order because an invoice states the rate it was booked at and the
/// invoice arrives with the goods - and because two deliveries months apart against one USD
/// order genuinely cost different pesos.
/// </summary>
public class GoodsReceipt : ITenantEntity
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public DateTime ReceiptDate { get; set; } = DateTime.UtcNow;

    /// <summary>Pesos per one unit of the order's currency, as booked. Exactly 1 for PHP.</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    public required string ReceivedByUserId { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GoodsReceiptLine> Lines { get; set; } = [];
}

public class GoodsReceiptLine
{
    public int Id { get; set; }

    public int GoodsReceiptId { get; set; }
    public GoodsReceipt? GoodsReceipt { get; set; }

    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    public int QuantityReceived { get; set; }
}
