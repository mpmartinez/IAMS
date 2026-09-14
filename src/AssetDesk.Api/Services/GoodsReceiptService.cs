using System.Data.Common;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
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
    ISubscriptionService subscriptions,
    ILogger<GoodsReceiptService> logger) : IGoodsReceiptService
{
    /// <summary>
    /// Records a delivery against a purchase order. A hardware line creates one asset per unit
    /// received; a software line records one licence entitlement instead - seats added to, or a
    /// renewal of, the licence it names - and may create that licence or move its expiry.
    ///
    /// Receipt creation, asset and entitlement creation, any licence created or re-dated, the
    /// line's received count and the order's status are one transaction. A partial success would
    /// create assets or seats the order does not know it produced, or advance a received count
    /// without the assets or seats to match - either leaves the register lying, which is the one
    /// thing this system exists to prevent.
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
                // is exactly what this call exists to refuse. Every asset and entitlement this
                // receipt creates takes Currency from the order and ExchangeRate from the dto, so a
                // bad pair here is a bad pair on every one of them - refuse before anything is written.
                if (CurrencyRules.Validate(order.Currency, dto.ExchangeRate) is { } currencyError)
                    return ServiceResult<int>.Fail(currencyError);

                // Software lines are resolved to their licences here, before the order row is locked
                // and before anything is written, so every refusal returns with nothing to roll back.
                // A licence this receipt creates is only described at this point; it is added to the
                // context after the lines are claimed, inside the same transaction, so a later refusal
                // or failure cannot leave it behind.
                var existingLicenceFor = new Dictionary<int, SoftwareLicence>();
                var newLicenceNames = new HashSet<string>(StringComparer.Ordinal);
                var redatedLicenceIds = new HashSet<int>();

                foreach (var incoming in dto.Lines)
                {
                    var line = order.Lines.FirstOrDefault(l => l.Id == incoming.PurchaseOrderLineId);
                    if (line is null)
                        return ServiceResult<int>.Fail("That line does not belong to this purchase order.");

                    if (line.DeviceType != DeviceTypes.Software)
                    {
                        // A renewal or an expiry on a hardware line has nothing to act on. Accepting
                        // it quietly would tell the caller a licence term was recorded when none was.
                        if (incoming.SoftwareLicenceId is not null || incoming.NewLicence is not null
                            || incoming.LicenceMode != LicenceReceiptModes.AddSeats
                            || incoming.LicenceExpiresAt is not null)
                            return ServiceResult<int>.Fail(
                                $"{line.DeviceType} is not software, so it cannot be received into a licence.");
                        continue;
                    }

                    var label = line.Description ?? line.DeviceType;

                    if ((incoming.SoftwareLicenceId is null) == (incoming.NewLicence is null))
                        return ServiceResult<int>.Fail(
                            $"{label}: choose the licence these seats belong to, or name a new one.");

                    if (!LicenceReceiptModes.IsValid(incoming.LicenceMode))
                        return ServiceResult<int>.Fail(
                            $"{label}: choose whether this adds seats or renews the licence.");

                    if (incoming.LicenceMode == LicenceReceiptModes.Renew)
                    {
                        if (incoming.NewLicence is not null)
                            return ServiceResult<int>.Fail($"{label}: a renewal needs an existing licence.");
                        if (incoming.LicenceExpiresAt is null)
                            return ServiceResult<int>.Fail($"{label}: a renewal needs the new expiry date.");
                    }

                    if (incoming.LicenceExpiresAt is { } expiry && expiry.Date <= dto.ReceiptDate.Date)
                        return ServiceResult<int>.Fail($"{label}: the new expiry must be after the receipt date.");

                    if (incoming.NewLicence is { } described)
                    {
                        var name = described.Name?.Trim();
                        if (string.IsNullOrEmpty(name))
                            return ServiceResult<int>.Fail($"{label}: the new licence needs a name.");
                        if (!LicenceModels.IsValid(described.LicenceModel))
                            return ServiceResult<int>.Fail(
                                $"{label}: choose whether the new licence is counted per user or per device.");

                        // Explicit tenant predicate: the global filter has an IsSuperAdmin() bypass, and
                        // a name taken only in another organisation must not block this one.
                        if (!newLicenceNames.Add(name)
                            || await db.SoftwareLicences.AnyAsync(l => l.TenantId == tenantId && l.Name == name, ct))
                            return ServiceResult<int>.Fail($"A licence named '{name}' already exists.");
                        continue;
                    }

                    var licence = await db.SoftwareLicences.FirstOrDefaultAsync(
                        l => l.Id == incoming.SoftwareLicenceId && l.TenantId == tenantId, ct);
                    if (licence is null)
                        return ServiceResult<int>.Fail($"{label}: licence not found.");
                    if (!licence.IsActive)
                        return ServiceResult<int>.Fail($"{label}: licence '{licence.Name}' is deactivated.");

                    // One new expiry per licence per delivery. The checks below see each line alone,
                    // so two dated lines for one licence would both pass them; then the first line's
                    // move decides what the second's conditional update matches, and the outcome
                    // turns on line order - a later date second quietly wins, an earlier one is
                    // refused as though another delivery had got there first.
                    if (incoming.LicenceExpiresAt is not null && !redatedLicenceIds.Add(licence.Id))
                        return ServiceResult<int>.Fail(
                            $"{label}: '{licence.Name}' is re-dated on more than one line of this delivery. " +
                            "Put its new expiry on one line.");

                    // Only a renewal moves a term that is already set, and only forward. Seats bought
                    // part-way through a term take the term already running: an add-on quote's end
                    // date, or a receipt booked late after a newer renewal, would otherwise shorten a
                    // licence someone has just paid to extend. These read the licence before the
                    // order lock, so they give the reason; the conditional update after the claims
                    // is what holds.
                    if (incoming.LicenceExpiresAt is { } requested && licence.ExpiresAt is { } current)
                    {
                        if (incoming.LicenceMode == LicenceReceiptModes.AddSeats)
                            return ServiceResult<int>.Fail(
                                $"{label}: '{licence.Name}' already runs until {licence.ExpiresAt:MMM dd, yyyy}. " +
                                "Added seats take that term - use Renew to change it.");

                        if (requested <= current)
                            return ServiceResult<int>.Fail(
                                $"{label}: a renewal must run past the licence's current expiry of {licence.ExpiresAt:MMM dd, yyyy}.");
                    }

                    existingLicenceFor[line.Id] = licence;
                }

                // The asset limit, for every unit of a hardware line - a software line records an
                // entitlement, not assets. Checked here, inside the transaction and before
                // anything else is written, so a refusal returns with nothing to roll back but the
                // tenant lock itself. ReserveAssetCapacityAsync takes the TENANT row's lock and
                // holds it to commit, which serialises every receipt, import and create that adds
                // assets to this tenant; a check made before the strategy would let two receipts
                // against different orders both see the same free room. Every line has been
                // matched to the order above, so First cannot miss.
                var assetsToCreate = dto.Lines
                    .Where(l => order.Lines.First(o => o.Id == l.PurchaseOrderLineId).DeviceType != DeviceTypes.Software)
                    .Sum(l => l.QuantityReceived);

                if (assetsToCreate > 0
                    && await subscriptions.ReserveAssetCapacityAsync(db, tenantId, assetsToCreate, ct) is { } limitReached)
                    return ServiceResult<int>.Fail(limitReached);

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
                // First write in the transaction after the tenant lock above, deliberately: a single
                // lock order (the tenant row, then the order row, then its lines) is what keeps two
                // receipts from deadlocking against each other by grabbing rows in different
                // sequences. Nothing takes the tenant lock after an order or line lock.
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

                // Saved before the assets and entitlements so each receipt line has an id for them
                // to point at.
                await db.SaveChangesAsync(ct);

                foreach (var receiptLine in receipt.Lines)
                {
                    var line = order.Lines.First(l => l.Id == receiptLine.PurchaseOrderLineId);

                    if (line.DeviceType == DeviceTypes.Software)
                    {
                        var incoming = dto.Lines.First(l => l.PurchaseOrderLineId == line.Id);
                        var licence = existingLicenceFor.GetValueOrDefault(line.Id)
                            ?? AddLicence(order, incoming.NewLicence!);
                        var renewing = incoming.LicenceMode == LicenceReceiptModes.Renew;

                        db.LicenceEntitlements.Add(new LicenceEntitlement
                        {
                            TenantId = order.TenantId,
                            SoftwareLicence = licence,
                            // A renewal is paid for per seat but adds none: the same seats run for
                            // another term. Counting them again would report seats nobody owns.
                            SeatsAdded = renewing ? 0 : receiptLine.QuantityReceived,
                            Cost = line.UnitPrice * receiptLine.QuantityReceived,
                            Currency = order.Currency,
                            ExchangeRate = dto.ExchangeRate,
                            EntitlementDate = dto.ReceiptDate,
                            ExpiresAtAfter = incoming.LicenceExpiresAt,
                            GoodsReceiptLineId = receiptLine.Id,
                            CreatedByUserId = actingUserId,
                            Notes = dto.Notes
                        });

                        if (incoming.LicenceExpiresAt is { } newExpiry)
                        {
                            if (!existingLicenceFor.ContainsKey(line.Id))
                            {
                                // Created by this receipt: the row does not exist yet, so nobody
                                // else can have dated it.
                                licence.ExpiresAt = newExpiry;
                                licence.UpdatedAt = DateTime.UtcNow;
                            }
                            else
                            {
                                // The move, as a conditional update rather than an assignment to the
                                // tracked licence. That licence was read before the order lock, and
                                // receipts against different orders do not serialise on it, so its
                                // ExpiresAt may already be stale: two renewals would both pass the
                                // checks above and whichever saved last would win - possibly pulling
                                // a later term back to an earlier one. SoftwareLicence carries no
                                // concurrency token, so nothing else would catch it.
                                //
                                // This UPDATE ... WHERE ExpiresAt IS NULL OR ExpiresAt < new is the
                                // rule: the predicate is re-evaluated against the row at write time
                                // under that row's lock, so a concurrent renewal either blocks here
                                // and then matches nothing, or has already landed in the value being
                                // compared. Added seats may date an undated licence but never move a
                                // term, so they get only the IS NULL half. Same shape as the line
                                // claim above.
                                //
                                // Going round the tracker is safe: the tracked licence stays
                                // Unchanged, so SaveChanges writes nothing back over this, and the
                                // tracker is cleared before the result is returned. The automatic
                                // change log does not see this write; the audited entitlement's
                                // ExpiresAtAfter is the record of the new term.
                                var renewable = db.SoftwareLicences
                                    .Where(l => l.Id == licence.Id && l.TenantId == tenantId);
                                renewable = renewing
                                    ? renewable.Where(l => l.ExpiresAt == null || l.ExpiresAt < newExpiry)
                                    : renewable.Where(l => l.ExpiresAt == null);

                                var moved = await renewable.ExecuteUpdateAsync(setters => setters
                                    .SetProperty(l => l.ExpiresAt, newExpiry)
                                    .SetProperty(l => l.UpdatedAt, DateTime.UtcNow), ct);

                                if (moved != 1)
                                {
                                    // Returning rolls the transaction back as it is disposed. The
                                    // receipt is already saved and the entitlements are Added, so
                                    // the tracker is cleared for the reason the catch below gives.
                                    db.ChangeTracker.Clear();

                                    // Worded for both modes: a renewal overtaken by a later one and
                                    // added seats whose undated licence was dated meanwhile both
                                    // arrive here.
                                    return ServiceResult<int>.Fail(
                                        $"{line.Description ?? line.DeviceType}: '{licence.Name}' was re-dated by another delivery - check its expiry and try again.");
                                }
                            }
                        }

                        continue;
                    }

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

                var bySoftware = receipt.Lines.ToLookup(
                    l => order.Lines.First(o => o.Id == l.PurchaseOrderLineId).DeviceType == DeviceTypes.Software);

                logger.LogInformation(
                    "Received {Lines} line(s) against purchase order {PoNumber}, creating {Assets} asset(s) " +
                    "and {Entitlements} licence entitlement(s)",
                    receipt.Lines.Count, order.PoNumber,
                    bySoftware[false].Sum(l => l.QuantityReceived), bySoftware[true].Count());

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
                // tracker knows nothing about that: it still holds every Asset and
                // LicenceEntitlement as Added, any SoftwareLicence this receipt created as Added,
                // and the GoodsReceipt as Unchanged carrying the id of a row that no longer exists.
                // This is a scoped DbContext, so a later SaveChangesAsync on the same request would
                // insert those assets and entitlements pointing at a receipt line that was rolled
                // away - and Asset, LicenceEntitlement and SoftwareLicence are all in
                // AuditSaveChangesInterceptor.AuditedTypes, so it would mint audit rows swearing
                // to them. No current caller saves again in this scope; clearing
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
    /// A licence named in the receive dialog, created inside the receiving transaction. Its supplier
    /// is the order's - the delivery is the evidence of who sold it.
    /// </summary>
    private SoftwareLicence AddLicence(PurchaseOrder order, NewLicenceInputDto described)
    {
        var licence = new SoftwareLicence
        {
            TenantId = order.TenantId,
            Name = described.Name.Trim(),
            Publisher = string.IsNullOrWhiteSpace(described.Publisher) ? null : described.Publisher.Trim(),
            LicenceModel = described.LicenceModel,
            SupplierId = order.SupplierId
        };

        db.SoftwareLicences.Add(licence);
        return licence;
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
