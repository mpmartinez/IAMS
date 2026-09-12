using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

/// <summary>
/// The per-device-type depreciation policy. Upsert rather than create/update: there is at most
/// one policy per device type per tenant, so the device type is the identity a caller knows and
/// a separate create-vs-update decision would only be a way to get it wrong.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanManageDepreciation")]
public class DepreciationPoliciesController(AppDbContext db, ILookupService lookups) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DepreciationPolicyDto>>>> GetAll()
    {
        var policies = await db.DepreciationPolicies
            .OrderBy(p => p.DeviceType)
            .Select(p => new DepreciationPolicyDto
            {
                Id = p.Id,
                DeviceType = p.DeviceType,
                UsefulLifeMonths = p.UsefulLifeMonths,
                ResidualPercent = p.ResidualPercent
            })
            .ToListAsync();

        return Ok(ApiResponse<List<DepreciationPolicyDto>>.Ok(policies));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<DepreciationPolicyDto>>> Upsert(UpsertDepreciationPolicyDto dto)
    {
        if (dto.UsefulLifeMonths <= 0)
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                "Useful life must be at least 1 month."));

        if (dto.ResidualPercent is < 0m or > 100m)
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                "Residual must be between 0 and 100 percent."));

        // Device types are admin-editable, so validate against the live lookup rather than the
        // DeviceTypes constant - the same call AssetsController makes.
        if (!await lookups.IsActiveValueAsync(LookupTypes.DeviceType, dto.DeviceType))
            return BadRequest(ApiResponse<DepreciationPolicyDto>.Fail(
                $"'{dto.DeviceType}' is not an active device type."));

        var policy = await db.DepreciationPolicies
            .FirstOrDefaultAsync(p => p.DeviceType == dto.DeviceType);

        if (policy is null)
        {
            policy = new DepreciationPolicy { DeviceType = dto.DeviceType };
            db.DepreciationPolicies.Add(policy);
        }
        else
        {
            policy.UpdatedAt = DateTime.UtcNow;
        }

        policy.UsefulLifeMonths = dto.UsefulLifeMonths;
        policy.ResidualPercent = dto.ResidualPercent;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<DepreciationPolicyDto>.Ok(new DepreciationPolicyDto
        {
            Id = policy.Id,
            DeviceType = policy.DeviceType,
            UsefulLifeMonths = policy.UsefulLifeMonths,
            ResidualPercent = policy.ResidualPercent
        }));
    }

    [HttpDelete("{deviceType}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(string deviceType)
    {
        var policy = await db.DepreciationPolicies
            .FirstOrDefaultAsync(p => p.DeviceType == deviceType);

        if (policy is null)
            return NotFound(ApiResponse<object>.Fail($"No depreciation policy for '{deviceType}'."));

        db.DepreciationPolicies.Remove(policy);
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new object()));
    }
}
