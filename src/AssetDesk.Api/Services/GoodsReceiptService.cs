using System.Data.Common;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Services;

public interface IGoodsReceiptService
{
    /// <summary>
    /// The tenant is a parameter rather than an ambient value from ITenantProvider so the
    /// contract is unambiguous at every call site and cannot be defeated by a provider that has
    /// no tenant selected.
    /// </summary>
    Task<ServiceResult<int>> ReceiveAsync(
        Guid tenantId, int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId,
        CancellationToken ct = default);
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
    ///
    /// Clearing the tracker is not enough on its own. It makes a replay independent of the
    /// previous attempt's tracked state, not of what that attempt committed - and a commit whose
    /// acknowledgement is lost looks, from here, exactly like a commit that never happened. So
    /// each call also carries an idempotency marker (<see cref="GoodsReceipt.RequestId"/>),
    /// minted once before the strategy is entered, and the strategy is given a verifySucceeded
    /// that looks that marker up before replaying anything.
    /// </summary>
    public async Task<ServiceResult<int>> ReceiveAsync(
        Guid tenantId, int purchaseOrderId, ReceiveGoodsDto dto, string actingUserId,
        CancellationToken ct = default)
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

        // Generated HERE, once, and deliberately not inside the delegate below: this is what
        // makes a replay recognisable as a replay. ChangeTracker.Clear() makes an attempt
        // independent of the previous attempt's *tracked* state, but it cannot make it
        // independent of what that attempt *committed* - and a commit whose acknowledgement is
        // lost to a dropped connection is indistinguishable from a commit that never happened.
        // The delegate stamps this on the receipt it creates; verifySucceeded below looks it up.
        // A value invented inside the delegate would be a fresh one on every pass and would
        // match nothing, which is the same as having no marker at all.
        var requestId = Guid.NewGuid();

