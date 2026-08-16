using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAMS.Api.Tests;

public class TicketFulfilmentTests
{
    /// <summary>
    /// Throws on the second SaveChanges call after being armed, simulating a failure
    /// between FulfilAsync's assignment insert (save #1) and its ticket/asset update
    /// (save #2). Lets a test prove the transaction actually rolls back the first save
    /// too, rather than merely proving an early guard rejected the request before any
    /// write happened.
    /// </summary>
    private sealed class ThrowOnSecondSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;
        private int _count;

        public void Arm()
        {
            _armed = true;
            _count = 0;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (_armed && ++_count == 2)
                throw new DbUpdateException("Simulated failure to prove transactional rollback.");
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && ++_count == 2)
                throw new DbUpdateException("Simulated failure to prove transactional rollback.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static async Task<(TicketService Service, Ticket Request)> SetupAsync(AppDbContext db, Guid tenantId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "A. Reyes");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
        var created = await service.CreateAsync(
            TicketTypes.Request, "Laptop for new documentation officer",
            null, TicketPriority.Medium, null, "emp-1", default);

        return (service, created.Value!);
    }

    [Fact]
    public async Task Fulfilling_creates_the_assignment_and_closes_the_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(
                request.Id, asset.Id, "Issued ThinkPad E14 with charger.", "staff-1", default);

            Assert.True(result.Success);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            var assignment = await db.AssetAssignments.SingleAsync();

            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.Equal(asset.Id, saved.AssetId);
            Assert.Equal(assignment.Id, saved.AssetAssignmentId);
            Assert.NotNull(saved.ResolvedAt);
            Assert.NotNull(saved.ClosedAt);
            Assert.Equal(AssetStatus.InUse, savedAsset.Status);
            Assert.Equal("emp-1", savedAsset.AssignedToUserId);
            Assert.Equal("emp-1", assignment.UserId);
        }
    }

    [Fact]
    public async Task Refuses_an_asset_that_is_not_available()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356", AssetStatus.InUse);

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);
            Assert.Contains("available", result.Message!, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Refuses_to_fulfil_a_non_request_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "A. Reyes");
            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
            var incident = await service.CreateAsync(
                TicketTypes.Incident, "Printer jams", null, TicketPriority.Low, null, "emp-1", default);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(incident.Value!.Id, asset.Id, "n/a", "emp-1", default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task A_failure_between_the_two_saves_rolls_back_the_assignment_too()
    {
        var tenantId = Guid.NewGuid();
        var interceptor = new ThrowOnSecondSaveInterceptor();

        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        var db = new AppDbContext(options, new FakeTenantProvider(tenantId));
        db.Database.EnsureCreated();

        using (db)
        using (connection)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            // Only start failing once the real fulfilment saves begin - seeding above must
            // not be affected, or this would just be testing setup, not FulfilAsync.
            interceptor.Arm();

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            // The assignment insert (save #1) committed to nothing, because save #2 failed
            // inside the same transaction and both were rolled back together.
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Null(saved.AssetAssignmentId);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Null(savedAsset.AssignedToUserId);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    [Fact]
    public async Task Nothing_is_written_when_the_ticket_does_not_exist()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, _) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(9999, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);

            db.ChangeTracker.Clear();
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }
}
