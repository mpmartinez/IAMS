using System.Text.Json;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAMS.Api.Tests;

public class AuditLogTests
{
    private sealed class StubUser : ICurrentUserAccessor
    {
        private readonly string? _id;
        public StubUser(string? id) => _id = id;
        public string? GetUserId() => _id;
    }

    private static (AppDbContext Db, SqliteConnection Conn) CreateAudited(string? userId = "staff-1")
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(
                new StubUser(userId), NullLogger<AuditSaveChangesInterceptor>.Instance))
            .Options;

        // Super-admin provider, not the provider-less constructor — see TestDb.Create.
        var db = new AppDbContext(options, new FakeTenantProvider(null, isSuperAdmin: true));
        db.Database.EnsureCreated();
        return (db, connection);
    }

    [Fact]
    public async Task Creating_a_ticket_writes_a_Created_entry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            var entry = await db.AuditLogs.SingleAsync(a => a.EntityType == "Ticket");
            Assert.Equal(AuditActions.Created, entry.Action);
            Assert.Equal("staff-1", entry.UserId);
            Assert.Equal(tenantId, entry.TenantId);

            // The whole point of the two-phase write: a Created entry names its row.
            Assert.Equal(ticket.Id.ToString(), entry.EntityId);
            Assert.NotEqual("0", entry.EntityId);
            Assert.NotEqual("", entry.EntityId);
        }
    }

    [Fact]
    public async Task Deleting_a_ticket_records_a_Deleted_entry_with_its_key()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            var id = ticket.Id;

            db.Tickets.Remove(ticket);
            await db.SaveChangesAsync();

            var entry = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.Deleted);
            Assert.Equal("Ticket", entry.EntityType);
            Assert.Equal(id.ToString(), entry.EntityId);
        }
    }

    [Fact]
    public async Task Updating_a_ticket_records_only_the_changed_fields()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            ticket.Status = TicketStatus.Assigned;
            ticket.AssignedToUserId = "staff-1";
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            Assert.True(changes.ContainsKey("Status"));
            Assert.True(changes.ContainsKey("AssignedToUserId"));
            Assert.False(changes.ContainsKey("Title"));
            Assert.Equal("New", changes["Status"].GetProperty("from").GetString());
            Assert.Equal("Assigned", changes["Status"].GetProperty("to").GetString());

            // Two saves happened (Created, then Updated); a leaked or duplicated entry from
            // either save would slip past the assertions above unless the count is checked too.
            Assert.Equal(2, await db.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task Notifications_are_not_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            db.Notifications.Add(new Notification
            {
                TenantId = tenantId, UserId = "staff-1",
                Title = "Hello", Message = "Body", Type = NotificationTypes.Info
            });
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task Audit_entries_do_not_audit_themselves()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            Assert.Equal(1, await db.AuditLogs.CountAsync());
        }
    }

    // SavingChanges/SavedChanges/Flush (the synchronous overrides) are never exercised by the
    // tests above, which all go through SaveChangesAsync. Cover the sync path directly.
    [Fact]
    public async Task Creating_a_ticket_via_the_sync_SaveChanges_writes_a_Created_entry()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            db.SaveChanges();

            var entry = await db.AuditLogs.SingleAsync(a => a.EntityType == "Ticket");
            Assert.Equal(AuditActions.Created, entry.Action);
            Assert.Equal(ticket.Id.ToString(), entry.EntityId);
            Assert.NotEqual("0", entry.EntityId);
            Assert.NotEqual("", entry.EntityId);
        }
    }

    [Fact]
    public async Task CreatedAt_and_UpdatedAt_are_excluded_from_serialised_changes()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            asset.Status = AssetStatus.InUse;
            asset.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.EntityType == "Asset" && a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            Assert.True(changes.ContainsKey("Status"));
            Assert.False(changes.ContainsKey("UpdatedAt"));
        }
    }

    [Fact]
    public async Task Long_string_values_are_truncated_short_ones_are_not()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var ticket = new Ticket
            {
                TenantId = tenantId, TicketNumber = 1,
                Title = "Printer jams", RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            var comment = new TicketComment { TenantId = tenantId, TicketId = ticket.Id, UserId = "staff-1", Body = "short" };
            db.TicketComments.Add(comment);
            await db.SaveChangesAsync();

            var longBody = new string('x', 600);
            comment.Body = longBody;
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.EntityType == "TicketComment" && a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            var from = changes["Body"].GetProperty("from").GetString();
            var to = changes["Body"].GetProperty("to").GetString();

            Assert.Equal("short", from);
            Assert.StartsWith(new string('x', 500), to);
            Assert.Contains("truncated, 600 chars", to);
            Assert.True(to!.Length < longBody.Length);
        }
    }
}
