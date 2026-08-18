using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

public class TicketServiceCreateTests
{
    private static TicketService Build(AssetDesk.Api.Data.AppDbContext db, Guid tenantId) =>
        new(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));

    // Stub allocator for testing the create-time retry loop without concurrency:
    // it hands out whatever sequence of ticket numbers the test configures.
    private class StubTicketNumberAllocator : ITicketNumberAllocator
    {
        private readonly Func<int> _next;
        public StubTicketNumberAllocator(Func<int> next) => _next = next;
        public Task<int> NextAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(_next());
    }

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
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", "Every second page", TicketPriority.High, null, "emp-1", default);

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
                "Escalation", TicketCategory.Hardware, "Bad type", null, TicketPriority.Low, null, "emp-1", default);

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
                TicketTypes.Incident, TicketCategory.Hardware, "Not mine", null, TicketPriority.Low, foreign.Id, "emp-1", default);

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
                TicketTypes.SecurityEvent, TicketCategory.Security, "Phishing email", null, TicketPriority.Low, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(TicketPriority.High, result.Value!.Priority);
        }
    }

    [Fact]
    public async Task Security_events_reported_as_critical_stay_critical()
    {
        // High is a floor, not a fixed value: a Critical report must not be silently
        // downgraded to High.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.SecurityEvent, TicketCategory.Security, "Active breach", null, TicketPriority.Critical, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(TicketPriority.Critical, result.Value!.Priority);
        }
    }

    [Fact]
    public async Task Rejects_a_title_over_200_characters()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            var longTitle = new string('a', 201);

            var result = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, longTitle, null, TicketPriority.Low, null, "emp-1", default);

            Assert.False(result.Success);
            Assert.Contains("200", result.Message!);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Rejects_a_requester_from_another_tenant()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, theirs, "foreign-emp", "Foreign Employee");
            var service = Build(db, mine);

            var result = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Not my requester", null, TicketPriority.Low, null, "foreign-emp", default);

            Assert.False(result.Success);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Accepts_a_valid_same_tenant_asset()
    {
        // Positive control for Rejects_an_asset_from_another_tenant: without this, an
        // AnyAsync that was unconditionally false would still pass the whole suite.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "MINE-1");
            var service = Build(db, tenantId);

            var result = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Broken screen", null, TicketPriority.Low, asset.Id, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(asset.Id, result.Value!.AssetId);
        }
    }

    [Fact]
    public async Task Recovers_from_a_ticket_number_collision_and_retries()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId, TicketNumber = 1, Title = "Existing", RequesterUserId = "emp-1"
            });
            await db.SaveChangesAsync();

            var numbers = new Queue<int>(new[] { 1, 2 });
            var allocator = new StubTicketNumberAllocator(() =>
                numbers.Count > 0 ? numbers.Dequeue() : throw new InvalidOperationException("Unexpected extra allocation"));
            var service = new TicketService(db, allocator, new FakeTenantProvider(tenantId));

            var result = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "New ticket", null, TicketPriority.Low, null, "emp-1", default);

            Assert.True(result.Success);
            Assert.Equal(2, result.Value!.TicketNumber);
        }
    }

    [Fact]
    public async Task Gives_up_after_max_retries_when_the_number_keeps_colliding()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId, TicketNumber = 1, Title = "Existing", RequesterUserId = "emp-1"
            });
            await db.SaveChangesAsync();

            var allocator = new StubTicketNumberAllocator(() => 1);
            var service = new TicketService(db, allocator, new FakeTenantProvider(tenantId));

            // After MaxNumberRetries attempts that all genuinely collide, the method gives
            // up by letting the underlying DbUpdateException propagate rather than looping
            // forever - CreateAsync's ServiceResult contract covers validation failures,
            // not an allocator that can never produce a free number.
            await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "New ticket", null, TicketPriority.Low, null, "emp-1", default));

            Assert.Equal(1, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Search_is_case_insensitive()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low, null, "emp-1", default);

            var (results, total, _, _) = await service.ListAsync(
                new TicketQuery(null, null, null, null, null, null, "PRINTER"), default);

            Assert.Equal(1, total);
            Assert.Equal("Printer jams", results[0].Title);
        }
    }

    [Fact]
    public async Task Search_treats_percent_as_a_literal_character()
    {
        // EF.Functions.Like's pattern wraps the raw search term in %...%, so a term that
        // itself contains % or _ acted as a wildcard instead of a literal character.
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "50% battery warning", null, TicketPriority.Low, null, "emp-1", default);

            var (results, total, _, _) = await service.ListAsync(
                new TicketQuery(null, null, null, null, null, null, "%"), default);

            Assert.Equal(1, total);
            Assert.Equal("50% battery warning", results[0].Title);
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

            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Request, TicketCategory.Hardware, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var (all, total, _, _) = await service.ListAsync(new TicketQuery(null, null, null, null, null, null, null), default);
            Assert.Equal(2, total);
            Assert.Equal(2, all.Count);

            var (requests, requestTotal, _, _) = await service.ListAsync(
                new TicketQuery(TicketTypes.Request, null, null, null, null, null, null), default);
            Assert.Equal(1, requestTotal);
            Assert.Equal("Two", requests[0].Title);
        }
    }

    [Fact]
    public async Task Rejects_an_unknown_category()
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
                TicketTypes.Incident, "Furniture", "Bad category", null, TicketPriority.Low, null, "emp-1", default);

            Assert.False(result.Success);
            Assert.Contains("category", result.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await db.Tickets.CountAsync());
        }
    }

    [Fact]
    public async Task Lists_and_filters_by_category()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "Laptop won't boot", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Software, "Excel keeps crashing", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Request, TicketCategory.Access, "Need training portal access", null, TicketPriority.Low, null, "emp-1", default);

            var (all, total, _, _) = await service.ListAsync(new TicketQuery(null, null, null, null, null, null, null), default);
            Assert.Equal(3, total);
            Assert.Equal(3, all.Count);

            var (software, softwareTotal, _, _) = await service.ListAsync(
                new TicketQuery(null, TicketCategory.Software, null, null, null, null, null), default);
            Assert.Equal(1, softwareTotal);
            Assert.Equal("Excel keeps crashing", software[0].Title);

            var (access, accessTotal, _, _) = await service.ListAsync(
                new TicketQuery(null, TicketCategory.Access, null, null, null, null, null), default);
            Assert.Equal(1, accessTotal);
            Assert.Equal("Need training portal access", access[0].Title);
        }
    }

    [Fact]
    public async Task New_tickets_default_to_the_Other_category_at_the_database_level()
    {
        // CreateAsync always passes an explicit, validated category, so this exercises the
        // column default directly - the safety net for any future write path that doesn't
        // go through the service (e.g. a raw insert or a future bulk-import job).
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");

            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Untyped ticket",
                RequesterUserId = "emp-1"
            };

            Assert.Equal(TicketCategory.Other, ticket.Category);
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

            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "One", null, TicketPriority.Low, null, "emp-1", default);
            await service.CreateAsync(TicketTypes.Incident, TicketCategory.Hardware, "Two", null, TicketPriority.Low, null, "emp-1", default);

            var summary = await service.GetSummaryAsync(default);

            Assert.Equal(2, summary.Open);
            Assert.Equal(2, summary.Unassigned);
            Assert.Equal(0, summary.InProgress);
            Assert.Equal(0, summary.Overdue);
        }
    }

    [Fact]
    public async Task ListAsync_reports_the_page_and_size_it_actually_used()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            var service = Build(db, tenantId);

            await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "One", null, TicketPriority.Low, null, "emp-1", default);

            // A caller asking for page 0 at 5000 per page gets page 1 at 200. The response must
            // say so: a client computing TotalCount / PageSize off the raw values sees one page
            // and silently drops the rest.
            var (_, _, page, size) = await service.ListAsync(
                new TicketQuery(null, null, null, null, null, null, null, Page: 0, PageSize: 5000), default);

            Assert.Equal(1, page);
            Assert.Equal(200, size);
        }
    }
}
