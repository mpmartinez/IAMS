using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TestDbTests
{
    [Fact]
    public async Task Create_gives_a_usable_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            var found = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal("IAMS-0001", found.AssetTag);
            Assert.Equal(tenantId, found.TenantId);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_assets()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedAssetAsync(db, mine, "MINE-1");
            await TestDb.SeedAssetAsync(db, theirs, "THEIRS-1");

            var visible = await db.Assets.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE-1", visible[0].AssetTag);
        }
    }

    // The four entities the service desk added each carry a global tenant query filter.
    // Those filters are the whole containment story for a product sold on audit evidence,
    // and every TicketEntityTests case runs through a super-admin context that bypasses
    // them — so without these, a copy-paste slip in one filter passes the entire suite.
    // Writes are deliberately not filtered, which is why both tenants' rows can be seeded
    // through the same scoped context and only the reads are expected to narrow.

    [Fact]
    public async Task Query_filter_hides_other_tenants_tickets()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, mine, "mine-1", "Mine One");
            await TestDb.SeedUserAsync(db, theirs, "theirs-1", "Theirs One");

            db.Tickets.Add(new Ticket
            {
                TenantId = mine, TicketNumber = 1, Title = "MINE", RequesterUserId = "mine-1"
            });
            db.Tickets.Add(new Ticket
            {
                TenantId = theirs, TicketNumber = 1, Title = "THEIRS", RequesterUserId = "theirs-1"
            });
            await db.SaveChangesAsync();

            var visible = await db.Tickets.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE", visible[0].Title);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_ticket_comments()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, mine, "mine-1", "Mine One");
            await TestDb.SeedUserAsync(db, theirs, "theirs-1", "Theirs One");

            var myTicket = new Ticket
            {
                TenantId = mine, TicketNumber = 1, Title = "MINE", RequesterUserId = "mine-1"
            };
            var theirTicket = new Ticket
            {
                TenantId = theirs, TicketNumber = 1, Title = "THEIRS", RequesterUserId = "theirs-1"
            };
            db.Tickets.AddRange(myTicket, theirTicket);
            await db.SaveChangesAsync();

            db.TicketComments.Add(new TicketComment
            {
                TenantId = mine, TicketId = myTicket.Id, UserId = "mine-1", Body = "MINE"
            });
            db.TicketComments.Add(new TicketComment
            {
                TenantId = theirs, TicketId = theirTicket.Id, UserId = "theirs-1", Body = "THEIRS"
            });
            await db.SaveChangesAsync();

            var visible = await db.TicketComments.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE", visible[0].Body);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_ticket_attachments()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedUserAsync(db, mine, "mine-1", "Mine One");
            await TestDb.SeedUserAsync(db, theirs, "theirs-1", "Theirs One");

            var myTicket = new Ticket
            {
                TenantId = mine, TicketNumber = 1, Title = "MINE", RequesterUserId = "mine-1"
            };
            var theirTicket = new Ticket
            {
                TenantId = theirs, TicketNumber = 1, Title = "THEIRS", RequesterUserId = "theirs-1"
            };
            db.Tickets.AddRange(myTicket, theirTicket);
            await db.SaveChangesAsync();

            db.TicketAttachments.Add(new TicketAttachment
            {
                TenantId = mine, TicketId = myTicket.Id,
                FileName = "MINE.pdf", StoredFileName = "a.pdf",
                ContentType = "application/pdf", Category = "Document",
                UploadedByUserId = "mine-1"
            });
            db.TicketAttachments.Add(new TicketAttachment
            {
                TenantId = theirs, TicketId = theirTicket.Id,
                FileName = "THEIRS.pdf", StoredFileName = "b.pdf",
                ContentType = "application/pdf", Category = "Document",
                UploadedByUserId = "theirs-1"
            });
            await db.SaveChangesAsync();

            var visible = await db.TicketAttachments.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE.pdf", visible[0].FileName);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_audit_logs()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);

            db.AuditLogs.Add(new AuditLog
            {
                TenantId = mine, EntityType = "Ticket", EntityId = "1",
                Action = AuditActions.Created, UserId = "mine-1"
            });
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = theirs, EntityType = "Ticket", EntityId = "1",
                Action = AuditActions.Created, UserId = "theirs-1"
            });
            await db.SaveChangesAsync();

            var visible = await db.AuditLogs.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("mine-1", visible[0].UserId);
        }
    }
}
