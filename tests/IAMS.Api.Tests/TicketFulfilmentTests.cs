using System.Data.Common;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Fails the audit interceptor's own save - the one it issues from SavedChanges, which
    /// inside FulfilAsync runs within the caller's still-uncommitted transaction. Recognised
    /// by the AuditLog rows the interceptor has just added to the context.
    /// </summary>
    private sealed class ThrowOnAuditSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        private bool ShouldThrow(DbContext? context) =>
            _armed && context is not null &&
            context.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added);

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (ShouldThrow(eventData.Context))
                throw new DbUpdateException("Simulated audit write failure.");
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldThrow(eventData.Context))
                throw new DbUpdateException("Simulated audit write failure.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Simulates the race the conditional claim exists to close: once armed, it flips every
    /// asset to InUse on the same connection immediately before FulfilAsync's claim statement
    /// executes - that is, after the availability guard has already read the row as Available.
    /// Nothing else can produce that window deterministically from a single test connection.
    /// </summary>
    private sealed class StealTheAssetInterceptor : DbCommandInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        private void StealBefore(DbCommand command)
        {
            if (!_armed) return;
            if (!command.CommandText.Contains("UPDATE", StringComparison.Ordinal)) return;
            if (!command.CommandText.Contains("Assets", StringComparison.Ordinal)) return;

            // Only the first asset UPDATE in the transaction - the claim - is raced.
            _armed = false;

            using var steal = command.Connection!.CreateCommand();
            steal.Transaction = command.Transaction;
            steal.CommandText = "UPDATE \"Assets\" SET \"Status\" = 'InUse'";
            steal.ExecuteNonQuery();
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            StealBefore(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            StealBefore(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Simulates the race the ticket's conditional claim exists to close: once armed, it
    /// flips every ticket to Closed on the same connection immediately before FulfilAsync's
    /// ticket-claim statement executes - that is, after the IsOpen guard has already read the
    /// ticket as open. Stands in for a second, concurrent fulfilment of the same ticket (e.g.
    /// a replayed offline sync action) that got there first.
    /// </summary>
    private sealed class StealTheTicketInterceptor : DbCommandInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        private void StealBefore(DbCommand command)
        {
            if (!_armed) return;
            if (!command.CommandText.Contains("UPDATE", StringComparison.Ordinal)) return;
            if (!command.CommandText.Contains("Tickets", StringComparison.Ordinal)) return;

            // Only the first Tickets UPDATE in the transaction - the claim - is raced.
            _armed = false;

            using var steal = command.Connection!.CreateCommand();
            steal.Transaction = command.Transaction;
            steal.CommandText = "UPDATE \"Tickets\" SET \"Status\" = 'Closed'";
            steal.ExecuteNonQuery();
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            StealBefore(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            StealBefore(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class StubUser : ICurrentUserAccessor
    {
        public string? GetUserId() => "staff-1";
    }

    private sealed class TransientTestException : Exception
    {
        public TransientTestException(string message) : base(message) { }
    }

    /// <summary>
    /// Stands in for Npgsql's EnableRetryOnFailure strategy, which production runs with and
    /// SQLite otherwise never provides. Two things only a retrying strategy can show up:
    /// EF Core refuses a user-initiated BeginTransaction outside the strategy, and the
    /// operation body can be executed more than once.
    /// </summary>
    private sealed class RetryingTestExecutionStrategy : ExecutionStrategy
    {
        public RetryingTestExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
    }

    /// <summary>
    /// Fails the second save of the first fulfilment attempt only, with an exception the test
    /// execution strategy treats as transient - so the attempt is rolled back and replayed.
    /// </summary>
    private sealed class TransientOnFirstAttemptInterceptor : SaveChangesInterceptor
    {
        private bool _armed;
        private int _count;
        private bool _thrown;

        public void Arm()
        {
            _armed = true;
            _count = 0;
        }

        private void MaybeThrow()
        {
            if (!_armed || _thrown) return;
            if (++_count != 2) return;

            _thrown = true;
            throw new TransientTestException("Simulated transient failure on the first attempt.");
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            MaybeThrow();
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// A SQLite context with test interceptors attached. The connection and the context are
    /// each disposed if a later step of the construction throws, so a failed EnsureCreated
    /// cannot leak an open in-memory connection.
    /// </summary>
    private static (AppDbContext Db, SqliteConnection Connection) CreateWith(
        Guid tenantId, params IInterceptor[] interceptors) =>
        CreateWith(tenantId, retrying: false, interceptors);

    /// <inheritdoc cref="CreateWith(Guid, IInterceptor[])"/>
    private static (AppDbContext Db, SqliteConnection Connection) CreateWith(
        Guid tenantId, bool retrying, params IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        try
        {
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection, sqlite =>
                {
                    if (retrying)
                        sqlite.ExecutionStrategy(deps => new RetryingTestExecutionStrategy(deps));
                })
                .AddInterceptors(interceptors)
                .Options;

            // Super-admin provider is never used here - see TestDb.Create for why the
            // provider-less constructor is not an option.
            var db = new AppDbContext(options, new FakeTenantProvider(tenantId));
            try
            {
                db.Database.EnsureCreated();
            }
            catch
            {
                db.Dispose();
                throw;
            }

            return (db, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
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
        var (db, conn) = CreateWith(tenantId, interceptor);

        using (db)
        using (conn)
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

    /// <summary>
    /// The rolled-back state must not survive as pending change-tracker entries either: the
    /// controller keeps using this request-scoped context, and the very next SaveChanges
    /// (a notification, a comment) would otherwise flush the ticket closure and the asset
    /// status change with no assignment row behind them - the exact corruption the
    /// transaction exists to prevent, reached from outside it.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_fulfilment_leaves_nothing_pending_on_the_context()
    {
        var tenantId = Guid.NewGuid();
        var interceptor = new ThrowOnSecondSaveInterceptor();
        var (db, conn) = CreateWith(tenantId, interceptor);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            interceptor.Arm();
            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);
            Assert.False(result.Success);

            Assert.Empty(db.ChangeTracker.Entries());

            // An unrelated later save on the same context must not carry the failed
            // fulfilment into the database with it. No ChangeTracker.Clear() here on
            // purpose - that would hide the bug this test is about.
            db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = "emp-1",
                Title = "Request update",
                Message = "Still open.",
                Type = NotificationTypes.Info
            });
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    /// <summary>
    /// The audit interceptor saves from SavedChanges, which inside FulfilAsync happens before
    /// the caller's Commit. Swallowing a failure there would leave the caller committing an
    /// aborted transaction - on PostgreSQL a silent rollback that reports success - and
    /// FulfilAsync would return Ok for an operation that persisted nothing.
    /// </summary>
    [Fact]
    public async Task An_audit_failure_inside_the_transaction_is_not_reported_as_success()
    {
        var tenantId = Guid.NewGuid();
        var audit = new AuditSaveChangesInterceptor(
            new StubUser(), NullLogger<AuditSaveChangesInterceptor>.Instance);
        var breakAudit = new ThrowOnAuditSaveInterceptor();
        var (db, conn) = CreateWith(tenantId, audit, breakAudit);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            // Seeding audits successfully; only the fulfilment's audit write fails.
            breakAudit.Arm();

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Null(savedAsset.AssignedToUserId);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    /// <summary>
    /// Two fulfilments naming the same asset can both pass the availability guard, which is a
    /// plain read outside the transaction. The conditional claim inside the transaction is
    /// what actually stops the second one from issuing an already-issued machine.
    /// </summary>
    [Fact]
    public async Task An_asset_taken_after_the_availability_check_is_not_issued_again()
    {
        var tenantId = Guid.NewGuid();
        var thief = new StealTheAssetInterceptor();
        var (db, conn) = CreateWith(tenantId, thief);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            thief.Arm();

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);
            // Specifically the claim's rejection, not the earlier read-based guard, which the
            // asset was still Available for.
            Assert.Contains("no longer available", result.Message!, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            // The steal ran inside the same transaction, so the rollback undoes it too: what
            // matters is that the ticket was not closed and no assignment was created.
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Null(saved.AssetAssignmentId);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    /// <summary>
    /// Mirrors An_asset_taken_after_the_availability_check_is_not_issued_again for the other
    /// half of the race: two fulfilments of the *same* ticket, each naming a different
    /// available asset, can both pass the IsOpen guard, which is a plain read outside the
    /// transaction. The conditional claim on Tickets is what actually stops the second one
    /// from creating a second assignment against a ticket the first one already closed.
    /// </summary>
    [Fact]
    public async Task A_ticket_closed_after_the_open_check_is_not_fulfilled_again()
    {
        var tenantId = Guid.NewGuid();
        var thief = new StealTheTicketInterceptor();
        var (db, conn) = CreateWith(tenantId, thief);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            thief.Arm();

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.False(result.Success);
            // Specifically the claim's rejection, not the earlier IsOpen guard, which the
            // ticket was still New for, and distinguishable from that guard's wording too.
            Assert.Contains("no longer open", result.Message!, StringComparison.OrdinalIgnoreCase);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            // The steal ran inside the same transaction, so the rollback undoes it too: what
            // matters is that the asset was never claimed and no assignment was created.
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Null(saved.AssetAssignmentId);
            Assert.Equal(AssetStatus.Available, savedAsset.Status);
            Assert.Null(savedAsset.AssignedToUserId);
            Assert.Equal(0, await db.AssetAssignments.CountAsync());
        }
    }

    /// <summary>
    /// Production is Npgsql with EnableRetryOnFailure. EF Core rejects a user-initiated
    /// transaction under a retrying execution strategy, so a FulfilAsync that opened its own
    /// transaction directly threw on every call in production while every SQLite test passed.
    /// This test configures a retrying strategy so that failure mode is reachable here.
    /// </summary>
    [Fact]
    public async Task Fulfilling_works_under_a_retrying_execution_strategy()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateWith(tenantId, retrying: true);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.True(result.Success, result.Message);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.Equal(1, await db.AssetAssignments.CountAsync());
        }
    }

    /// <summary>
    /// The retrying strategy can execute the whole operation more than once. A replay starts
    /// from a change tracker that still holds the failed attempt's mutations unless the
    /// operation clears it and re-reads, which would re-apply stale state or duplicate the
    /// assignment. The transient failure here lands on the second save, so the first attempt
    /// has already inserted an assignment and mutated the ticket before it is rolled back.
    /// </summary>
    [Fact]
    public async Task A_retried_fulfilment_replays_cleanly_and_issues_the_asset_once()
    {
        var tenantId = Guid.NewGuid();
        var transient = new TransientOnFirstAttemptInterceptor();
        var (db, conn) = CreateWith(tenantId, retrying: true, transient);

        using (db)
        using (conn)
        {
            var (service, request) = await SetupAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0356");

            transient.Arm();

            var result = await service.FulfilAsync(request.Id, asset.Id, "Issued.", "staff-1", default);

            Assert.True(result.Success, result.Message);

            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == request.Id);
            var savedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
            var assignment = await db.AssetAssignments.SingleAsync();

            Assert.Equal(TicketStatus.Closed, saved.Status);
            Assert.Equal(assignment.Id, saved.AssetAssignmentId);
            Assert.Equal(AssetStatus.InUse, savedAsset.Status);
            Assert.Equal("emp-1", savedAsset.AssignedToUserId);
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
