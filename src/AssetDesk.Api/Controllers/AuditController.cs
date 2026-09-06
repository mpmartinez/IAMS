using System.Text.Json;
using AssetDesk.Api.Data;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// Reads the append-only trail that AuditSaveChangesInterceptor writes. Read-only by
/// construction: AuditLog has no update or delete path anywhere in the API, and adding one
/// here would defeat the point of the table.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewAuditLog")]
public class AuditController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILogger<AuditController> logger) : ControllerBase
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 25;

    /// <summary>
    /// Page the audit trail, newest first.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<AuditLogDto>>>> GetAuditLog(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? entityType = null,
        [FromQuery] string? entityId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // Tenant scoping comes from AppDbContext's global query filter, as everywhere else.
        // It is bypassed for a super-admin, who therefore reads across every tenant - hence
        // TenantId on the DTO, so the caller can tell whose row it is looking at.
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);

        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(a => a.EntityId == entityId);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);

        if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(a => a.UserId == userId);

        if (fromDate.HasValue)
            query = query.Where(a => a.Timestamp >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(a => a.Timestamp <= toDate.Value);

        var totalCount = await query.CountAsync();

        // The Id tie-break is load-bearing, not decoration. One SaveChanges writes a whole
        // batch of rows whose timestamps are microseconds apart or identical, and the database
        // is free to return equal-timestamp rows in any order - which would let a row shuffle
        // between pages and show up twice, or not at all.
        var rows = await query
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id, a.TenantId, a.EntityType, a.EntityId, a.Action, a.UserId, a.Changes, a.Timestamp
            })
            .ToListAsync();

        var names = await ResolveUserNamesAsync(rows.Select(r => r.UserId));

        var items = rows.Select(r => new AuditLogDto
        {
            Id = r.Id,
            TenantId = r.TenantId,
            EntityType = r.EntityType,
            EntityId = r.EntityId,
            Action = r.Action,
            UserId = r.UserId,
            UserName = r.UserId is null
                ? "System"
                : names.GetValueOrDefault(r.UserId) ?? "Unknown user",
            Timestamp = r.Timestamp,
            Changes = ParseChanges(r.Changes, r.Id)
        }).ToList();

        return Ok(ApiResponse<PagedResponse<AuditLogDto>>.Ok(new PagedResponse<AuditLogDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        }));
    }

    /// <summary>
    /// The entity types and actions actually present in this tenant's trail. The dropdowns are
    /// built from this rather than from AuditedTypes, so widening what gets audited - or a type
    /// that has simply never been touched here - never leaves the UI offering a filter that
    /// matches nothing, or hiding one that would.
    /// </summary>
    [HttpGet("filters")]
    public async Task<ActionResult<ApiResponse<AuditFilterOptionsDto>>> GetFilterOptions()
    {
        var entityTypes = await db.AuditLogs.AsNoTracking()
            .Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync();

        var actions = await db.AuditLogs.AsNoTracking()
            .Select(a => a.Action).Distinct().OrderBy(t => t).ToListAsync();

        return Ok(ApiResponse<AuditFilterOptionsDto>.Ok(new AuditFilterOptionsDto
        {
            EntityTypes = entityTypes,
            Actions = actions
        }));
    }

    /// <summary>
    /// Resolves display names for the ids on one page. AuditLog deliberately has no navigation
    /// to ApplicationUser - the row has to outlive the account it names - so this is a separate
    /// lookup, and an id that no longer resolves is simply absent from the result.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveUserNamesAsync(IEnumerable<string?> userIds)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0) return [];

        // A super-admin reads across tenants, so the user lookup has to reach as wide as the
        // audit rows it is naming; otherwise every cross-tenant row would read "Unknown user".
        var users = tenantProvider.IsSuperAdmin()
            ? db.Users.IgnoreQueryFilters()
            : db.Users.AsQueryable();

        return await users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
    }

    /// <summary>
    /// Turns the interceptor's { "Field": { "from": ..., "to": ... } } blob into a flat list.
    ///
    /// A blob that will not parse yields an empty list rather than failing the request: a page
    /// of history must not vanish because one row is unreadable. The loss is logged, since a
    /// malformed blob means the interceptor wrote something unexpected and that is worth
    /// knowing about.
    /// </summary>
    private List<AuditChangeDto> ParseChanges(string? json, long auditLogId)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

            return document.RootElement.EnumerateObject()
                .Select(property => new AuditChangeDto
                {
                    Field = property.Name,
                    From = ReadSide(property.Value, "from"),
                    To = ReadSide(property.Value, "to")
                })
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Audit log {AuditLogId} has an unparseable Changes blob; returning it with no field diff.",
                auditLogId);
            return [];
        }
    }

    /// <summary>
    /// Reads one side of a field's diff as text. The interceptor serialises whatever the
    /// property held, so the value can be a string, a number, a bool or null; a JSON null and
    /// an absent side both mean "no value" and render the same.
    /// </summary>
    private static string? ReadSide(JsonElement change, string side)
    {
        if (change.ValueKind != JsonValueKind.Object) return null;
        if (!change.TryGetProperty(side, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
    }
}
