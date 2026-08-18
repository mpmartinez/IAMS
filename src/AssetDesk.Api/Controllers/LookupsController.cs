using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// Admin-editable reference data (device types, currencies, attachment categories, ticket
/// categories) plus the read-only locked vocabularies (ticket status/type, asset status,
/// ticket priority) that code branches on. Reading is open to any authenticated user because
/// every dropdown in the app needs it. Writing is SuperAdmin-only and rejects locked types
/// server-side, regardless of what a client sends - see LookupTypes.
/// </summary>
[ApiController]
[Route("api/lookups")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class LookupsController(AppDbContext db) : ControllerBase
{
    /// <summary>The catalogue of lookup vocabularies, editable and locked alike - drives the
    /// "pick a lookup type" selector on the admin screen.</summary>
    [HttpGet("types")]
    public ActionResult<ApiResponse<List<LookupTypeDto>>> GetTypes()
    {
        var types = LookupTypes.All.Select(t => new LookupTypeDto
        {
            Type = t,
            DisplayName = LookupTypes.DisplayName(t),
            IsEditable = LookupTypes.IsEditable(t),
            LockedReason = LookupTypes.LockedReason(t)
        }).ToList();

        return Ok(ApiResponse<List<LookupTypeDto>>.Ok(types));
    }

    /// <summary>
    /// The values for one lookup type. Defaults to active values only - the shape every
    /// dropdown wants. Pass includeInactive=true for the admin screen, which also needs to
    /// show (and let an admin reactivate) deactivated values.
    /// </summary>
    [HttpGet("{type}")]
    public async Task<ActionResult<ApiResponse<List<LookupValueDto>>>> GetValues(
        string type, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        if (!LookupTypes.IsValidType(type))
            return NotFound(ApiResponse<List<LookupValueDto>>.Fail($"Unknown lookup type '{type}'."));

        var query = db.LookupValues.Where(l => l.LookupType == type);
        if (!includeInactive)
            query = query.Where(l => l.IsActive);

        var values = await query
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Label)
            .Select(l => MapToDto(l))
            .ToListAsync(ct);

        return Ok(ApiResponse<List<LookupValueDto>>.Ok(values));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "SuperAdmin")]
    public async Task<ActionResult<ApiResponse<LookupValueDto>>> Create(
        CreateLookupValueDto dto, CancellationToken ct)
    {
        if (!LookupTypes.IsValidType(dto.LookupType))
            return BadRequest(ApiResponse<LookupValueDto>.Fail($"Unknown lookup type '{dto.LookupType}'."));

        if (!LookupTypes.IsEditable(dto.LookupType))
            return BadRequest(ApiResponse<LookupValueDto>.Fail(
                $"'{LookupTypes.DisplayName(dto.LookupType)}' is a locked lookup type and cannot be edited. " +
                LookupTypes.LockedReason(dto.LookupType)));

        var value = dto.Value?.Trim();
        var label = dto.Label?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(ApiResponse<LookupValueDto>.Fail("Value is required."));
        if (string.IsNullOrWhiteSpace(label))
            return BadRequest(ApiResponse<LookupValueDto>.Fail("Label is required."));
        if (value.Length > 50)
            return BadRequest(ApiResponse<LookupValueDto>.Fail("Value cannot exceed 50 characters."));
        if (label.Length > 100)
            return BadRequest(ApiResponse<LookupValueDto>.Fail("Label cannot exceed 100 characters."));

        var duplicate = await db.LookupValues
            .AnyAsync(l => l.LookupType == dto.LookupType && l.Value == value, ct);
        if (duplicate)
            return BadRequest(ApiResponse<LookupValueDto>.Fail($"'{value}' already exists for {LookupTypes.DisplayName(dto.LookupType)}."));

        var maxSortOrder = await db.LookupValues
            .Where(l => l.LookupType == dto.LookupType)
            .Select(l => (int?)l.SortOrder)
            .MaxAsync(ct);

        var entity = new LookupValue
        {
            LookupType = dto.LookupType,
            Value = value,
            Label = label,
            SortOrder = dto.SortOrder ?? (maxSortOrder ?? -1) + 1,
            IsActive = true
        };

        db.LookupValues.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetValues), new { type = entity.LookupType },
            ApiResponse<LookupValueDto>.Ok(MapToDto(entity), "Value created."));
    }

    [HttpPut("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "SuperAdmin")]
    public async Task<ActionResult<ApiResponse<LookupValueDto>>> Update(
        int id, UpdateLookupValueDto dto, CancellationToken ct)
    {
        var entity = await db.LookupValues.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (entity is null)
            return NotFound(ApiResponse<LookupValueDto>.Fail("Lookup value not found."));

        if (!LookupTypes.IsEditable(entity.LookupType))
            return BadRequest(ApiResponse<LookupValueDto>.Fail(
                $"'{LookupTypes.DisplayName(entity.LookupType)}' is a locked lookup type and cannot be edited. " +
                LookupTypes.LockedReason(entity.LookupType)));

        if (dto.Label is not null)
        {
            var label = dto.Label.Trim();
            if (string.IsNullOrWhiteSpace(label))
                return BadRequest(ApiResponse<LookupValueDto>.Fail("Label cannot be blank."));
            if (label.Length > 100)
                return BadRequest(ApiResponse<LookupValueDto>.Fail("Label cannot exceed 100 characters."));
            entity.Label = label;
        }

        if (dto.SortOrder.HasValue)
            entity.SortOrder = dto.SortOrder.Value;

        if (dto.IsActive.HasValue)
            entity.IsActive = dto.IsActive.Value;

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<LookupValueDto>.Ok(MapToDto(entity), "Value updated."));
    }

    /// <summary>Reorders every value of one lookup type in a single request: <paramref
    /// name="orderedIds"/> is the full set of ids for that type, in the order they should
    /// display.</summary>
    [HttpPost("{type}/reorder")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "SuperAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Reorder(
        string type, [FromBody] List<int> orderedIds, CancellationToken ct)
    {
        if (!LookupTypes.IsValidType(type))
            return NotFound(ApiResponse<object>.Fail($"Unknown lookup type '{type}'."));

        if (!LookupTypes.IsEditable(type))
            return BadRequest(ApiResponse<object>.Fail(
                $"'{LookupTypes.DisplayName(type)}' is a locked lookup type and cannot be reordered. " +
                LookupTypes.LockedReason(type)));

        var values = await db.LookupValues
            .Where(l => l.LookupType == type && orderedIds.Contains(l.Id))
            .ToListAsync(ct);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = values.FirstOrDefault(v => v.Id == orderedIds[i]);
            if (entity is null) continue;
            entity.SortOrder = i;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { }, "Reordered."));
    }

    /// <summary>
    /// Deletion is deliberately unavailable, not merely absent from the UI: asset, ticket and
    /// attachment records store a lookup's Value as a raw string, so deleting a value that is
    /// still referenced would orphan every record that used it. Deactivate it instead (PUT with
    /// IsActive=false), which hides it from new records without touching history.
    /// </summary>
    [HttpDelete("{id:int}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "SuperAdmin")]
    public ActionResult<ApiResponse<object>> Delete(int id) =>
        StatusCode(StatusCodes.Status405MethodNotAllowed, ApiResponse<object>.Fail(
            "Lookup values cannot be deleted, only deactivated - existing records may still reference this value. " +
            "Set IsActive to false instead."));

    private static LookupValueDto MapToDto(LookupValue l) => new()
    {
        Id = l.Id,
        LookupType = l.LookupType,
        Value = l.Value,
        Label = l.Label,
        SortOrder = l.SortOrder,
        IsActive = l.IsActive
    };
}
