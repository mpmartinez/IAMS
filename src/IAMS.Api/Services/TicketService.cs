using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAMS.Api.Services;

public record ServiceResult(bool Success, string? Message = null)
{
    public static ServiceResult Ok() => new(true);
    public static ServiceResult Fail(string message) => new(false, message);
}

public record ServiceResult<T>(bool Success, T? Value, string? Message = null)
{
    public static ServiceResult<T> Ok(T value) => new(true, value);
    public static ServiceResult<T> Fail(string message) => new(false, default, message);
}

public record TicketQuery(
    string? Type,
    string? Category,
    string? Status,
    string? Priority,
    string? AssignedToUserId,
    int? AssetId,
    string? Search,
    int Page = 1,
    int PageSize = 25);

public record TicketSummary(int Open, int Unassigned, int InProgress, int Overdue);

public interface ITicketService
{
    Task<ServiceResult<Ticket>> CreateAsync(
        string type, string category, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default);

    Task<Ticket?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Returns the requested page along with the page and size actually used. Callers must
    /// report these rather than the values they passed in — this method clamps both, and a
    /// response echoing an unclamped page size makes the client's page arithmetic wrong.
    /// </summary>
    Task<(List<Ticket> Items, int TotalCount, int Page, int PageSize)> ListAsync(
        TicketQuery query, CancellationToken ct = default);

    Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default);

    Task<ServiceResult> AssignAsync(int id, string assigneeUserId, CancellationToken ct = default);

    Task<ServiceResult> ChangeStatusAsync(int id, string status, CancellationToken ct = default);

    Task<ServiceResult> ResolveAsync(int id, string resolution, CancellationToken ct = default);

    Task<ServiceResult<TicketComment>> AddCommentAsync(
        int ticketId, string userId, string body, bool isInternal, CancellationToken ct = default);

    Task<ServiceResult> FulfilAsync(
        int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default);
}

public partial class TicketService : ITicketService
{
    private const int MaxNumberRetries = 3;
    private const int MaxTitleLength = 200;
    private const int MaxCommentLength = 4000;

    // Lower rank = lower urgency. Keyed by the TicketPriority constants.
    private static readonly Dictionary<string, int> PriorityRank = new()
    {
        [TicketPriority.Low] = 0,
        [TicketPriority.Medium] = 1,
        [TicketPriority.High] = 2,
        [TicketPriority.Critical] = 3
    };

    private readonly AppDbContext _db;
    private readonly ITicketNumberAllocator _numbers;
    private readonly ITenantProvider _tenants;
    private readonly ILogger<TicketService> _logger;

    // The logger is optional so the many tests that construct this service directly stay
    // readable; every composition-root path resolves a real one from DI.
    public TicketService(
        AppDbContext db,
        ITicketNumberAllocator numbers,
        ITenantProvider tenants,
        ILogger<TicketService>? logger = null)
    {
        _db = db;
        _numbers = numbers;
        _tenants = tenants;
        _logger = logger ?? NullLogger<TicketService>.Instance;
    }

    // Raises `priority` to `floor` when it ranks lower, but never lowers a priority that
    // already ranks at or above the floor.
    private static string RaiseToAtLeast(string priority, string floor) =>
        PriorityRank[priority] >= PriorityRank[floor] ? priority : floor;

