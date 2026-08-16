using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketServiceCreateTests
{
    private static TicketService Build(IAMS.Api.Data.AppDbContext db, Guid tenantId) =>
        new(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));

    [Fact]
    public async Task Creates_a_new_ticket_with_an_allocated_number()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.Incident, "Printer jams", "Every second page", TicketPriority.High, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(1, result.Value!.TicketNumber);
            Assert.Equal(TicketStatus.New, result.Value.Status);
            Assert.Equal(tenantId, result.Value.TenantId);
        }
    }

    [Fact]
    public async Task Rejects_an_unknown_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                "Escalation", "Bad type", null, TicketPriority.Low, null, "emp-1", default);

            Assert.False(result.Success);
            Assert.Contains("type", result.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_an_asset_from_another_tenant()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, mine, "emp-1", "J. Dela Cruz");
            var foreign = await TestDb.SeedAssetAsync(db, theirs, "THEIRS-1");
            var service = Build(db, mine);

            var result = await service.CreateAsync(
                TicketTypes.Incident, "Not mine", null, TicketPriority.Low, foreign.Id, "emp-1", default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Security_events_default_to_high_priority()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.SecurityEvent, "Phishing email", null, TicketPriority.Low, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(TicketPriority.High, result.Value!.Priority);
        }
    }

    [Fact]
    public async Task Lists_and_filters_by_status_and_type()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Request, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var (all, total) = await service.ListAsync(new TicketQuery(null, null, null, null, null, null), default);
            Assert.Equal(2, total);
            Assert.Equal(2, all.Count);

            var (requests, requestTotal) = await service.ListAsync(
                new TicketQuery(TicketTypes.Request, null, null, null, null, null), default);
            Assert.Equal(1, requestTotal);
            Assert.Equal("Two", requests[0].Title);
        }
    }

    [Fact]
    public async Task Summary_counts_open_and_unassigned()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Incident, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var summary = await service.GetSummaryAsync(default);

            Assert.Equal(2, summary.Open);
            Assert.Equal(2, summary.Unassigned);
            Assert.Equal(0, summary.InProgress);
            Assert.Equal(0, summary.Overdue);
        }
    }
}
