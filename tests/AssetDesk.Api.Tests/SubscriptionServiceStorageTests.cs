using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

public class SubscriptionServiceStorageTests
{
    // SubscriptionService resolves its own AppDbContext from a fresh DI scope on every
    // call (it is a singleton-friendly service, not one constructed per-request like
    // TicketService), so it cannot simply be handed the TestDb.Create() context directly -
    // it needs a real service provider wired to the same in-memory Sqlite connection.
    [Fact]
    public async Task GetUsageAsync_sums_asset_and_ticket_attachments_together()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "AST-1");

            var ticket = new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Type = TicketTypes.Incident,
                Title = "Printer jams",
                Status = TicketStatus.New,
                Priority = TicketPriority.Medium,
                RequesterUserId = "staff-1"
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();

            db.Attachments.Add(new Attachment
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                FileName = "receipt.pdf",
                StoredFileName = "stored-receipt.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 100,
                Category = AttachmentCategories.Receipt,
                UploadedByUserId = "staff-1"
            });

            db.TicketAttachments.Add(new TicketAttachment
            {
                TenantId = tenantId,
                TicketId = ticket.Id,
                FileName = "photo.jpg",
                StoredFileName = "stored-photo.jpg",
                ContentType = "image/jpeg",
                FileSizeBytes = 50,
                Category = TicketAttachmentCategories.Other,
                UploadedByUserId = "staff-1"
            });

            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton<ITenantProvider>(new FakeTenantProvider(tenantId));
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(conn));
            await using var provider = services.BuildServiceProvider();

            var subscriptionService = new SubscriptionService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<SubscriptionService>.Instance);

            var usage = await subscriptionService.GetUsageAsync(tenantId);

            Assert.Equal(150, usage.CurrentStorageBytes);
        }
    }
}
