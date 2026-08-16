using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketServiceWorkflowTests
{
    private static async Task<(TicketService Service, Ticket Ticket)> SetupAsync(AppDbContext db, Guid tenantId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
        var created = await service.CreateAsync(
            TicketTypes.Incident, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

        return (service, created.Value!);
    }

    [Fact]
    public async Task Assigning_moves_the_ticket_to_Assigned_and_stamps_the_time()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.AssignAsync(ticket.Id, "staff-1", default);

            Assert.True(result.Success);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Assigned, saved.Status);
            Assert.Equal("staff-1", saved.AssignedToUserId);
            Assert.NotNull(saved.AssignedAt);
        }
    }

    [Fact]
    public async Task Rejects_an_invalid_transition_and_leaves_state_unchanged()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.ChangeStatusAsync(ticket.Id, TicketStatus.Closed, default);

            Assert.False(result.Success);
            Assert.Contains("New", result.Message!);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.New, saved.Status);
        }
    }

    [Fact]
    public async Task Starting_work_stamps_StartedAt_once()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var first = (await db.Tickets.SingleAsync(t => t.Id == ticket.Id)).StartedAt;
            Assert.NotNull(first);

            await service.ChangeStatusAsync(ticket.Id, TicketStatus.OnHold, default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var second = (await db.Tickets.SingleAsync(t => t.Id == ticket.Id)).StartedAt;
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public async Task Resolving_requires_a_resolution_and_stamps_ResolvedAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var blank = await service.ResolveAsync(ticket.Id, "   ", default);
            Assert.False(blank.Success);

            var ok = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);
            Assert.True(ok.Success);

            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Resolved, saved.Status);
            Assert.Equal("Replaced the fuser.", saved.Resolution);
            Assert.NotNull(saved.ResolvedAt);
        }
    }

    [Fact]
    public async Task Closing_a_resolved_ticket_stamps_ClosedAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);
            await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);

            var result = await service.ChangeStatusAsync(ticket.Id, TicketStatus.Closed, default);

            Assert.True(result.Success);
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.NotNull(saved.ClosedAt);
        }
    }

    [Fact]
    public async Task Comments_record_their_author_and_visibility()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var publicComment = await service.AddCommentAsync(ticket.Id, "staff-1", "Looking at it now.", false, default);
            var internalNote = await service.AddCommentAsync(ticket.Id, "staff-1", "Third jam this quarter.", true, default);

            Assert.True(publicComment.Success);
            Assert.True(internalNote.Success);
            Assert.False(publicComment.Value!.IsInternal);
            Assert.True(internalNote.Value!.IsInternal);
            Assert.Equal(2, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_an_empty_comment()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);

            var result = await service.AddCommentAsync(ticket.Id, "staff-1", "  ", false, default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }
}
