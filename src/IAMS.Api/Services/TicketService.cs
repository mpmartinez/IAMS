using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

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
        string type, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default);

    Task<Ticket?> GetAsync(int id, CancellationToken ct = default);

    Task<(List<Ticket> Items, int TotalCount)> ListAsync(TicketQuery query, CancellationToken ct = default);

    Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default);
}

public partial class TicketService : ITicketService
{
    private const int MaxNumberRetries = 3;

    private readonly AppDbContext _db;
    private readonly ITicketNumberAllocator _numbers;
    private readonly ITenantProvider _tenants;

    public TicketService(AppDbContext db, ITicketNumberAllocator numbers, ITenantProvider tenants)
    {
        _db = db;
        _numbers = numbers;
        _tenants = tenants;
    }

    public async Task<ServiceResult<Ticket>> CreateAsync(
        string type, string title, string? description, string priority,
        int? assetId, string requesterUserId, CancellationToken ct = default)
    {
        if (!TicketTypes.IsValid(type))
            return ServiceResult<Ticket>.Fail($"'{type}' is not a valid ticket type.");

        if (string.IsNullOrWhiteSpace(title))
            return ServiceResult<Ticket>.Fail("A ticket needs a title.");

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

        // A security report is never low priority, whatever the form said.
        var effectivePriority = type == TicketTypes.SecurityEvent
            ? TicketPriority.High
            : priority;

        for (var attempt = 1; ; attempt++)
        {
            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = await _numbers.NextAsync(tenantId, ct),
                Type = type,
                Title = title.Trim(),
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
            catch (DbUpdateException) when (attempt < MaxNumberRetries)
            {
                // Another request took the number between our MAX read and this insert.
                _db.Entry(ticket).State = EntityState.Detached;
            }
        }
    }

    public Task<Ticket?> GetAsync(int id, CancellationToken ct = default) =>
        _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<(List<Ticket> Items, int TotalCount)> ListAsync(
        TicketQuery query, CancellationToken ct = default)
    {
        var q = _db.Tickets
            .Include(t => t.Asset)
            .Include(t => t.RequesterUser)
            .Include(t => t.AssignedToUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Type))
            q = q.Where(t => t.Type == query.Type);

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
            var term = $"%{query.Search.Trim()}%";
            q = q.Where(t =>
                EF.Functions.Like(t.Title, term) ||
                (t.Description != null && EF.Functions.Like(t.Description, term)));
        }

        var total = await q.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (items, total);
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
