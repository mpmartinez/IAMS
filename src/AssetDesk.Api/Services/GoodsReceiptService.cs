using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Services;

public interface IGoodsReceiptService
{
    Task<ServiceResult<int>> ReceiveAsync(
        int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId, CancellationToken ct = default);
}

public class GoodsReceiptService(
    AppDbContext db,
    IAssetTagGenerator tags,
    ILogger<GoodsReceiptService> logger) : IGoodsReceiptService
{
    /// <summary>
    /// Records a delivery against a purchase order and creates one asset per unit received.
    ///
    /// Receipt creation, asset creation, the line's received count and the order's status are
    /// one transaction. A partial success would create assets the order does not know it
    /// produced, or advance a received count without the assets to match - either leaves the
    /// register lying, which is the one thing this system exists to prevent.
    ///
    /// The transaction runs through the provider's execution strategy because production is
    /// Npgsql with EnableRetryOnFailure and EF Core refuses a user-initiated transaction under a
    /// retrying strategy. That means the delegate can run more than once, so it clears the
    /// change tracker on entry and re-reads everything: a retry must not see the failed
    /// attempt's mutations.
    /// </summary>
    public async Task<ServiceResult<int>> ReceiveAsync(
        int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId, CancellationToken ct = default)
    {
        if (dto.Lines.Count == 0)
            return ServiceResult<int>.Fail("Nothing was received.");

        if (dto.ExchangeRate <= 0m)
            return ServiceResult<int>.Fail("Exchange rate must be greater than zero.");

        if (dto.Lines.Any(l => l.QuantityReceived <= 0))
            return ServiceResult<int>.Fail("A received quantity must be at least 1.");

        if (dto.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != dto.Lines.Count)
            return ServiceResult<int>.Fail("The same line appears more than once.");

        var strategy = db.Database.CreateExecutionStrategy();

        // Captured by the successful attempt and used after the strategy is done. Nothing here
        // raises a notification, but if one is ever added it belongs out here: the delegate is
        // retryable, and a notification inside it would fire again on every replay.
        var receiptId = 0;

        var result = await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this delegate on the same DbContext, which still tracks the failed
            // attempt's mutations. Starting from a cleared tracker is what makes each attempt
            // independent: every entity below is loaded fresh and every guard re-evaluated.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var order = await db.PurchaseOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == purchaseOrderId, ct);

            if (order is null)
                return ServiceResult<int>.Fail("Purchase order not found.");

            if (!PurchaseOrderWorkflow.IsOpen(order.Status))
                return ServiceResult<int>.Fail(
                    $"A {order.Status} purchase order cannot receive goods.");

            var receipt = new GoodsReceipt
            {
                TenantId = order.TenantId,
                PurchaseOrderId = order.Id,
                ReceiptDate = dto.ReceiptDate,
                ExchangeRate = dto.ExchangeRate,
                ReceivedByUserId = actingUserId,
                Notes = dto.Notes
            };

            var tagCache = new Dictionary<string, int>();

            foreach (var incoming in dto.Lines)
            {
                var line = order.Lines.FirstOrDefault(l => l.Id == incoming.PurchaseOrderLineId);
                if (line is null)
                    return ServiceResult<int>.Fail("That line does not belong to this purchase order.");

                // The invariant, re-read inside the transaction. The caller may also have
                // pre-checked this for a friendlier message; that pre-check is not what holds.
                // Two concurrent receipts must not both claim the last unit.
                if (line.ReceivedQuantity + incoming.QuantityReceived > line.Quantity)
                    return ServiceResult<int>.Fail(
                        $"{line.DeviceType}: {line.Quantity - line.ReceivedQuantity} of {line.Quantity} outstanding, " +
                        $"cannot receive {incoming.QuantityReceived}.");

                var receiptLine = new GoodsReceiptLine
                {
                    PurchaseOrderLineId = line.Id,
                    QuantityReceived = incoming.QuantityReceived
                };
                receipt.Lines.Add(receiptLine);

                line.ReceivedQuantity += incoming.QuantityReceived;
            }

            db.GoodsReceipts.Add(receipt);

            // Saved before the assets so each receipt line has an id to point at.
            await db.SaveChangesAsync(ct);

            foreach (var receiptLine in receipt.Lines)
            {
                var line = order.Lines.First(l => l.Id == receiptLine.PurchaseOrderLineId);

                for (var i = 0; i < receiptLine.QuantityReceived; i++)
                {
                    db.Assets.Add(new Asset
                    {
                        TenantId = order.TenantId,
                        AssetTag = await tags.NextAsync(line.DeviceType, tagCache, ct),
                        DeviceType = line.DeviceType,
                        Name = line.Description,
                        Status = AssetStatus.Available,
                        PurchasePrice = line.UnitPrice,
                        Currency = order.Currency,
                        ExchangeRate = dto.ExchangeRate,
                        PurchaseDate = dto.ReceiptDate,
                        GoodsReceiptLineId = receiptLine.Id
                    });
                }
            }

            var totalOrdered = order.Lines.Sum(l => l.Quantity);
            var totalReceived = order.Lines.Sum(l => l.ReceivedQuantity);
            order.Status = PurchaseOrderWorkflow.StatusFor(totalOrdered, totalReceived, order.Status);
            order.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            receiptId = receipt.Id;

            logger.LogInformation(
                "Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s)",
                receipt.Lines.Count, order.PoNumber, receipt.Lines.Sum(l => l.QuantityReceived));

            return ServiceResult<int>.Ok(receipt.Id);
        });

        return result;
    }
}
