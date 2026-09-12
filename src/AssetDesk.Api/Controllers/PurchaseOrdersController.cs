using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AssetDesk.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewProcurement")]
public class PurchaseOrdersController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    IPurchaseOrderNumberAllocator numbers,
    ILookupService lookups,
    IGoodsReceiptService receipts,
    ILogger<PurchaseOrdersController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PurchaseOrderDto>>>> GetAll()
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<List<PurchaseOrderDto>>.Fail("Select an organisation first."));

        var orders = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .OrderByDescending(p => p.PoNumber)
            .ToListAsync();

        return Ok(ApiResponse<List<PurchaseOrderDto>>.Ok(orders.Select(Map).ToList()));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> GetById(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        return order is null
            ? NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    [HttpPost]
    [Authorize(Policy = "CanManageProcurement")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Create(CreatePurchaseOrderDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        if (dto.Lines.Count == 0)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("A purchase order needs at least one line."));

        foreach (var line in dto.Lines)
        {
            if (line.Quantity <= 0)
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Every line needs a quantity of at least 1."));
            if (line.UnitPrice < 0)
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("A unit price cannot be negative."));
            if (!await lookups.IsActiveValueAsync(LookupTypes.DeviceType, line.DeviceType))
                return BadRequest(ApiResponse<PurchaseOrderDto>.Fail($"'{line.DeviceType}' is not an active device type."));
        }

        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, dto.Currency))
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail($"'{dto.Currency}' is not a valid currency."));

        // Explicit tenant filter, not the global one - a super admin's filter admits every
        // tenant's suppliers, and ordering from another organisation's supplier would be silent.
        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && s.TenantId == tenantId);
        if (supplier is null)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Supplier not found."));

        var order = new PurchaseOrder
        {
            TenantId = tenantId,
            PoNumber = await numbers.NextAsync(tenantId),
            SupplierId = supplier.Id,
            Currency = dto.Currency,
            Status = PurchaseOrderStatus.Draft,
            OrderDate = dto.OrderDate,
            ExpectedDate = dto.ExpectedDate,
            Notes = dto.Notes,
            CreatedByUserId = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""
        };

        foreach (var line in dto.Lines)
        {
            order.Lines.Add(new PurchaseOrderLine
            {
                DeviceType = line.DeviceType,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            });
        }

        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        await db.Entry(order).Reference(o => o.Supplier).LoadAsync();
        return CreatedAtAction(nameof(GetById), new { id = order.Id },
            ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    [HttpPost("{id:int}/send")]
    [Authorize(Policy = "CanManageProcurement")]
    public Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Send(int id) =>
        TransitionAsync(id, PurchaseOrderStatus.Ordered);

    [HttpPost("{id:int}/cancel")]
    [Authorize(Policy = "CanManageProcurement")]
    public Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Cancel(int id) =>
        TransitionAsync(id, PurchaseOrderStatus.Cancelled);

    [HttpPost("{id:int}/receive")]
    [Authorize(Policy = "CanManageProcurement")]
    public async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> Receive(int id, ReceiveGoodsDto dto)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        // Resolve through an explicit tenant filter before handing the id to the service, so a
        // caller cannot receive against another organisation's order. The global query filter is
        // not enough on its own - it admits every tenant's orders for a super admin.
        var exists = await db.PurchaseOrders
            .AnyAsync(p => p.Id == id && p.TenantId == tenantId);
        if (!exists)
            return NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."));

        var actingUserId = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var result = await receipts.ReceiveAsync(id, dto, actingUserId);

        if (!result.Success)
        {
            // A refused delivery is worth a trace: it means what arrived disagreed with what the
            // order says, which someone standing at the loading bay will have to reconcile.
            logger.LogWarning(
                "Receiving against purchase order {PurchaseOrderId} was refused: {Reason}",
                id, result.Message);

            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(result.Message ?? "Receiving failed."));
        }

        return await GetById(id);
    }

    /// <summary>
    /// The only statuses a user may set directly. PartiallyReceived and Received come from the
    /// quantities via PurchaseOrderWorkflow.StatusFor, never from here.
    /// </summary>
    private async Task<ActionResult<ApiResponse<PurchaseOrderDto>>> TransitionAsync(int id, string to)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        if (order is null)
            return NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."));

        if (!PurchaseOrderWorkflow.CanTransition(order.Status, to))
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(
                $"A {order.Status} purchase order cannot become {to}."));

        order.Status = to;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    private static PurchaseOrderDto Map(PurchaseOrder p) => new()
    {
        Id = p.Id,
        PoNumber = p.PoNumber,
        Reference = $"PO-{p.PoNumber:D4}",
        SupplierId = p.SupplierId,
        SupplierName = p.Supplier?.Name,
        Currency = p.Currency,
        Status = p.Status,
        OrderDate = p.OrderDate,
        ExpectedDate = p.ExpectedDate,
        Notes = p.Notes,
        OrderTotal = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
        Lines = p.Lines.Select(l => new PurchaseOrderLineDto
        {
            Id = l.Id,
            DeviceType = l.DeviceType,
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            ReceivedQuantity = l.ReceivedQuantity,
            OutstandingQuantity = l.Quantity - l.ReceivedQuantity,
            LineTotal = l.Quantity * l.UnitPrice
        }).ToList()
    };
}