    public async Task<ServiceResult<Ticket>> CreateAsync(
        string type, string category, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default)
    {
        if (!TicketTypes.IsValid(type))
            return ServiceResult<Ticket>.Fail($"'{type}' is not a valid ticket type.");

        if (!TicketCategory.IsValid(category))
            return ServiceResult<Ticket>.Fail($"'{category}' is not a valid ticket category.");

        if (string.IsNullOrWhiteSpace(title))
            return ServiceResult<Ticket>.Fail("A ticket needs a title.");

        var trimmedTitle = title.Trim();
        if (trimmedTitle.Length > MaxTitleLength)
            return ServiceResult<Ticket>.Fail($"Title cannot exceed {MaxTitleLength} characters.");

        if (!TicketPriority.IsValid(priority))
            return ServiceResult<Ticket>.Fail($"'{priority}' is not a valid priority.");

        var tenantId = _tenants.GetRequiredTenantId();

        if (assetId is not null)
        {
            // The query filter already scopes this to the current tenant, so a foreign
            // asset id simply does not resolve.
            var assetExists = await _db.Assets.AnyAsync(a => a.Id == assetId, ct);
            if (!assetExists)
                return ServiceResult<Ticket>.Fail("That asset does not exist.");
        }

        // ApplicationUser has no global tenant query filter (it's the Identity table), so
        // resolve the requester through an explicit tenant filter the same way the asset
        // is resolved through its query filter - otherwise another tenant's user id would
        // satisfy the FK and quietly attribute the ticket to a user outside the tenant.
        var requesterExists = await _db.Users
            .AnyAsync(u => u.Id == requesterUserId && u.TenantId == tenantId, ct);
        if (!requesterExists)
            return ServiceResult<Ticket>.Fail("That requester does not exist.");

        // A security report is never low priority, whatever the form said - but it must
        // never be *lowered* either, so raise to High as a floor, not a fixed value.
        var effectivePriority = type == TicketTypes.SecurityEvent
            ? RaiseToAtLeast(priority, TicketPriority.High)
            : priority;

        for (var attempt = 1; ; attempt++)
        {
            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = await _numbers.NextAsync(tenantId, ct),
                Type = type,
                Category = category,
                Title = trimmedTitle,
                Description = description,
                Status = TicketStatus.New,
                Priority = effectivePriority,
                AssetId = assetId,
                RequesterUserId = requesterUserId,
                CreatedAt = DateTime.UtcNow
            };

            _db.Tickets.Add(ticket);

            try
            {
                await _db.SaveChangesAsync(ct);
                return ServiceResult<Ticket>.Ok(ticket);
            }
            catch (DbUpdateException)
            {
                _db.Entry(ticket).State = EntityState.Detached;

                // Npgsql (production) and SQLite (tests) word unique-violation errors
                // completely differently, so we can't reliably tell a ticket-number
                // collision apart from any other DbUpdateException (an FK violation, a
                // length violation, etc.) by inspecting the exception. Instead, verify by
                // querying: if a ticket now holds this (TenantId, TicketNumber), another
                // request won the race and it is safe to retry with a fresh number. If not,
                // the failure had some other cause and must propagate instead of being
                // silently retried three times and then escaping anyway.
                var collided = await _db.Tickets
                    .IgnoreQueryFilters()
                    .AnyAsync(t => t.TenantId == tenantId && t.TicketNumber == ticket.TicketNumber, ct);

                if (!collided || attempt >= MaxNumberRetries)
                    throw;
            }
        }
    }

    public Task<Ticket?> GetAsync(int id, CancellationToken ct = default) =>
        _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<(List<Ticket> Items, int TotalCount, int Page, int PageSize)> ListAsync(
        TicketQuery query, CancellationToken ct = default)
    {
        var q = _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Type))
            q = q.Where(t => t.Type == query.Type);

        if (!string.IsNullOrWhiteSpace(query.Category))
            q = q.Where(t => t.Category == query.Category);

        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(t => t.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.Priority))
            q = q.Where(t => t.Priority == query.Priority);

        if (!string.IsNullOrWhiteSpace(query.AssignedToUserId))
            q = q.Where(t => t.AssignedToUserId == query.AssignedToUserId);

        if (query.AssetId is not null)
            q = q.Where(t => t.AssetId == query.AssetId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // EF.Functions.Like is case-insensitive on SQLite but case-sensitive on
            // Npgsql (production), and treats the term's own % / _ as wildcards instead
            // of literal characters. ToLower().Contains(...) matches the idiom used
            // elsewhere in this codebase (AssetsController, UsersController) and is
            // consistent across both providers.
            var term = query.Search.Trim().ToLower();
            q = q.Where(t =>
                t.Title.ToLower().Contains(term) ||
                (t.Description != null && t.Description.ToLower().Contains(term)));
        }

        var total = await q.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (items, total, page, size);
    }

    public async Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var open = await _db.Tickets.CountAsync(t => TicketStatus.Open.Contains(t.Status), ct);
        var unassigned = await _db.Tickets.CountAsync(
            t => TicketStatus.Open.Contains(t.Status) && t.AssignedToUserId == null, ct);
        var inProgress = await _db.Tickets.CountAsync(t => t.Status == TicketStatus.InProgress, ct);
        var overdue = await _db.Tickets.CountAsync(
            t => TicketStatus.Open.Contains(t.Status) && t.DueAt != null && t.DueAt < now, ct);

        return new TicketSummary(open, unassigned, inProgress, overdue);
    }
}
