using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

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
    private bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Staff");

    [HttpGet]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<List<TicketCommentDto>>>> List(int ticketId, CancellationToken ct)
    {
        var ticket = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return NotFound(ApiResponse<List<TicketCommentDto>>.Fail("Ticket not found."));

        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        var comments = await _db.TicketComments
            .Include(c => c.User)
            .Where(c => c.TicketId == ticketId)
            .Where(c => IsStaff || !c.IsInternal)
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

        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        // Only staff may write a note the requester cannot see.
        if (request.IsInternal && !IsStaff)
            return Forbid();

        var result = await _tickets.AddCommentAsync(
            ticketId, CurrentUserId, request.Body, request.IsInternal, ct);

        return result.Success
            ? Ok(ApiResponse<TicketCommentDto>.Ok(result.Value!.ToDto(), "Comment added."))
            : BadRequest(ApiResponse<TicketCommentDto>.Fail(result.Message!));
    }
}
