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

    /// <summary>
    /// Identifies the one call to <c>GoodsReceiptService.ReceiveAsync</c> that produced this
    /// receipt, so a replay of that call can recognise its own committed work instead of
    /// recording the delivery a second time.
    ///
    /// The transaction runs under the provider's execution strategy, which replays the whole
    /// delegate on a transient failure. A commit that lands on the server but whose
    /// acknowledgement never arrives looks exactly like a failure, so the delegate runs again -
    /// and because the quantity claim is a *relative* increment, the second pass happily claims
    /// the same units once more. One physical delivery of 5 becomes 10 received, 10 assets and
    /// two receipts. The value is generated once per call, outside the retryable delegate (a
    /// value invented inside it would be new on every pass and prove nothing), and the strategy's
    /// verifySucceeded looks it up to decide whether the work already landed.
    ///
    /// Unique, so even if that check were somehow skipped the database still refuses the
    /// duplicate rather than quietly accepting it.
    /// </summary>
    public Guid RequestId { get; set; }

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
