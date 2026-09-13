using System.Text;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using static AssetDesk.Api.Tests.LicenceTestKit;

namespace AssetDesk.Api.Tests;

public class LicenceComplianceReportTests
{
    private static LicenceReportsController ReportFor(AppDbContext db, ITenantProvider tenants) =>
        new(db, tenants, new LicenceUsageReader(db), new PdfReportService());

    private static LicenceComplianceSummaryDto Summary(ActionResult<ApiResponse<LicenceComplianceSummaryDto>> result) =>
        Assert.IsType<ApiResponse<LicenceComplianceSummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;

    /// <summary>Splits one CSV record, honouring quoted fields - enough to prove columns line up.</summary>
    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    [Fact]
    public async Task Spend_is_totalled_in_pesos_across_currencies()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 10, cost: 1200m, currency: Currencies.USD, rate: 58.20m);
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 10000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(79840m, summary.TotalSpendInPesos);
            Assert.Equal(79840m, summary.Rows.Single().SpendInPesos);
            Assert.Equal(15, summary.TotalSeatsOwned);
            Assert.Equal(Currencies.PHP, summary.PrimaryCurrency);
        }
    }

    [Fact]
    public async Task Deactivated_licences_are_left_out()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await SeedLicenceAsync(db, tenantId, "In use");
            var retired = await SeedLicenceAsync(db, tenantId, "Retired", isActive: false);
            await SeedEntitlementAsync(db, tenantId, retired.Id, seats: 100, cost: 500000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal("In use", Assert.Single(summary.Rows).Name);
            Assert.Equal(0, summary.TotalSeatsOwned);
            Assert.Equal(0m, summary.TotalSpendInPesos);
        }
    }

    [Fact]
    public async Task Over_assigned_and_reclaimable_seats_are_totalled()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            await TestDb.SeedUserAsync(db, tenantId, "user-2", "Jose Reyes");
            var departed = await TestDb.SeedUserAsync(db, tenantId, "user-3", "Ana Cruz");

            var tight = await SeedLicenceAsync(db, tenantId, "Tight");
            await SeedEntitlementAsync(db, tenantId, tight.Id, seats: 2);
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-1");
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-2");
            await SeedUserSeatAsync(db, tenantId, tight.Id, "user-3");

            var roomy = await SeedLicenceAsync(db, tenantId, "Roomy");
            await SeedEntitlementAsync(db, tenantId, roomy.Id, seats: 10);
            await SeedUserSeatAsync(db, tenantId, roomy.Id, "user-1");

            departed.IsActive = false;
            await db.SaveChangesAsync();

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(12, summary.TotalSeatsOwned);
            Assert.Equal(4, summary.TotalSeatsAssigned);
            Assert.Equal(1, summary.OverAssignedLicenceCount);
            Assert.Equal(1, summary.OverAssignedSeats);
            Assert.Equal(1, summary.ReclaimableSeats);
        }
    }

    [Fact]
    public async Task Renewals_due_and_expired_are_counted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.Date;
            await SeedLicenceAsync(db, tenantId, "Expired", expiresAt: today.AddDays(-1));
            await SeedLicenceAsync(db, tenantId, "Due soon", expiresAt: today.AddDays(45));
            await SeedLicenceAsync(db, tenantId, "Due later", expiresAt: today.AddDays(90));
            await SeedLicenceAsync(db, tenantId, "Active", expiresAt: today.AddDays(365));
            await SeedLicenceAsync(db, tenantId, "Perpetual");

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantId)).Get(default));

            Assert.Equal(2, summary.RenewalsDue);
            Assert.Equal(1, summary.RenewalsExpired);
            Assert.Equal(LicenceRenewalStatuses.Perpetual, summary.Rows.Single(r => r.Name == "Perpetual").RenewalStatus);
        }
    }

    [Fact]
    public async Task The_csv_has_one_aligned_row_per_licence_and_never_the_key()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, "Acrobat Pro, Team", key: "SECRET-KEY-VALUE-1234");
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 12500m);

            var file = Assert.IsType<FileContentResult>(await ReportFor(db, new FakeTenantProvider(tenantId)).Export(default));
            var text = Encoding.UTF8.GetString(file.FileContents);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

            Assert.Equal("text/csv", file.ContentType);
            Assert.DoesNotContain("SECRET-KEY-VALUE-1234", text);
            Assert.Equal(2, lines.Count);

            var header = Fields(lines[0]);
            var row = Fields(lines[1]);
            Assert.Equal(header.Count, row.Count);
            Assert.Equal("Acrobat Pro, Team", row[header.IndexOf("Licence")]);
            Assert.Equal("12500.00", row[header.IndexOf("Spend (PHP)")]);
        }
    }

    [Fact]
    public async Task The_pdf_is_a_rendered_pdf()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = await SeedLicenceAsync(db, tenantId, expiresAt: DateTime.UtcNow.Date.AddDays(10));
            await SeedEntitlementAsync(db, tenantId, licence.Id, seats: 5, cost: 12500m);

            var file = Assert.IsType<FileContentResult>(await ReportFor(db, new FakeTenantProvider(tenantId)).Pdf(default));

            Assert.Equal("application/pdf", file.ContentType);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(file.FileContents, 0, 5));
        }
    }

    [Fact]
    public async Task Another_tenants_licences_are_not_reported()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            var theirs = await SeedLicenceAsync(db, tenantB, "Theirs");
            await SeedEntitlementAsync(db, tenantB, theirs.Id, seats: 50, cost: 35000m);

            var summary = Summary(await ReportFor(db, new FakeTenantProvider(tenantA, isSuperAdmin: true)).Get(default));

            Assert.Empty(summary.Rows);
            Assert.Equal(0m, summary.TotalSpendInPesos);
        }
    }

    [Fact]
    public async Task A_caller_with_no_organisation_is_refused()
    {
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            var result = await ReportFor(db, new FakeTenantProvider(null, isSuperAdmin: true)).Get(default);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }
}
