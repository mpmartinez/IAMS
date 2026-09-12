using System.ComponentModel.DataAnnotations;

namespace AssetDesk.Shared.DTOs;

public record SupplierDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
}

public record UpsertSupplierDto
{
    [Required, StringLength(200)]
    public required string Name { get; init; }

    [StringLength(200)] public string? ContactName { get; init; }
    [EmailAddress, StringLength(256)] public string? Email { get; init; }
    [StringLength(50)] public string? Phone { get; init; }
    [StringLength(500)] public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; } = true;
}

public record PurchaseOrderLineInputDto
{
    [Required, StringLength(50)]
    public required string DeviceType { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    [Range(1, 100000, ErrorMessage = "Quantity must be at least 1")]
    public int Quantity { get; init; }

    [Range(0, 100000000, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; init; }
}

public record CreatePurchaseOrderDto
{
    public int SupplierId { get; init; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = "PHP";

    public DateTime OrderDate { get; init; } = DateTime.UtcNow;
    public DateTime? ExpectedDate { get; init; }
    public string? Notes { get; init; }

    public List<PurchaseOrderLineInputDto> Lines { get; init; } = [];
}

public record PurchaseOrderLineDto
{
    public int Id { get; init; }
    public required string DeviceType { get; init; }
    public string? Description { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public int ReceivedQuantity { get; init; }
    public int OutstandingQuantity { get; init; }
    public decimal LineTotal { get; init; }
}

public record PurchaseOrderDto
{
    public int Id { get; init; }
    public int PoNumber { get; init; }

    /// <summary>Rendered PO-0042 in the UI; the raw number is kept so callers can sort on it.</summary>
    public string Reference { get; init; } = "";

    public int SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public DateTime OrderDate { get; init; }
    public DateTime? ExpectedDate { get; init; }
    public string? Notes { get; init; }
    public decimal OrderTotal { get; init; }
    public List<PurchaseOrderLineDto> Lines { get; init; } = [];
}

public record ReceiveLineDto
{
    public int PurchaseOrderLineId { get; init; }

    [Range(1, 100000, ErrorMessage = "Quantity received must be at least 1")]
    public int QuantityReceived { get; init; }
}

public record ReceiveGoodsDto
{
    public DateTime ReceiptDate { get; init; } = DateTime.UtcNow;

    [Range(0.000001, 1000000, ErrorMessage = "Exchange rate must be greater than zero")]
    public decimal ExchangeRate { get; init; } = 1m;

    public string? Notes { get; init; }
    public List<ReceiveLineDto> Lines { get; init; } = [];
}