        // The verifySucceeded overload, not the bare one. Without it the strategy's only move on
        // a transient failure is to run the delegate again; with it, it first asks whether the
        // attempt that appeared to fail actually landed, and stops if it did.
        var result = await strategy.ExecuteAsync(
            requestId,
            // The delegate's own DbContext and cancellation token are the ones already closed
            // over, so they are discarded here rather than shadowing them under new names.
            async (_, _, _) =>
        {
            // A retry re-runs this delegate on the same DbContext, which still tracks the failed
            // attempt's mutations. Starting from a cleared tracker is what makes each attempt
            // independent: every entity below is loaded fresh and every guard re-evaluated.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // The tenant predicate is explicit rather than left to the global query filter,
                // which has an IsSuperAdmin() bypass and would admit every organisation's orders
                // to a super-admin caller. It comes from the caller's parameter, so the
                // guarantee holds wherever this is called from rather than only where a
                // controller happened to check first.
                var order = await db.PurchaseOrders
                    .Include(p => p.Lines)
                    .FirstOrDefaultAsync(p => p.Id == purchaseOrderId && p.TenantId == tenantId, ct);

                if (order is null)
                    return ServiceResult<int>.Fail("Purchase order not found.");

                if (!PurchaseOrderWorkflow.IsOpen(order.Status))
                    return ServiceResult<int>.Fail(
                        $"A {order.Status} purchase order cannot receive goods.");

                // The authoritative check: CurrencyRules.Validate is the single home for the
                // rules binding a currency to its exchange rate (AssetsController and
                // AssetImportService both call it too), and this is the first point in this
                // method the order's currency is known - order is read inside the delegate, so
                // there is nothing to validate against before this. The dto.ExchangeRate <= 0m
                // check above is only a fast, friendly pre-check for the common typo; it cannot
                // catch a PHP order at a non-1 rate or a foreign-currency order left at 1, which
                // is exactly what this call exists to refuse. Every asset this receipt creates
                // takes Currency from the order and ExchangeRate from the dto, so a bad pair here
                // is a bad pair on every asset created below - refuse before anything is written.
                if (CurrencyRules.Validate(order.Currency, dto.ExchangeRate) is { } currencyError)
                    return ServiceResult<int>.Fail(currencyError);

                // Take the ORDER's row lock before claiming any line. The lock is the point; the
                // assignment is only how you get it - an UPDATE takes the row's write lock and
                // holds it until this transaction ends.
                //
                // Without it, two receipts against *different* lines of one order never contend:
                // each line claim locks only its own line. Both then read the order's line
                // totals seeing only their own line advanced, both compute PartiallyReceived,
                // and both commit. An order that is fully delivered reads PartiallyReceived for
                // ever, and no later receipt can put it right, because the claim now refuses
                // every remaining quantity. Serialising receipts per order means the second one
                // reads totals that already include the first.
                //
                // First write in the transaction, deliberately: a single lock order (the order
                // row, then its lines) is what keeps two receipts from deadlocking against each
                // other by grabbing lines in different sequences.
                var locked = await db.PurchaseOrders
                    .Where(p => p.Id == order.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

                if (locked != 1)
                    return ServiceResult<int>.Fail("Purchase order not found.");

                var receipt = new GoodsReceipt
                {
                    TenantId = order.TenantId,
                    PurchaseOrderId = order.Id,
                    // Stamped from the value generated once outside the strategy, so a replay of
                    // this delegate writes the same marker - and verifySucceeded can find it.
                    RequestId = requestId,
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

                    // The over-receipt invariant, as a conditional claim rather than a re-read.
                    // Reading ReceivedQuantity, comparing it in memory and then writing back an
                    // incremented absolute value is a lost update at READ COMMITTED: two
                    // overlapping receipts both read 0, both agree that 8 of 10 is fine, and
                    // both write 8 - over-receiving, with assets created for units nobody
                    // ordered. PurchaseOrderLine carries no concurrency token, so nothing else
                    // would catch it.
                    //
                    // This UPDATE ... WHERE ReceivedQuantity + n <= Quantity is the invariant:
                    // the predicate is re-evaluated against the row at write time under that
                    // row's lock, and the increment is relative, so a concurrent receipt either
                    // blocks here and then matches nothing, or has already landed in the value
                    // being added to. Same shape as the conditional claims in
                    // TicketService.FulfilAsync.
                    var claimed = await db.PurchaseOrderLines
                        .Where(l => l.Id == line.Id
                                 && l.ReceivedQuantity + incoming.QuantityReceived <= l.Quantity)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(
                                l => l.ReceivedQuantity,
                                l => l.ReceivedQuantity + incoming.QuantityReceived), ct);

                    if (claimed != 1)
                    {
                        // There is deliberately no in-memory pre-check ahead of this. One would
                        // only restate the condition, and it is not what holds; leaving the
                        // claim as the single refusal path means every refused over-receipt -
                        // in production and in the tests - provably came from the conditional
                        // UPDATE. The quantities below are read off the tracked line to give a
                        // human a reason. Under a genuine race they may be a moment stale,
                        // which is fine for prose and is not what the decision rested on.
                        logger.LogWarning(
                            "Receiving {Quantity} against purchase order line {LineId} was refused: " +
                            "the claim would have taken the line past its ordered quantity.",
                            incoming.QuantityReceived, line.Id);

                        return ServiceResult<int>.Fail(
                            $"{line.DeviceType}: {line.Quantity - line.ReceivedQuantity} of {line.Quantity} outstanding, " +
                            $"cannot receive {incoming.QuantityReceived}.");
                    }

                    receipt.Lines.Add(new GoodsReceiptLine
                    {
                        PurchaseOrderLineId = line.Id,
                        QuantityReceived = incoming.QuantityReceived
                    });
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

                // The claims above went round the change tracker, so order.Lines still holds the
                // pre-claim counts and summing those here would derive the wrong status. Read
                // the totals back instead: this query runs inside the same transaction, so it
                // sees its own writes. Assigning the new values onto the tracked lines would
                // work too, but it would mark them Modified and make EF re-issue an UPDATE for a
                // row this transaction has already written - noise, and it would quietly put an
                // absolute write back next to the relative one that is the invariant.
                // PurchaseOrderLine is not in AuditSaveChangesInterceptor.AuditedTypes, so
                // unlike FulfilAsync there is no audit trail depending on the tracked entity.
                var totals = await db.PurchaseOrderLines
                    .Where(l => l.PurchaseOrderId == order.Id)
                    .Select(l => new { l.Quantity, l.ReceivedQuantity })
                    .ToListAsync(ct);

                var totalOrdered = totals.Sum(t => t.Quantity);
                var totalReceived = totals.Sum(t => t.ReceivedQuantity);

                order.Status = PurchaseOrderWorkflow.StatusFor(totalOrdered, totalReceived, order.Status);
                order.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                receiptId = receipt.Id;

                logger.LogInformation(
                    "Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s)",
                    receipt.Lines.Count, order.PoNumber, receipt.Lines.Sum(l => l.QuantityReceived));

                // The committed ReceivedQuantity values are still not the ones this context has
                // tracked. Anything reading the order afterwards on the same scoped DbContext -
                // the controller re-reads it through GetById to build its response - gets the
                // tracked instances back by identity resolution and would report the counts as
                // they were before the delivery. Detaching is the cheap, honest fix: the data is
                // committed, so the next read comes from the database.
                db.ChangeTracker.Clear();

                return ServiceResult<int>.Ok(receiptId);
            }
            // Only a durable failure becomes a failure result. A transient one (a database
            // restart, a dropped connection) is precisely what the execution strategy exists to
            // replay, so it must propagate and reach the strategy rather than be reported to the
            // caller as a rejected delivery. Neither path rolls back by hand: the `await using`
            // above rolls the transaction back as it is disposed, so this is about the response
            // the caller gets, not about data integrity.
            catch (DbUpdateException ex) when (!IsTransient(ex))
            {
                // The transaction is rolled back by the `await using` above, but the change
                // tracker knows nothing about that: it still holds every Asset as Added and the
                // GoodsReceipt as Unchanged carrying the id of a row that no longer exists. This
                // is a scoped DbContext, so a later SaveChangesAsync on the same request would
                // insert those assets pointing at a receipt line that was rolled away - and
                // Asset IS in AuditSaveChangesInterceptor.AuditedTypes, so it would mint audit
                // rows swearing to it. No current caller saves again in this scope; clearing
                // here is what keeps that true of future ones. FulfilAsync clears in exactly
                // this branch for exactly this reason.
                db.ChangeTracker.Clear();

                // The exception text names constraints, columns and tables; it belongs in the
                // log, not in a message handed back to a caller.
                logger.LogError(ex,
                    "Receiving against purchase order {PurchaseOrderId} failed and was rolled back.",
                    purchaseOrderId);

                return ServiceResult<int>.Fail("Could not record the delivery. Please try again.");
            }
        },
            // Asked by the strategy after a failure it would otherwise retry, to establish
            // whether the attempt actually landed. A receipt carrying this call's marker can
            // only have been written by this call, so finding one means the delivery is already
            // recorded and replaying would record it twice.
            //
            // Untracked and unfiltered on purpose: tracked state would be whatever the failed
            // attempt left behind rather than what the database holds, and the tenant filter
            // could hide the row (a super admin with no tenant selected, say) and turn "already
            // committed" into "retry" - the exact double-receipt this exists to prevent. The
            // marker is a Guid this method minted moments ago, so reading past the filter can
            // reach nothing but its own work.
            async (_, marker, verifyCt) =>
            {
                db.ChangeTracker.Clear();

                var committed = await db.GoodsReceipts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(r => r.RequestId == marker)
                    .Select(r => (int?)r.Id)
                    .FirstOrDefaultAsync(verifyCt);

                if (committed is not { } id)
                    return new ExecutionResult<ServiceResult<int>>(false, default!);

                logger.LogWarning(
                    "Receiving against purchase order {PurchaseOrderId} was replayed, but receipt " +
                    "{ReceiptId} for request {RequestId} had already committed; the delivery was " +
                    "not recorded a second time.",
                    purchaseOrderId, id, marker);

                receiptId = id;
                return new ExecutionResult<ServiceResult<int>>(true, ServiceResult<int>.Ok(id));
            },
            ct);

        return result;
    }

    /// <summary>
    /// Walks the inner-exception chain for a provider exception the driver itself classes as
    /// transient (Npgsql sets this for connection and timeout failures). The same test
    /// TicketService applies to its own transaction; kept private here rather than shared so
    /// this fix stays inside the operation it is fixing.
    /// </summary>
    private static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { IsTransient: true })
                return true;
        }

        return false;
    }
}
