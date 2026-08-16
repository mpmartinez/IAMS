using System.Data.Common;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAMS.Api.Services;

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
    /// on every attempt.
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

                // Claim the asset conditionally, as the first write in the transaction. The
                // guard above is a time-of-check/time-of-use read: two fulfilments of
                // different tickets naming the same asset can both pass it and both issue the
                // same machine. This UPDATE ... WHERE Status = 'Available' is the invariant -
                // it affects a row only while the asset is still available, and it holds that
                // row's lock until this transaction ends, so a concurrent fulfilment either
                // blocks here and then matches nothing, or is already visible to us as InUse.
                // A re-read instead of a conditional write would have exactly the same
                // time-of-check problem at read-committed isolation.
                var claimed = await _db.Assets
                    .Where(a => a.Id == asset.Id && a.Status == AssetStatus.Available)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(a => a.Status, AssetStatus.InUse)
                        .SetProperty(a => a.AssignedToUserId, ticket.RequesterUserId)
                        .SetProperty(a => a.UpdatedAt, now), ct);

                if (claimed != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    _db.ChangeTracker.Clear();
                    return ServiceResult.Fail(
                        $"{asset.AssetTag} is no longer available — it was issued to someone else a moment ago.");
                }

                // ExecuteUpdate bypasses the change tracker, so the interceptor that writes
                // the audit trail never sees the claim. Restate the same values on the tracked
                // entity so the Asset change is still audited; EF re-issues them as an UPDATE
                // against the row this transaction already wrote, which is a no-op on the data
                // and the price of keeping the audit trail honest.
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

                ticket.AssetId = asset.Id;
                ticket.AssetAssignmentId = assignment.Id;
                ticket.Resolution = resolution.Trim();
                ticket.Status = TicketStatus.Closed;
                ticket.ResolvedAt = now;
                ticket.ClosedAt = now;

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return ServiceResult.Ok();
            }
            // Only a durable failure becomes a failure result. A transient one (a Neon compute
            // waking up, a dropped connection) is precisely what the execution strategy exists
            // to replay, so it must fall through to the rethrowing catch below and reach the
            // strategy instead of being reported to the caller as a rejected request.
            catch (DbUpdateException ex) when (!IsTransient(ex))
            {
                // CancellationToken.None, not ct: when the failure is itself a cancellation,
                // rolling back with the already-cancelled token throws and replaces the real
                // exception, leaving the rollback to transaction disposal instead of running
                // deliberately here.
                await transaction.RollbackAsync(CancellationToken.None);
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
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                throw;
            }
        });
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
