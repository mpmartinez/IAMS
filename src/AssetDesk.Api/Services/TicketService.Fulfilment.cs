using System.Data.Common;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Services;

public partial class TicketService
{
    /// <summary>
    /// Closes an equipment Request by issuing an asset to its requester. Ticket closure,
    /// assignment creation and the asset status change are one transaction: a partial
    /// success here would leave the assignment history lying, which is the one thing
    /// this system exists to prevent.
    ///
    /// The transaction runs through the provider's execution strategy because production
    /// is Npgsql with EnableRetryOnFailure, and EF Core refuses a user-initiated
    /// transaction under a retrying strategy — the whole unit has to be handed to the
    /// strategy so it can be retried as a whole. That means the body below can run more
    /// than once, so it re-reads and re-checks everything from a cleared change tracker
    /// on every attempt. That clear happens unconditionally on entry, too: any change a
    /// caller staged on this DbContext before calling FulfilAsync is discarded, not saved
    /// alongside it. There is no caller yet that does that, but a future one must not
    /// assume otherwise.
    /// </summary>
    public async Task<ServiceResult> FulfilAsync(
        int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return ServiceResult.Fail("A resolution note is required.");

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this delegate on the same DbContext, which still tracks the
            // failed attempt's mutations (a Closed ticket, an InUse asset, an assignment
            // whose row no longer exists). Starting from a cleared tracker is what makes
            // the attempt independent: every entity below is loaded fresh and every guard
            // is re-evaluated against the database as it is now.
            _db.ChangeTracker.Clear();

            // Ticket carries a tenant query filter, so a ticket id from another tenant simply
            // does not resolve here - no explicit tenant check needed.
            var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
            if (ticket is null)
                return ServiceResult.Fail("Ticket not found.");

            if (ticket.Type != TicketTypes.Request)
                return ServiceResult.Fail("Only an equipment request can be fulfilled with an asset.");

            if (!TicketWorkflow.IsOpen(ticket.Status))
                return ServiceResult.Fail($"A {ticket.Status} ticket cannot be fulfilled.");

            // Asset carries a tenant query filter too, so a foreign asset id simply does not
            // resolve here.
            var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
            if (asset is null)
                return ServiceResult.Fail("That asset does not exist.");

            // A friendly, specific rejection for the ordinary case. It is not the invariant:
            // the availability check that actually holds is the conditional claim inside the
            // transaction below.
            if (asset.Status != AssetStatus.Available)
                return ServiceResult.Fail($"{asset.AssetTag} is not available — it is {asset.Status}.");

            // ApplicationUser has no global tenant query filter (it's the Identity table), so
            // resolve the acting user through an explicit tenant filter the same way the
            // requester is resolved in CreateAsync and the assignee in AssignAsync - otherwise
            // another tenant's user id would satisfy the AssignedByUser FK and quietly record a
            // foreign-tenant staff member as having issued this asset.
            var tenantId = _tenants.GetRequiredTenantId();
            var actingUserExists = await _db.Users
                .AnyAsync(u => u.Id == actingUserId && u.TenantId == tenantId, ct);
            if (!actingUserExists)
                return ServiceResult.Fail("That user does not exist.");

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = DateTime.UtcNow;

                // Claim the ticket conditionally, as the first write in the transaction -
                // ahead of the asset. The IsOpen guard above is a time-of-check/time-of-use
                // read: two fulfilments of the *same* ticket (with two different available
                // assets, so they never contend on the asset row) can both pass it and both
                // issue a machine, leaving one asset's assignment with no ticket pointing at
                // it. This app has an offline PWA whose sync queue replays queued actions, so
                // a double-submit of the same fulfilment is a realistic way to hit this, not
                // just a two-operator edge case. This UPDATE ... WHERE Status IN (open) is the
                // invariant - it closes the ticket only while it is still open, and holds that
                // row's lock until this transaction ends, so a concurrent fulfilment of the
                // same ticket either blocks here and then matches nothing, or already sees it
                // as Closed.
                //
                // Claimed ahead of the asset deliberately: a duplicate submission always
                // targets the same ticket, so rejecting the ticket claim first means the
                // Assets table is never touched for it at all. It also fixes a lock order
                // (Tickets before Assets) for this method's two claims; since this is the only
                // place that takes both locks, that ordering choice cannot deadlock against
                // itself, but keeping it fixed here is what would keep a future caller that
                // also needs both from deadlocking against this one.
                var ticketClaimed = await _db.Tickets
                    .Where(t => t.Id == ticket.Id && TicketStatus.Open.Contains(t.Status))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(t => t.AssetId, asset.Id)
                        .SetProperty(t => t.Resolution, resolution.Trim())
                        .SetProperty(t => t.Status, TicketStatus.Closed)
                        .SetProperty(t => t.ResolvedAt, now)
                        .SetProperty(t => t.ClosedAt, now), ct);

                if (ticketClaimed != 1)
                {
                    await SafeRollbackAsync(transaction);
                    _db.ChangeTracker.Clear();

                    // Fires only when the race this claim exists to close actually happens in
                    // production - worth a trace so how often that is isn't a total unknown.
                    _logger.LogWarning(
                        "Fulfilment of ticket {TicketId} rejected: ticket was no longer open when claimed.",
                        ticketId);

                    return ServiceResult.Fail(
                        "This request is no longer open — it was already fulfilled or closed a moment ago.");
                }

