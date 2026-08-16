using IAMS.Api.Entities;
using IAMS.Api.Mapping;
using IAMS.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TicketCommentVisibilityTests
{
    [Fact]
    public async Task A_requesters_view_never_contains_internal_notes()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var service = new TicketService(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));
            var created = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.High, null, "emp-1", default);

            await service.AddCommentAsync(created.Value!.Id, "staff-1", "On it now.", false, default);
            await service.AddCommentAsync(created.Value.Id, "staff-1", "Warranty lapses soon.", true, default);

            db.ChangeTracker.Clear();
            var loaded = await db.Tickets
                .Include(t => t.Comments)
                .SingleAsync(t => t.Id == created.Value.Id);

            var requesterView = loaded.ToDto(includeInternalComments: false);
            var staffView = loaded.ToDto(includeInternalComments: true);

            Assert.Single(requesterView.Comments);
            Assert.DoesNotContain(requesterView.Comments, c => c.IsInternal);
            Assert.DoesNotContain("Warranty", string.Join(" ", requesterView.Comments.Select(c => c.Body)));
            Assert.Equal(2, staffView.Comments.Count);
        }
    }
}
