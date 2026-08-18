using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

public class TicketNumberAllocatorTests
{
    [Fact]
    public async Task First_ticket_for_a_tenant_is_number_one()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var allocator = new TicketNumberAllocator(db);

            Assert.Equal(1, await allocator.NextAsync(tenantId, default));
        }
    }

    [Fact]
    public async Task Numbers_continue_from_the_tenants_highest()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "u1", "User One");

            db.Tickets.Add(new Ticket { TenantId = tenantId, TicketNumber = 7, Title = "a", RequesterUserId = "u1" });
            await db.SaveChangesAsync();

            var allocator = new TicketNumberAllocator(db);
            Assert.Equal(8, await allocator.NextAsync(tenantId, default));
        }
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_sequence()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantA);
            await TestDb.SeedTenantAsync(db, tenantB);
            await TestDb.SeedUserAsync(db, tenantA, "a1", "A One");

            db.Tickets.Add(new Ticket { TenantId = tenantA, TicketNumber = 42, Title = "a", RequesterUserId = "a1" });
            await db.SaveChangesAsync();

            var allocator = new TicketNumberAllocator(db);
            Assert.Equal(43, await allocator.NextAsync(tenantA, default));
            Assert.Equal(1, await allocator.NextAsync(tenantB, default));
        }
    }
}
