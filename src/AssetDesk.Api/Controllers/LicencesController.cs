using System.Security.Claims;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// Every query filters on the tenant explicitly rather than trusting the global query filter,
/// which has an IsSuperAdmin() bypass. The depreciation feature shipped a Critical for leaning on
/// that filter, and procurement had to be corrected for the same shape twice.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewLicences")]
public class LicencesController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILicenceUsageReader usage,
    ILookupService lookups) : ControllerBase
{
    private string CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated user is required.");

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<SoftwareLicenceDto>>>> GetAll(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<SoftwareLicenceDto>>.Fail("Select an organisation first."));

        var licences = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Include(l => l.Supplier)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);

        var usageByLicence = await usage.ReadAsync(tenantId, ct: ct);
        var locked = await LicencesWithSeatsAsync(tenantId, ct);
        var today = DateTime.UtcNow;

        return Ok(ApiResponse<List<SoftwareLicenceDto>>.Ok(licences
            .Select(l => Map(l, usageByLicence.GetValueOrDefault(l.Id, LicenceUsage.None), locked.Contains(l.Id), today))
            .ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> GetById(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        return await DetailAsync(tenantId, id, ct) is { } detail
            ? Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok(detail))
            : NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));
    }

    [HttpPost]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Create(
        UpsertSoftwareLicenceDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        if (await ValidateAsync(tenantId, dto, existing: null, ct) is { } error)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(error));

        var licence = new SoftwareLicence
        {
            TenantId = tenantId,
            Name = dto.Name.Trim(),
            Publisher = TrimToNull(dto.Publisher),
            SupplierId = dto.SupplierId,
            LicenceModel = dto.LicenceModel,
            LicenceKey = dto.ClearKey ? null : TrimToNull(dto.LicenceKey),
            ExpiresAt = dto.ExpiresAt,
            Notes = TrimToNull(dto.Notes),
            IsActive = dto.IsActive
        };

        db.SoftwareLicences.Add(licence);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = licence.Id },
            ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, licence.Id, ct))!));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Update(
        int id, UpsertSoftwareLicenceDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (await ValidateAsync(tenantId, dto, licence, ct) is { } error)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(error));

        licence.Name = dto.Name.Trim();
        licence.Publisher = TrimToNull(dto.Publisher);
        licence.SupplierId = dto.SupplierId;
        licence.LicenceModel = dto.LicenceModel;
        licence.ExpiresAt = dto.ExpiresAt;
        licence.Notes = TrimToNull(dto.Notes);
        licence.IsActive = dto.IsActive;
        licence.UpdatedAt = DateTime.UtcNow;

        // The edit form never holds the key, so a blank field means "unchanged", not "remove".
        if (dto.ClearKey)
            licence.LicenceKey = null;
        else if (TrimToNull(dto.LicenceKey) is { } newKey)
            licence.LicenceKey = newKey;

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    /// <summary>Deactivates rather than deletes - entitlements and seat history reference it.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> Deactivate(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        licence.IsActive = false;
        licence.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    [HttpPost("{id:int}/entitlements")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> AddEntitlement(
        int id, AddLicenceEntitlementDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (!licence.IsActive)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"Licence '{licence.Name}' is deactivated. Reactivate it before recording seats against it."));

        if (dto.SeatsAdded == 0 && dto.ExpiresAt is null)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                "Record seats added or removed, or a new expiry date."));

        if (dto.Cost < 0)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("A cost cannot be negative."));

        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency, ct))
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail($"'{dto.Currency}' is not a valid currency."));

        if (CurrencyRules.Validate(dto.Currency, dto.ExchangeRate) is { } currencyError)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(currencyError));

        if (dto.ExpiresAt is { } expiry && expiry.Date <= dto.EntitlementDate.Date)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                "The new expiry must be after the entry's date."));

        if (dto.SeatsAdded < 0)
        {
            if (string.IsNullOrWhiteSpace(dto.Notes))
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Say why seats are being removed."));

            // Read then write, deliberately without a lock. This floor is a guard against a typo,
            // not a money invariant: two administrators reducing the same licence at the same
            // moment could take it below zero, and both reductions would sit in the ledger with
            // their authors and reasons, where the next person to open the licence sees them.
            var owned = await db.LicenceEntitlements
                .Where(e => e.TenantId == tenantId && e.SoftwareLicenceId == id)
                .SumAsync(e => e.SeatsAdded, ct);

            if (owned + dto.SeatsAdded < 0)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"Only {owned} seats are owned, so {-dto.SeatsAdded} cannot be removed."));
        }

        db.LicenceEntitlements.Add(new LicenceEntitlement
        {
            TenantId = tenantId,
            SoftwareLicenceId = id,
            SeatsAdded = dto.SeatsAdded,
            Cost = dto.Cost,
            Currency = dto.Currency,
            ExchangeRate = dto.ExchangeRate,
            EntitlementDate = dto.EntitlementDate,
            ExpiresAtAfter = dto.ExpiresAt,
            CreatedByUserId = CurrentUserId,
            Notes = TrimToNull(dto.Notes)
        });

        if (dto.ExpiresAt is { } newExpiry)
        {
            licence.ExpiresAt = newExpiry;
            licence.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    /// <summary>
    /// Over-assignment is allowed. Refusing the fifty-first seat does not stop the fifty-first
    /// install; it only stops it being recorded, and a register that reflects reality - including
    /// non-compliance - is the point.
    /// </summary>
    [HttpPost("{id:int}/seats")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> AssignSeat(
        int id, AssignLicenceSeatDto dto, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id, ct);
        if (licence is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Licence not found."));

        if (!licence.IsActive)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail($"Licence '{licence.Name}' is deactivated."));

        var toUser = !string.IsNullOrWhiteSpace(dto.UserId);
        var toAsset = dto.AssetId is not null;

        if (toUser == toAsset)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Assign the seat to one person or one device."));

        if (licence.LicenceModel == LicenceModels.PerUser && !toUser)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"'{licence.Name}' is licensed per user, so its seats go to people."));

        if (licence.LicenceModel == LicenceModels.PerDevice && !toAsset)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                $"'{licence.Name}' is licensed per device, so its seats go to devices."));

        if (toUser)
        {
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == dto.UserId && u.TenantId == tenantId)
                .Select(u => new { u.FullName, u.IsActive })
                .FirstOrDefaultAsync(ct);
            if (user is null)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("User not found."));
            if (!user.IsActive)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"{user.FullName} is deactivated, so cannot be given a seat."));
        }
        else
        {
            var asset = await db.Assets
                .AsNoTracking()
                .Where(a => a.Id == dto.AssetId && a.TenantId == tenantId)
                .Select(a => new { a.AssetTag, a.Status })
                .FirstOrDefaultAsync(ct);
            if (asset is null)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Asset not found."));
            if (asset.Status is AssetStatus.Retired or AssetStatus.Lost)
                return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(
                    $"{asset.AssetTag} is {asset.Status}, so cannot be given a seat."));
        }

        var alreadyHeld = $"That {(toUser ? "person" : "device")} already holds a seat on this licence.";

        if (await HoldsActiveSeatAsync(tenantId, id, dto, ct))
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(alreadyHeld));

        db.LicenceSeatAssignments.Add(new LicenceSeatAssignment
        {
            TenantId = tenantId,
            SoftwareLicenceId = id,
            UserId = toUser ? dto.UserId : null,
            AssetId = toAsset ? dto.AssetId : null,
            AssignedByUserId = CurrentUserId,
            Notes = TrimToNull(dto.Notes)
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The failed insert is still tracked as Added; clear it so neither the check below nor
            // any later save on this scoped context sees or retries it.
            db.ChangeTracker.Clear();

            // Two assignments of the same holder raced past the check above and the partial unique
            // index refused the second - say so. Anything else is a real failure and propagates.
            if (!await HoldsActiveSeatAsync(tenantId, id, dto, ct))
                throw;

            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail(alreadyHeld));
        }

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    [HttpPost("{id:int}/seats/{seatId:int}/release")]
    [Authorize(Policy = "CanManageLicences")]
    public async Task<ActionResult<ApiResponse<SoftwareLicenceDetailDto>>> ReleaseSeat(
        int id, int seatId, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("Select an organisation first."));

        var seat = await db.LicenceSeatAssignments
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SoftwareLicenceId == id && s.Id == seatId, ct);
        if (seat is null)
            return NotFound(ApiResponse<SoftwareLicenceDetailDto>.Fail("Seat not found."));

        if (seat.ReleasedAt is not null)
            return BadRequest(ApiResponse<SoftwareLicenceDetailDto>.Fail("That seat was already released."));

        seat.ReleasedAt = DateTime.UtcNow;
        seat.ReleasedByUserId = CurrentUserId;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<SoftwareLicenceDetailDto>.Ok((await DetailAsync(tenantId, id, ct))!));
    }

    [HttpGet("device/{assetId:int}")]
    public async Task<ActionResult<ApiResponse<List<DeviceLicenceDto>>>> GetDeviceLicences(
        int assetId, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<DeviceLicenceDto>>.Fail("Select an organisation first."));

        var rows = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.AssetId == assetId && s.ReleasedAt == null)
            .Select(s => new
            {
                s.SoftwareLicenceId,
                s.SoftwareLicence!.Name,
                s.SoftwareLicence.Publisher,
                s.SoftwareLicence.ExpiresAt,
                s.AssignedAt
            })
            .ToListAsync(ct);

        var today = DateTime.UtcNow;
        return Ok(ApiResponse<List<DeviceLicenceDto>>.Ok(rows
            .OrderBy(r => r.Name)
            .Select(r => new DeviceLicenceDto
            {
                LicenceId = r.SoftwareLicenceId,
                Name = r.Name,
                Publisher = r.Publisher,
                AssignedAt = r.AssignedAt,
                RenewalStatus = LicenceRules.RenewalStatus(r.ExpiresAt, today)
            })
            .ToList()));
    }

    private Task<bool> HoldsActiveSeatAsync(Guid tenantId, int licenceId, AssignLicenceSeatDto dto, CancellationToken ct) =>
        db.LicenceSeatAssignments.AnyAsync(s =>
            s.TenantId == tenantId
            && s.SoftwareLicenceId == licenceId
            && s.ReleasedAt == null
            && (dto.AssetId == null ? s.UserId == dto.UserId : s.AssetId == dto.AssetId), ct);

    /// <summary>
    /// The only response that carries a full key. A POST so the key does not end up in browser
    /// history or an intermediary's access log, and the audit row is saved before the key is
    /// returned: if the record of who saw it cannot be written, nobody sees it.
    /// </summary>
    [HttpPost("{id:int}/key/reveal")]
    [Authorize(Policy = "CanRevealLicenceKeys")]
    public async Task<ActionResult<ApiResponse<RevealedLicenceKeyDto>>> RevealKey(int id, CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<RevealedLicenceKeyDto>.Fail("Select an organisation first."));

        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Id == id)
            .Select(l => new { l.LicenceKey })
            .FirstOrDefaultAsync(ct);

        if (licence is null)
            return NotFound(ApiResponse<RevealedLicenceKeyDto>.Fail("Licence not found."));
        if (string.IsNullOrEmpty(licence.LicenceKey))
            return NotFound(ApiResponse<RevealedLicenceKeyDto>.Fail("No key is recorded for this licence."));

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            EntityType = nameof(SoftwareLicence),
            EntityId = id.ToString(),
            Action = AuditActions.LicenceKeyRevealed,
            UserId = CurrentUserId,
            Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<RevealedLicenceKeyDto>.Ok(new RevealedLicenceKeyDto { LicenceKey = licence.LicenceKey }));
    }

    /// <summary>
    /// A bare count for the nav badge, in the shape api/warrantyalerts/count already returns. With
    /// no organisation selected there is nothing to renew, so it is zero rather than a refusal the
    /// layout would have to handle on every page.
    /// </summary>
    [HttpGet("renewals/count")]
    public async Task<ActionResult<int>> GetRenewalCount(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return Ok(0);

        var expiries = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive && l.ExpiresAt != null)
            .Select(l => l.ExpiresAt)
            .ToListAsync(ct);

        var today = DateTime.UtcNow;
        return Ok(expiries.Count(e => LicenceRules.NeedsRenewalAttention(LicenceRules.RenewalStatus(e, today))));
    }

    private async Task<string?> ValidateAsync(
        Guid tenantId, UpsertSoftwareLicenceDto dto, SoftwareLicence? existing, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "A licence needs a name.";

        if (!LicenceModels.IsValid(dto.LicenceModel))
            return "Choose whether the licence is counted per user or per device.";

        if (await db.SoftwareLicences.AnyAsync(
                l => l.TenantId == tenantId && l.Name == name && (existing == null || l.Id != existing.Id), ct))
            return $"A licence named '{name}' already exists.";

        // An unchanged supplier is left alone even if it has since been retired - editing a
        // licence's notes must not force someone to pick a new reseller.
        if (dto.SupplierId is { } supplierId && supplierId != existing?.SupplierId)
        {
            var supplier = await db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == supplierId && s.TenantId == tenantId, ct);
            if (supplier is null)
                return "Supplier not found.";
            if (!supplier.IsActive)
                return $"Supplier '{supplier.Name}' is inactive.";
        }

        if (existing is not null
            && existing.LicenceModel != dto.LicenceModel
            && await db.LicenceSeatAssignments.AnyAsync(
                s => s.TenantId == tenantId && s.SoftwareLicenceId == existing.Id, ct))
            return "The licence model cannot change once seats have been assigned - every seat would point at the wrong kind of holder.";

        return null;
    }

    private async Task<HashSet<int>> LicencesWithSeatsAsync(Guid tenantId, CancellationToken ct) =>
        (await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.SoftwareLicenceId)
            .Distinct()
            .ToListAsync(ct))
        .ToHashSet();

    /// <summary>
    /// The detail every write returns, so a screen that has just changed a licence shows what the
    /// database now holds rather than what it sent.
    /// </summary>
    private async Task<SoftwareLicenceDetailDto?> DetailAsync(Guid tenantId, int id, CancellationToken ct)
    {
        var licence = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Id == id)
            .Include(l => l.Supplier)
            .FirstOrDefaultAsync(ct);
        if (licence is null) return null;

        var entitlements = await db.LicenceEntitlements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.SoftwareLicenceId == id)
            .Select(e => new
            {
                e.Id, e.SeatsAdded, e.Cost, e.Currency, e.ExchangeRate, e.EntitlementDate,
                e.ExpiresAtAfter, e.CreatedByUserId, e.Notes,
                PurchaseOrderId = e.GoodsReceiptLine == null ? (int?)null : e.GoodsReceiptLine.GoodsReceipt!.PurchaseOrderId,
                PoNumber = e.GoodsReceiptLine == null ? (int?)null : e.GoodsReceiptLine.GoodsReceipt!.PurchaseOrder!.PoNumber
            })
            .ToListAsync(ct);

        var seats = await db.LicenceSeatAssignments
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SoftwareLicenceId == id)
            .Select(s => new
            {
                s.Id, s.UserId, s.AssetId, s.AssignedAt, s.ReleasedAt, s.AssignedByUserId, s.Notes,
                UserName = s.User == null ? null : s.User.FullName,
                UserIsActive = s.User == null ? (bool?)null : s.User.IsActive,
                AssetTag = s.Asset == null ? null : s.Asset.AssetTag,
                AssetName = s.Asset == null ? null : s.Asset.Name,
                AssetStatus = s.Asset == null ? null : s.Asset.Status
            })
            .ToListAsync(ct);

        var names = await UserNamesAsync(
            entitlements.Select(e => e.CreatedByUserId).Concat(seats.Select(s => s.AssignedByUserId)), ct);
        var licenceUsage = (await usage.ReadAsync(tenantId, id, ct)).GetValueOrDefault(id, LicenceUsage.None);
        var today = DateTime.UtcNow;

        return new SoftwareLicenceDetailDto
        {
            Licence = Map(licence, licenceUsage, modelLocked: seats.Count > 0, today),
            SpendInPesos = licenceUsage.SpendInPesos,
            Entitlements = [.. entitlements
                .OrderByDescending(e => e.EntitlementDate)
                .ThenByDescending(e => e.Id)
                .Select(e => new LicenceEntitlementDto
                {
                    Id = e.Id,
                    SeatsAdded = e.SeatsAdded,
                    Cost = e.Cost,
                    Currency = e.Currency,
                    ExchangeRate = e.ExchangeRate,
                    CostInPesos = e.Cost * e.ExchangeRate,
                    EntitlementDate = e.EntitlementDate,
                    ExpiresAtAfter = e.ExpiresAtAfter,
                    PurchaseOrderId = e.PurchaseOrderId,
                    PurchaseOrderReference = e.PoNumber is { } n ? $"PO-{n:D4}" : null,
                    CreatedByName = names.GetValueOrDefault(e.CreatedByUserId),
                    Notes = e.Notes
                })],
            Seats = [.. seats
                .OrderBy(s => s.ReleasedAt is null ? 0 : 1)
                .ThenByDescending(s => s.ReleasedAt ?? s.AssignedAt)
                .Select(s => new LicenceSeatDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    UserName = s.UserName,
                    AssetId = s.AssetId,
                    AssetTag = s.AssetTag,
                    AssetName = s.AssetName,
                    AssetStatus = s.AssetStatus,
                    AssignedAt = s.AssignedAt,
                    ReleasedAt = s.ReleasedAt,
                    AssignedByName = names.GetValueOrDefault(s.AssignedByUserId),
                    Notes = s.Notes,
                    IsActive = s.ReleasedAt is null,
                    IsReclaimable = s.ReleasedAt is null && LicenceRules.IsReclaimable(s.UserIsActive, s.AssetStatus)
                })]
        };
    }

    /// <summary>
    /// ApplicationUser has no query filter, so this lookup is not tenant-scoped. What bounds it is
    /// the ids: they come off this tenant's own entitlement and seat rows.
    /// </summary>
    private async Task<Dictionary<string, string>> UserNamesAsync(IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private static SoftwareLicenceDto Map(SoftwareLicence l, LicenceUsage u, bool modelLocked, DateTime today) => new()
    {
        Id = l.Id,
        Name = l.Name,
        Publisher = l.Publisher,
        SupplierId = l.SupplierId,
        SupplierName = l.Supplier?.Name,
        LicenceModel = l.LicenceModel,
        MaskedKey = LicenceRules.Mask(l.LicenceKey),
        ExpiresAt = l.ExpiresAt,
        RenewalStatus = LicenceRules.RenewalStatus(l.ExpiresAt, today),
        DaysUntilExpiry = LicenceRules.DaysUntilExpiry(l.ExpiresAt, today),
        Notes = l.Notes,
        IsActive = l.IsActive,
        SeatsOwned = u.SeatsOwned,
        SeatsAssigned = u.SeatsAssigned,
        OverAssigned = u.OverAssigned,
        Reclaimable = u.Reclaimable,
        ModelLocked = modelLocked
    };

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
