using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Data;
using AssetDesk.Api.Mapping;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/tickets/{ticketId:int}/comments")]
[Authorize]
public class TicketCommentsController : ControllerBase
{
    private readonly ITicketService _tickets;
    private readonly AppDbContext _db;

    public TicketCommentsController(ITicketService tickets, AppDbContext db)
    {
        _tickets = tickets;
        _db = db;
    }

    private string CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";

    /// Read access to the ticket queue: seeing internal comments and reading any ticket's thread.
    private bool CanViewQueue => User.HasPermission(Permissions.TicketsQueue);

    /// Write access to other people's tickets: authoring an internal (requester-hidden) comment.
    private bool CanManageQueue => User.HasPermission(Permissions.TicketsManage);

    [HttpGet]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<List<TicketCommentDto>>>> List(int ticketId, CancellationToken ct)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<List<TicketCommentDto>>.Fail("Ticket not found."));

        if (!CanViewQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var comments = await _db.TicketComments
            .Include(c => c.User)
            .Where(c => c.TicketId == ticketId)
            .Where(c => CanViewQueue || !c.IsInternal)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return Ok(ApiResponse<List<TicketCommentDto>>.Ok(comments.Select(c => c.ToDto()).ToList()));
    }

    [HttpPost]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<TicketCommentDto>>> Add(
        int ticketId, [FromBody] AddTicketCommentRequest request, CancellationToken ct)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<TicketCommentDto>.Fail("Ticket not found."));

        // Writing to someone else's ticket needs manage rights, not just read access to the
        // queue - TicketsQueue's catalog description promises read-only access ("see every ticket
        // in the tenant, not just your own"), so gating this write on it would hand comment rights
        // on other people's tickets to any role built from that description alone.
        if (!CanManageQueue && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        // Only staff with manage rights may write a note the requester cannot see.
        if (request.IsInternal && !CanManageQueue)
            return Forbid();

        var result = await _tickets.AddCommentAsync(
            ticketId, CurrentUserId, request.Body, request.IsInternal, ct);

        return result.Success
            ? Ok(ApiResponse<TicketCommentDto>.Ok(result.Value!.ToDto(), "Comment added."))
            : BadRequest(ApiResponse<TicketCommentDto>.Fail(result.Message!));
    }
}
