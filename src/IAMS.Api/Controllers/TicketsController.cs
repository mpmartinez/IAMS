using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _tickets;
    private readonly AppDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly ITenantProvider _tenants;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        ITicketService tickets,
        AppDbContext db,
        ISubscriptionService subscriptions,
        ITenantProvider tenants,
        ILogger<TicketsController> logger)
    {
        _tickets = tickets;
        _db = db;
        _subscriptions = subscriptions;
        _tenants = tenants;
        _logger = logger;
    }

    private string CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
    private bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Staff");

    [HttpGet]
    [Authorize(Policy = "CanViewTicketQueue")]
    public async Task<ActionResult<ApiResponse<PagedResponse<TicketListItemDto>>>> List(
        [FromQuery] string? type,
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] string? assignedToUserId,
        [FromQuery] int? assetId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        // effectivePage/effectiveSize, not the raw query values: ListAsync clamps both, and
        // reporting the unclamped ones makes the client's TotalCount / PageSize arithmetic wrong.
        var (items, total, effectivePage, effectiveSize) = await _tickets.ListAsync(
            new TicketQuery(type, category, status, priority, assignedToUserId, assetId, search, page, pageSize), ct);

        var payload = new PagedResponse<TicketListItemDto>
        {
            Items = items.Select(t => t.ToListItem()).ToList(),
            Page = effectivePage,
            PageSize = effectiveSize,
            TotalCount = total
        };

        return Ok(ApiResponse<PagedResponse<TicketListItemDto>>.Ok(payload));
    }

    [HttpGet("summary")]
    [Authorize(Policy = "CanViewTicketQueue")]
    public async Task<ActionResult<ApiResponse<TicketSummaryDto>>> Summary(CancellationToken ct)
    {
        var summary = await _tickets.GetSummaryAsync(ct);

        return Ok(ApiResponse<TicketSummaryDto>.Ok(new TicketSummaryDto
        {
            Open = summary.Open,
            Unassigned = summary.Unassigned,
            InProgress = summary.InProgress,
            Overdue = summary.Overdue
        }));
    }

    [HttpGet("mine")]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<List<TicketListItemDto>>>> Mine(CancellationToken ct)
    {
        var mine = await _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .Where(t => t.RequesterUserId == CurrentUserId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return Ok(ApiResponse<List<TicketListItemDto>>.Ok(mine.Select(t => t.ToListItem()).ToList()));
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<TicketDto>>> Get(int id, CancellationToken ct)
    {
        var ticket = await _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .Include(t => t.Comments).ThenInclude(c => c.User)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (ticket is null)
            return NotFound(ApiResponse<TicketDto>.Fail("Ticket not found."));

        // A requester may read their own ticket; everyone else needs staff rights.
        if (!IsStaff && ticket.RequesterUserId != CurrentUserId)
            return Forbid();

        return Ok(ApiResponse<TicketDto>.Ok(ticket.ToDto(includeInternalComments: IsStaff)));
    }

    [HttpPost]
    [Authorize(Policy = "CanFileTickets")]
    public async Task<ActionResult<ApiResponse<TicketDto>>> Create(
        [FromBody] CreateTicketRequest request, CancellationToken ct)
    {
        var tenantId = _tenants.GetRequiredTenantId();
        if (!await _subscriptions.CanCreateTicketAsync(tenantId))
            return BadRequest(ApiResponse<TicketDto>.Fail(
                "Monthly ticket limit reached for your subscription. Please upgrade or contact your administrator."));

        ServiceResult<Ticket> result;
        try
        {
            result = await _tickets.CreateAsync(
                request.Type, request.Category, request.Title, request.Description, request.Priority,
                request.AssetId, CurrentUserId, ct);
        }
        catch (DbUpdateException ex)
        {
            // CreateAsync exhausts its own retries against ticket-number collisions and
            // rethrows rather than silently retrying forever. By the time it reaches here
            // the failure is either a genuine, repeated collision or some other durable
            // write failure - neither is the caller's fault, so report it as a conflict
            // rather than letting it surface as an unhandled 500.
            _logger.LogError(ex, "Ticket creation failed after retries for requester {RequesterUserId}.", CurrentUserId);
            return Conflict(ApiResponse<TicketDto>.Fail(
                "Could not create the ticket right now - please try again."));
        }

        if (!result.Success)
            return BadRequest(ApiResponse<TicketDto>.Fail(result.Message!));

        var created = await _tickets.GetAsync(result.Value!.Id, ct);
        return Ok(ApiResponse<TicketDto>.Ok(created!.ToDto(includeInternalComments: IsStaff), "Ticket created."));
    }

    [HttpPost("{id:int}/assign")]
    [Authorize(Policy = "CanManageTicketQueue")]
    public async Task<ActionResult<ApiResponse<object>>> Assign(
        int id, [FromBody] AssignTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.AssignAsync(id, request.AssignedToUserId, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Ticket assigned."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/status")]
    [Authorize(Policy = "CanManageTicketQueue")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeStatus(
        int id, [FromBody] ChangeTicketStatusRequest request, CancellationToken ct)
    {
        var result = await _tickets.ChangeStatusAsync(id, request.Status, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, $"Ticket moved to {request.Status}."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/resolve")]
    [Authorize(Policy = "CanManageTicketQueue")]
    public async Task<ActionResult<ApiResponse<object>>> Resolve(
        int id, [FromBody] ResolveTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.ResolveAsync(id, request.Resolution, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Ticket resolved."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }

    [HttpPost("{id:int}/fulfil")]
    [Authorize(Policy = "CanManageAssets")]
    public async Task<ActionResult<ApiResponse<object>>> Fulfil(
        int id, [FromBody] FulfilTicketRequest request, CancellationToken ct)
    {
        var result = await _tickets.FulfilAsync(id, request.AssetId, request.Resolution, CurrentUserId, ct);

        return result.Success
            ? Ok(ApiResponse<object>.Ok(new { }, "Request fulfilled and asset issued."))
            : BadRequest(ApiResponse<object>.Fail(result.Message!));
    }
}
