using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketEntityTests
{
    [Fact]
    public async Task Ticket_round_trips_with_its_defaults()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "J. Dela Cruz");

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Printer jams",
                RequesterUserId = "user-1"
            });
            await db.SaveChangesAsync();

            var saved = await db.Tickets.SingleAsync();

            Assert.Equal(TicketTypes.Incident, saved.Type);
            Assert.Equal(TicketCategory.Other, saved.Category);
            Assert.Equal(TicketStatus.New, saved.Status);
            Assert.Equal(TicketPriority.Medium, saved.Priority);
            Assert.Null(saved.AssetId);
            Assert.Null(saved.AssignedToUserId);
        }
    }

    [Fact]
    public async Task Ticket_number_is_unique_within_a_tenant_only()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantA, "a-1", "A One");
            await TestDb.SeedUserAsync(db, tenantB, "b-1", "B One");

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 1, Title = "A", RequesterUserId = "a-1" });
            db.Tickets.Add(new Ticket { TenantId = tenantB, TicketNumber = 1, Title = "B", RequesterUserId = "b-1" });
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.Tickets.CountAsync());

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 1, Title = "dupe", RequesterUserId = "a-1" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Comments_cascade_when_their_ticket_is_deleted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "J. Dela Cruz");

            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Printer jams",
                RequesterUserId = "user-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            db.TicketComments.Add(new TicketComment
            {
                TenantId = tenantId,
                TicketId = ticket.Id,
                UserId = "user-1",
                Body = "Any update?"
            });
            await db.SaveChangesAsync();

            db.Tickets.Remove(ticket);
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Asset_carries_an_owner_and_a_verification_stamp()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "owner-1", "Crewing Manager");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0241");

            asset.OwnerUserId = "owner-1";
            asset.LastVerifiedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var reloaded = await db.Assets.SingleAsync();
            Assert.Equal("owner-1", reloaded.OwnerUserId);
            Assert.Equal(2026, reloaded.LastVerifiedAt!.Value.Year);
        }
    }

    [Fact]
    public async Task Deleting_the_asset_clears_AssetId_but_keeps_the_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "J. Dela Cruz");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0242");

            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Laptop won't boot",
                RequesterUserId = "user-1",
                AssetId = asset.Id
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            db.Assets.Remove(asset);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var reloaded = await db.Tickets.SingleAsync();
            Assert.Null(reloaded.AssetId);
        }
    }
}
