using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class SuppliersController(AppDbContext db, ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>
    /// Every query filters on the tenant explicitly rather than trusting the global query
    /// filter, which has an IsSuperAdmin() bypass. The depreciation feature shipped a Critical
    /// for exactly this: a super-admin caller could rewrite another organisation's rows because
    /// the lookup key was not unique across tenants.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<SupplierDto>>>> GetAll()
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<SupplierDto>>.Fail("Select an organisation first."));

        var suppliers = await db.Suppliers
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .Select(s => Map(s))
            .ToListAsync();

        return Ok(ApiResponse<List<SupplierDto>>.Ok(suppliers));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SupplierDto>>> Create(UpsertSupplierDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SupplierDto>.Fail("Select an organisation first."));

        if (await db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.Name == dto.Name))
            return BadRequest(ApiResponse<SupplierDto>.Fail($"A supplier named '{dto.Name}' already exists."));

        var supplier = new Supplier
        {
            TenantId = tenantId,
            Name = dto.Name,
            ContactName = dto.ContactName,
            Email = dto.Email,
            Phone = dto.Phone,
            Address = dto.Address,
            Notes = dto.Notes
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), ApiResponse<SupplierDto>.Ok(Map(supplier)));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<SupplierDto>>> Update(int id, UpsertSupplierDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<SupplierDto>.Fail("Select an organisation first."));

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (supplier is null)
            return NotFound(ApiResponse<SupplierDto>.Fail("Supplier not found."));

        if (await db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.Name == dto.Name && s.Id != id))
            return BadRequest(ApiResponse<SupplierDto>.Fail($"A supplier named '{dto.Name}' already exists."));

        supplier.Name = dto.Name;
        supplier.ContactName = dto.ContactName;
        supplier.Email = dto.Email;
        supplier.Phone = dto.Phone;
        supplier.Address = dto.Address;
        supplier.Notes = dto.Notes;
        supplier.IsActive = dto.IsActive;
        supplier.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(ApiResponse<SupplierDto>.Ok(Map(supplier)));
    }

    /// <summary>Deactivates rather than deletes - purchase orders reference suppliers.</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);

        if (supplier is null)
            return NotFound(ApiResponse<object>.Fail("Supplier not found."));

        supplier.IsActive = false;
        supplier.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new object()));
    }

    private static SupplierDto Map(Supplier s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        ContactName = s.ContactName,
        Email = s.Email,
        Phone = s.Phone,
        Address = s.Address,
        Notes = s.Notes,
        IsActive = s.IsActive
    };
}
