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
            TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

        return (service, created.Value!);
    }

    // Like SetupAsync, but the ticket is linked to a seeded asset - SetupAsync always
    // passes assetId: null, which is why ResolveAsync's asset-restoration branch had no
    // coverage at all.
    private static async Task<(TicketService Service, Ticket Ticket, Asset Asset)> SetupWithAssetAsync(
        AppDbContext db, Guid tenantId, string assetStatus, string? assetAssignedToUserId = null)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var asset = await TestDb.SeedAssetAsync(db, tenantId, "AST-1", assetStatus);
        if (assetAssignedToUserId is not null)
        {
            asset.AssignedToUserId = assetAssignedToUserId;
            await db.SaveChangesAsync();
        }

        var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
        var created = await service.CreateAsync(
            TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.High, asset.Id, "emp-1", default);

        return (service, created.Value!, asset);
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

    [Fact]
    public async Task Rejects_a_comment_over_4000_characters()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            var longBody = new string('a', 4001);

            var result = await service.AddCommentAsync(ticket.Id, "staff-1", longBody, false, default);

            Assert.False(result.Success);
            Assert.Contains("4000", result.Message!);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Reassigning_an_open_ticket_keeps_its_status_and_first_AssignedAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-2", "L. Reyes");

            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            db.ChangeTracker.Clear();
            var firstAssignedAt = (await db.Tickets.SingleAsync(t => t.Id == ticket.Id)).AssignedAt;
            Assert.NotNull(firstAssignedAt);

            var result = await service.AssignAsync(ticket.Id, "staff-2", default);

            Assert.True(result.Success);
            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.InProgress, saved.Status);
            Assert.Equal("staff-2", saved.AssignedToUserId);
            Assert.Equal(firstAssignedAt, saved.AssignedAt);
        }
    }

    [Fact]
    public async Task Closed_ticket_cannot_be_reassigned()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-2", "L. Reyes");

            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);
            await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.Closed, default);

            var result = await service.AssignAsync(ticket.Id, "staff-2", default);

            Assert.False(result.Success);
            Assert.Contains("Closed", result.Message!);
            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal("staff-1", saved.AssignedToUserId);
        }
    }

    [Fact]
    public async Task ChangeStatusAsync_refuses_to_move_directly_to_Resolved()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket) = await SetupAsync(db, tenantId);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var result = await service.ChangeStatusAsync(ticket.Id, TicketStatus.Resolved, default);

            Assert.False(result.Success);
            Assert.Contains("Resolve", result.Message!);
            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.InProgress, saved.Status);
        }
    }

    [Fact]
    public async Task Resolving_restores_a_maintenance_asset_with_an_assignee_to_InUse()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, asset) = await SetupWithAssetAsync(
                db, tenantId, AssetStatus.Maintenance, assetAssignedToUserId: "emp-1");
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var result = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);

            Assert.True(result.Success);
            db.ChangeTracker.Clear();
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.Equal(AssetStatus.InUse, savedAsset.Status);
        }
    }

    [Fact]
    public async Task Resolving_restores_a_maintenance_asset_with_no_assignee_to_Available()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, asset) = await SetupWithAssetAsync(
                db, tenantId, AssetStatus.Maintenance);
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var result = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);

            Assert.True(result.Success);
            db.ChangeTracker.Clear();
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
        }
    }

    [Fact]
    public async Task Resolving_leaves_a_non_maintenance_asset_untouched()
    {
        // Uses an assignee on a non-Maintenance asset so that if the Maintenance gate in
        // ResolveAsync were ever removed, this would flip to InUse and the test would fail -
        // an unassigned Available asset would stay Available either way and wouldn't pin
        // the gate at all.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, asset) = await SetupWithAssetAsync(
                db, tenantId, AssetStatus.Available, assetAssignedToUserId: "emp-1");
            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);

            var result = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", default);

            Assert.True(result.Success);
            db.ChangeTracker.Clear();
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
        }
    }

    [Fact]
    public async Task Cannot_assign_a_ticket_to_a_user_from_another_tenant()
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
            await TestDb.SeedUserAsync(db, theirs, "foreign-staff", "Foreign Staff");

            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(mine));
            var created = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

            // The ticket is New (open, assignable), so this exercises the tenant-scoped
            // assignee lookup itself rather than the status gate Fix 3 moved ahead of it.
            var result = await service.AssignAsync(created.Value!.Id, "foreign-staff", default);

            Assert.False(result.Success);
            Assert.Contains("does not exist", result.Message!);
            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == created.Value.Id);
            Assert.Null(saved.AssignedToUserId);
        }
    }

    [Fact]
    public async Task Cannot_attribute_a_comment_to_a_user_from_another_tenant()
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
            await TestDb.SeedUserAsync(db, theirs, "foreign-staff", "Foreign Staff");

            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(mine));
            var created = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

            var result = await service.AddCommentAsync(
                created.Value!.Id, "foreign-staff", "Looking at it now.", false, default);

            Assert.False(result.Success);
            Assert.Contains("does not exist", result.Message!);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }
}
