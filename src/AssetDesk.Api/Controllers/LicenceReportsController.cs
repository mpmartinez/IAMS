using System.Globalization;
using System.Text;
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
/// Sits under api/reports beside the other reports and shares their permission, but is its own
/// controller: ReportsController reads through the global query filter and takes no tenant
/// provider, while every licence read scopes to the caller's organisation explicitly. One summary
/// builder feeds all three formats, as BuildDepreciationSummaryAsync does for depreciation.
/// </summary>
[ApiController]
[Route("api/reports/licences")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = "CanViewReports")]
public class LicenceReportsController(
    AppDbContext db,
    ITenantProvider tenantProvider,
    ILicenceUsageReader usage,
    IPdfReportService pdf) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<LicenceComplianceSummaryDto>>> Get(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<LicenceComplianceSummaryDto>.Fail("Select an organisation first."));

        return Ok(ApiResponse<LicenceComplianceSummaryDto>.Ok(await BuildSummaryAsync(tenantId, ct)));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var summary = await BuildSummaryAsync(tenantId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Licence,Publisher,Model,Seats Owned,Seats Assigned,Over-Assigned,Reclaimable,Expires,Renewal Status,Spend (PHP)");

        foreach (var r in summary.Rows)
        {
            sb.AppendLine(string.Join(',',
                CsvFormat.Escape(r.Name),
                CsvFormat.Escape(r.Publisher),
                CsvFormat.Escape(AssetDesk.Shared.LicenceModels.Label(r.LicenceModel)),
                r.SeatsOwned.ToString(CultureInfo.InvariantCulture),
                r.SeatsAssigned.ToString(CultureInfo.InvariantCulture),
                r.OverAssigned.ToString(CultureInfo.InvariantCulture),
                r.Reclaimable.ToString(CultureInfo.InvariantCulture),
                r.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                CsvFormat.Escape(r.RenewalStatus),
                r.SpendInPesos.ToString("F2", CultureInfo.InvariantCulture)));
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
            $"Licence Compliance {summary.AsOf:yyyy-MM-dd}.csv");
    }

    [HttpGet("pdf")]
    public async Task<IActionResult> Pdf(CancellationToken ct)
    {
        if (tenantProvider.GetCurrentTenantId() is not { } tenantId)
            return BadRequest(ApiResponse<object>.Fail("Select an organisation first."));

        var summary = await BuildSummaryAsync(tenantId, ct);
        return File(pdf.BuildLicenceCompliancePdf(summary), "application/pdf",
            $"Licence Compliance {summary.AsOf:yyyy-MM-dd}.pdf");
    }

    /// <summary>
    /// Deactivated licences are left out, as Retired and Lost assets are left out of the asset
    /// reports: a compliance figure that counts software nobody uses any more overstates exposure.
    /// </summary>
    private async Task<LicenceComplianceSummaryDto> BuildSummaryAsync(Guid tenantId, CancellationToken ct)
    {
        var licences = await db.SoftwareLicences
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive)
            .OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.Publisher, l.LicenceModel, l.ExpiresAt })
            .ToListAsync(ct);

        var usageByLicence = await usage.ReadAsync(tenantId, ct: ct);
        var asOf = DateTime.UtcNow;

        var rows = licences.Select(l =>
        {
            var u = usageByLicence.GetValueOrDefault(l.Id, LicenceUsage.None);
            return new LicenceComplianceRow
            {
                LicenceId = l.Id,
                Name = l.Name,
                Publisher = l.Publisher,
                LicenceModel = l.LicenceModel,
                SeatsOwned = u.SeatsOwned,
                SeatsAssigned = u.SeatsAssigned,
                OverAssigned = u.OverAssigned,
                Reclaimable = u.Reclaimable,
                ExpiresAt = l.ExpiresAt,
                RenewalStatus = LicenceRules.RenewalStatus(l.ExpiresAt, asOf),
                SpendInPesos = u.SpendInPesos
            };
        }).ToList();

        return new LicenceComplianceSummaryDto
        {
            LicenceCount = rows.Count,
            TotalSeatsOwned = rows.Sum(r => r.SeatsOwned),
            TotalSeatsAssigned = rows.Sum(r => r.SeatsAssigned),
            OverAssignedLicenceCount = rows.Count(r => r.OverAssigned > 0),
            OverAssignedSeats = rows.Sum(r => r.OverAssigned),
            ReclaimableSeats = rows.Sum(r => r.Reclaimable),
            RenewalsDue = rows.Count(r => r.RenewalStatus == AssetDesk.Shared.LicenceRenewalStatuses.Due),
            RenewalsExpired = rows.Count(r => r.RenewalStatus == AssetDesk.Shared.LicenceRenewalStatuses.Expired),
            TotalSpendInPesos = rows.Sum(r => r.SpendInPesos),
            PrimaryCurrency = Currencies.PHP,
            AsOf = asOf,
            Rows = rows
        };
    }
}
