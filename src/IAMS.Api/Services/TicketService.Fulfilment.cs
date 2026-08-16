using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public partial class TicketService
{
    /// <summary>
    /// Closes an equipment Request by issuing an asset to its requester. Ticket closure,
    /// assignment creation and the asset status change are one transaction: a partial
    /// success here would leave the assignment history lying, which is the one thing
    /// this system exists to prevent.
    /// </summary>
    public async Task<ServiceResult> FulfilAsync(
        int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return ServiceResult.Fail("A resolution note is required.");

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

            asset.Status = AssetStatus.InUse;
            asset.AssignedToUserId = ticket.RequesterUserId;
            asset.UpdatedAt = now;

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
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(ct);
            return ServiceResult.Fail($"Could not fulfil the request: {ex.Message}");
        }
        catch
        {
            // Any other failure (cancellation, an unexpected exception, etc.) must still
            // roll back before propagating, so a partial write never survives.
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