                // ExecuteUpdate bypasses the change tracker, so the interceptor that writes
                // the audit trail never sees the claim. Restate the same values on the tracked
                // entity so the Ticket change is still audited; EF re-issues them as an UPDATE
                // against the row this transaction already wrote, which is a no-op on the data
                // and the price of keeping the audit trail honest. AssetAssignmentId cannot be
                // set here — the assignment has no id until it is saved — so it is written after
                // the first save and produces a SECOND Ticket/Updated audit row carrying only
                // that field. Two rows per fulfilment is expected, not a bug: both are inside
                // this transaction and both are true.
                ticket.AssetId = asset.Id;
                ticket.Resolution = resolution.Trim();
                ticket.Status = TicketStatus.Closed;
                ticket.ResolvedAt = now;
                ticket.ClosedAt = now;

                // Claim the asset conditionally, second. The guard above is a
                // time-of-check/time-of-use read: two fulfilments of different tickets naming
                // the same asset can both pass it and both issue the same machine. This
                // UPDATE ... WHERE Status = 'Available' is the invariant - it affects a row
                // only while the asset is still available, and it holds that row's lock until
                // this transaction ends, so a concurrent fulfilment either blocks here and
                // then matches nothing, or is already visible to us as InUse. A re-read
                // instead of a conditional write would have exactly the same time-of-check
                // problem at read-committed isolation.
                var claimed = await _db.Assets
                    .Where(a => a.Id == asset.Id && a.Status == AssetStatus.Available)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(a => a.Status, AssetStatus.InUse)
                        .SetProperty(a => a.AssignedToUserId, ticket.RequesterUserId)
                        .SetProperty(a => a.UpdatedAt, now), ct);

                if (claimed != 1)
                {
                    await SafeRollbackAsync(transaction);
                    _db.ChangeTracker.Clear();

                    // Same reasoning as the ticket claim's log above: this is the trace for
                    // how often the asset side of the race is actually hit.
                    _logger.LogWarning(
                        "Fulfilment of ticket {TicketId} rejected: asset {AssetId} was no longer available when claimed.",
                        ticketId, assetId);

                    return ServiceResult.Fail(
                        $"{asset.AssetTag} is no longer available — it was issued to someone else a moment ago.");
                }

                // Same restatement reasoning as the ticket claim above.
                asset.Status = AssetStatus.InUse;
                asset.AssignedToUserId = ticket.RequesterUserId;
                asset.UpdatedAt = now;

                // ticket.RequesterUserId is not re-validated here: it was already resolved
                // through a tenant-scoped check when the ticket was created (CreateAsync), so
                // it is trustworthy without a second lookup.
                var assignment = new AssetAssignment
                {
                    TenantId = ticket.TenantId,
                    AssetId = asset.Id,
                    UserId = ticket.RequesterUserId,
                    AssignedAt = now,
                    AssignedByUserId = actingUserId
                };
                _db.AssetAssignments.Add(assignment);
                await _db.SaveChangesAsync(ct);

                ticket.AssetAssignmentId = assignment.Id;

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return ServiceResult.Ok();
            }
            // Only a durable failure becomes a failure result. A transient one (a database
            // restart, a dropped connection) is precisely what the execution strategy exists
            // to replay, so it must fall through to the rethrowing catch below and reach the
            // strategy instead of being reported to the caller as a rejected request.
            catch (DbUpdateException ex) when (!IsTransient(ex))
            {
                // CancellationToken.None, not ct: when the failure is itself a cancellation,
                // rolling back with the already-cancelled token throws and replaces the real
                // exception, leaving the rollback to transaction disposal instead of running
                // deliberately here.
                await SafeRollbackAsync(transaction);
                _db.ChangeTracker.Clear();

                // The exception text names constraints, columns and tables; it belongs in the
                // log, not in a message handed back to a caller.
                _logger.LogError(ex,
                    "Fulfilment of ticket {TicketId} with asset {AssetId} failed and was rolled back.",
                    ticketId, assetId);

                return ServiceResult.Fail("Could not fulfil the request. Please try again.");
            }
            catch
            {
                // Any other failure (cancellation, an unexpected exception, etc.) must still
                // roll back before propagating, so a partial write never survives.
                await SafeRollbackAsync(transaction);
                _db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    /// <summary>
    /// Rolls back a transaction without letting a rollback failure replace whatever exception
    /// (or claim rejection) triggered it. By the time any caller here reaches a rollback, the
    /// most likely cause is precisely the kind of failure that also kills the connection - a
    /// database restart, a dropped socket - and on a dead connection RollbackAsync
    /// itself throws. The server-side transaction is already gone in that case, so there is
    /// nothing left to roll back and swallowing the failure loses nothing. Letting it
    /// propagate instead would replace the original exception: a transient failure would stop
    /// looking transient to the execution strategy (defeating the retry this whole design
    /// exists to enable), and the non-transient path would leak raw provider text to the
    /// caller instead of running the friendly failure message that follows it.
    /// </summary>
    private static async Task SafeRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Deliberately swallowed - see method doc above.
        }
    }

    /// <summary>Walks the inner-exception chain for a provider exception the driver itself
    /// classes as transient (Npgsql sets this for connection and timeout failures).</summary>
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
