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
    ILogger<PurchaseOrdersController> logger,
    IPdfReportService pdf) : ControllerBase
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

        // The receipts are reached through an order already filtered by tenant, which is what
        // makes them safe to include: GoodsReceiptLine carries no filter of its own, so a
        // receipt line is only ever as well isolated as the order it hangs off.
        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .Include(p => p.Receipts)
                .ThenInclude(r => r.Lines)
            .FirstOrDefaultAsync();

        return order is null
            ? NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderDto>.Ok(await MapDetailAsync(order)));
    }

    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> GetPdf(int id)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var order = await db.PurchaseOrders
            .Where(p => p.TenantId == tenantId && p.Id == id)
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
            .FirstOrDefaultAsync();

        if (order is null)
            return NotFound(ApiResponse<object>.Fail("Purchase order not found."));

        var dto = Map(order);
        return File(pdf.BuildPurchaseOrderPdf(dto), "application/pdf", $"{dto.Reference}.pdf");
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

        // Deactivation is the only way a supplier is retired - Delete sets IsActive rather than
        // removing the row, because orders reference it. Refusing here rather than only hiding
        // inactive suppliers in the picker is what makes the retirement mean something: an id
        // from a stale tab or a scripted client would otherwise produce a fully valid, numbered,
        // receivable order against a supplier somebody retired for cause. Named separately from
        // "not found" so an operator can tell a wrong id from a retired supplier.
        if (!supplier.IsActive)
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(
                $"Supplier '{supplier.Name}' is inactive and cannot be ordered from."));

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

        // Resolve through an explicit tenant filter so a caller naming another organisation's
        // order gets a 404 with a message rather than a bare refusal from the service. The
        // global query filter is not enough on its own - it admits every tenant's orders for a
        // super admin - and this is friendliness, not the invariant: ReceiveAsync takes the
        // tenant itself and filters on it, so the guarantee does not rest on this check.
        var exists = await db.PurchaseOrders
            .AnyAsync(p => p.Id == id && p.TenantId == tenantId);
        if (!exists)
            return NotFound(ApiResponse<PurchaseOrderDto>.Fail("Purchase order not found."));

        var actingUserId = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var result = await receipts.ReceiveAsync(tenantId, id, dto, actingUserId);

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

        // Ordered is only ever reached through Send, and Send is where an order stops being a
        // private draft and becomes a commitment: only then is it receivable, and only then does
        // the PDF go out bearing the supplier's name. Create's check is not enough on its own,
        // because a draft raised while the supplier was active can be sent days after finance
        // retired it. Cancel is deliberately not gated the same way - taking an order to a
        // retired supplier off the books is exactly what should stay possible.
        if (to == PurchaseOrderStatus.Ordered && order.Supplier is { IsActive: false })
            return BadRequest(ApiResponse<PurchaseOrderDto>.Fail(
                $"Supplier '{order.Supplier.Name}' is inactive and cannot be ordered from."));

        order.Status = to;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ApiResponse<PurchaseOrderDto>.Ok(Map(order)));
    }

    /// <summary>
    /// The detail read, and only the detail read. Map is deliberately left without the receipts:
    /// the list screen renders nothing from them, so including them there would be a join per
    /// row for data nobody looks at. A caller that needs the delivery history asks for one order.
    ///
    /// Expects Receipts and their Lines to be loaded already - see GetById.
    /// </summary>
    private async Task<PurchaseOrderDto> MapDetailAsync(PurchaseOrder p)
    {
        var receiverNames = await ResolveUserNamesAsync(p.Receipts.Select(r => r.ReceivedByUserId));

        return Map(p) with
        {
            // Newest first: the question a delivery history answers is almost always "what
            // arrived last, and at what rate".
            Receipts = [.. p.Receipts
                .OrderByDescending(r => r.ReceiptDate)
                .ThenByDescending(r => r.Id)
                .Select(r => new GoodsReceiptDto
                {
                    Id = r.Id,
                    ReceiptDate = r.ReceiptDate,
                    ExchangeRate = r.ExchangeRate,
                    ReceivedByName = receiverNames.GetValueOrDefault(r.ReceivedByUserId),
                    Notes = r.Notes,
                    TotalUnits = r.Lines.Sum(l => l.QuantityReceived),
                    Lines = [.. r.Lines.Select(l => new GoodsReceiptLineDto
                    {
                        Id = l.Id,
                        PurchaseOrderLineId = l.PurchaseOrderLineId,
                        DeviceType = p.Lines.First(o => o.Id == l.PurchaseOrderLineId).DeviceType,
                        Description = p.Lines.First(o => o.Id == l.PurchaseOrderLineId).Description,
                        QuantityReceived = l.QuantityReceived
                    })]
                })]
        };
    }

    /// <summary>
    /// GoodsReceipt names its receiver by Identity id and has no navigation to the user - the
    /// receipt has to outlive the account that recorded it - so the display names are a separate
    /// lookup, and an id that no longer resolves is simply absent, leaving the name null rather
    /// than showing a raw guid.
    ///
    /// ApplicationUser has no query filter, so this lookup is not tenant-scoped. What bounds it is
    /// the ids: they come off the caller's own tenant-filtered order. The one cross-tenant name it
    /// can surface is a super admin from another organisation who recorded a delivery against
    /// that order.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveUserNamesAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
    }

    /// <summary>
    /// The shared mapper, used by the list, the PDF and (through MapDetailAsync) the detail read.
    /// It leaves Receipts empty on purpose; only MapDetailAsync fills them.
    /// </summary>
    private static PurchaseOrderDto Map(PurchaseOrder p) => new()
    {
        Id = p.Id,
        PoNumber = p.PoNumber,
        Reference = $"PO-{p.PoNumber:D4}",
        SupplierId = p.SupplierId,
        SupplierName = p.Supplier?.Name,
        // Unresolved reads as retired: every caller here loads the supplier, and a screen that
        // offers Send on a supplier it could not see would only be offering a refusal.
        SupplierIsActive = p.Supplier?.IsActive ?? false,
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
