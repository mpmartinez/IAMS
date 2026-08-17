using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public partial class TicketService
{
    public async Task<ServiceResult> AssignAsync(
        int id, string assigneeUserId, CancellationToken ct = default)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        // Judge whether the ticket's current status even permits (re)assignment before
        // touching the assignee - otherwise a bad user id on a closed ticket reports "That
        // user does not exist" instead of the real reason it was rejected.
        // Reassigning an in-flight ticket keeps its status; only a New ticket advances.
        var advanceToAssigned = false;
        if (ticket.Status == TicketStatus.New)
        {
            if (!TicketWorkflow.CanTransition(ticket.Status, TicketStatus.Assigned))
                return ServiceResult.Fail($"A {ticket.Status} ticket cannot be assigned.");

            advanceToAssigned = true;
        }
        else if (!TicketWorkflow.IsOpen(ticket.Status))
        {
            return ServiceResult.Fail($"A {ticket.Status} ticket cannot be reassigned.");
        }

        // ApplicationUser has no global tenant query filter (it's the Identity table), so
        // resolve the assignee through an explicit tenant filter the same way the requester
        // is resolved in CreateAsync - otherwise another tenant's user id would satisfy the
        // FK and quietly assign the ticket to a user outside the tenant.
        var tenantId = _tenants.GetRequiredTenantId();
        var assigneeExists = await _db.Users
            .AnyAsync(u => u.Id == assigneeUserId && u.TenantId == tenantId, ct);
        if (!assigneeExists)
            return ServiceResult.Fail("That user does not exist.");

        if (advanceToAssigned)
            ticket.Status = TicketStatus.Assigned;

        ticket.AssignedToUserId = assigneeUserId;
        ticket.AssignedAt ??= DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ChangeStatusAsync(
        int id, string status, CancellationToken ct = default)
    {
        // Locked - TicketWorkflow.CanTransition is a hardcoded transition table over these
        // exact values, so this keeps validating against the constant.
        if (!TicketStatus.IsValid(status))
            return ServiceResult.Fail($"'{status}' is not a valid status.");

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        if (!TicketWorkflow.CanTransition(ticket.Status, status))
            return ServiceResult.Fail($"A ticket cannot move from {ticket.Status} to {status}.");

        // Resolve has its own method because it requires resolution text.
        if (status == TicketStatus.Resolved)
            return ServiceResult.Fail("Use Resolve so a resolution is recorded.");

        ticket.Status = status;

        if (status == TicketStatus.InProgress)
            ticket.StartedAt ??= DateTime.UtcNow;

        if (status is TicketStatus.Closed or TicketStatus.Cancelled)
            ticket.ClosedAt ??= DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ResolveAsync(
        int id, string resolution, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return ServiceResult.Fail("A resolution note is required.");

        var ticket = await _db.Tickets
            .Include(t => t.Asset)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (ticket is null)
            return ServiceResult.Fail("Ticket not found.");

        if (!TicketWorkflow.CanTransition(ticket.Status, TicketStatus.Resolved))
            return ServiceResult.Fail($"A {ticket.Status} ticket cannot be resolved.");

        ticket.Status = TicketStatus.Resolved;
        ticket.Resolution = resolution.Trim();
        ticket.ResolvedAt = DateTime.UtcNow;

        // An asset parked in Maintenance for this ticket returns to service.
        if (ticket.Asset is { Status: AssetStatus.Maintenance } asset)
        {
            asset.Status = asset.AssignedToUserId is null
                ? AssetStatus.Available
                : AssetStatus.InUse;
            asset.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<TicketComment>> AddCommentAsync(
        int ticketId, string userId, string body, bool isInternal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
            return ServiceResult<TicketComment>.Fail("A comment cannot be empty.");

        var trimmedBody = body.Trim();
        if (trimmedBody.Length > MaxCommentLength)
            return ServiceResult<TicketComment>.Fail($"Comment cannot exceed {MaxCommentLength} characters.");

        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return ServiceResult<TicketComment>.Fail("Ticket not found.");

        // ApplicationUser has no global tenant query filter (it's the Identity table), so
        // resolve the author through an explicit tenant filter the same way the requester
        // is resolved in CreateAsync and the assignee in AssignAsync - otherwise another
        // tenant's user id would satisfy the FK and quietly attribute a comment to a user
        // outside the tenant.
        var tenantId = _tenants.GetRequiredTenantId();
        var authorExists = await _db.Users
            .AnyAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        if (!authorExists)
            return ServiceResult<TicketComment>.Fail("That user does not exist.");

        var comment = new TicketComment
        {
            TenantId = ticket.TenantId,
            TicketId = ticket.Id,
            UserId = userId,
            Body = trimmedBody,
            IsInternal = isInternal,
            CreatedAt = DateTime.UtcNow
        };

        _db.TicketComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<TicketComment>.Ok(comment);
    }
}
