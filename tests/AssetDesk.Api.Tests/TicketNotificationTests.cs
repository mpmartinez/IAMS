using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The whole point of the notification wiring is that the person who filed a ticket hears
/// about it when someone else works on it. Every delivery piece (the SSE stream, the bell,
/// the read/unread endpoints) already existed and was exercised by hand; what had no producer
/// at all was the ticket workflow, so these cover the three moments that now raise one and,
/// just as importantly, the moments that must stay silent.
/// </summary>
public class TicketNotificationTests
{
    /// <summary>Records what the workflow asked for instead of touching a database.</summary>
    private sealed class FakeNotifications : INotificationService
    {
        public List<CreateNotificationDto> Sent { get; } = [];

        /// <summary>Set to make the send blow up, standing in for a database that is down.</summary>
        public bool Throws { get; set; }

        public Task<NotificationDto> CreateNotificationAsync(CreateNotificationDto dto)
        {
            if (Throws) throw new InvalidOperationException("notification store unavailable");

            Sent.Add(dto);
            return Task.FromResult(new NotificationDto
            {
                Id = Sent.Count,
                Title = dto.Title,
                Message = dto.Message,
                Type = dto.Type,
                Link = dto.Link
            });
        }

        public Task<List<NotificationDto>> GetUserNotificationsAsync(string userId, int take = 20) =>
            throw new NotSupportedException();
        public Task<NotificationCountDto> GetUserNotificationCountAsync(string userId) =>
            throw new NotSupportedException();
        public Task MarkAsReadAsync(int notificationId, string userId) => throw new NotSupportedException();
        public Task MarkAllAsReadAsync(string userId) => throw new NotSupportedException();
        public Task DeleteNotificationAsync(int notificationId, string userId) => throw new NotSupportedException();
        public IAsyncEnumerable<NotificationDto> SubscribeAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task BroadcastToUserAsync(string userId, NotificationDto notification) =>
            throw new NotSupportedException();
    }

    private static async Task<(TicketService Service, Ticket Ticket, FakeNotifications Notifications)> SetupAsync(
        AppDbContext db, Guid tenantId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

        var notifications = new FakeNotifications();
        var service = new TicketService(
            db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId),
            logger: null, lookups: null, notifications: notifications);

        var created = await service.CreateAsync(
            TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null,
            TicketPriority.High, null, "emp-1", default);

        return (service, created.Value!, notifications);
    }

    private static async Task ProgressAsync(TicketService service, Ticket ticket)
    {
        await service.AssignAsync(ticket.Id, "staff-1", default);
        await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);
    }

    [Fact]
    public async Task Resolving_notifies_the_requester_with_a_link_to_their_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);
            await ProgressAsync(service, ticket);

            var result = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", "staff-1", default);

            Assert.True(result.Success);
            var sent = Assert.Single(notifications.Sent);
            Assert.Equal("emp-1", sent.UserId);
            Assert.Equal(NotificationTypes.Success, sent.Type);
            Assert.Equal($"/tickets/{ticket.Id}", sent.Link);
            Assert.Equal(ticket.Id, sent.RelatedEntityId);
            Assert.Contains("Replaced the fuser.", sent.Message);

            // Written through a fresh DI scope with no HttpContext to stamp it from, so the
            // tenant has to be carried explicitly or the query filter hides it forever.
            Assert.Equal(tenantId, sent.TenantId);
        }
    }

    [Fact]
    public async Task Resolving_your_own_ticket_notifies_nobody()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);
            await ProgressAsync(service, ticket);

            // "emp-1" filed it, so resolving as "emp-1" is telling yourself what you just did.
            var result = await service.ResolveAsync(ticket.Id, "Sorted it myself.", "emp-1", default);

            Assert.True(result.Success);
            Assert.Empty(notifications.Sent);
        }
    }

    [Fact]
    public async Task A_failed_notification_does_not_fail_the_resolve()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);
            await ProgressAsync(service, ticket);
            notifications.Throws = true;

            var result = await service.ResolveAsync(ticket.Id, "Replaced the fuser.", "staff-1", default);

            // The resolution is committed. Reporting a failure here would invite the operator
            // to resolve an already-resolved ticket.
            Assert.True(result.Success);
            db.ChangeTracker.Clear();
            var saved = await db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            Assert.Equal(TicketStatus.Resolved, saved.Status);
        }
    }

    [Fact]
    public async Task A_public_comment_from_the_fixer_notifies_the_requester()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);

            var result = await service.AddCommentAsync(
                ticket.Id, "staff-1", "Can you confirm the model number?", isInternal: false, default);

            Assert.True(result.Success);
            var sent = Assert.Single(notifications.Sent);
            Assert.Equal("emp-1", sent.UserId);
            Assert.Contains("Can you confirm the model number?", sent.Message);
        }
    }

    [Fact]
    public async Task An_internal_comment_notifies_nobody()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);

            var result = await service.AddCommentAsync(
                ticket.Id, "staff-1", "Requester is a repeat offender.", isInternal: true, default);

            Assert.True(result.Success);

            // The filer cannot read internal comments, so a bell entry quoting one would leak
            // both the note and the fact that it exists.
            Assert.Empty(notifications.Sent);
        }
    }

    [Fact]
    public async Task A_comment_from_the_requester_notifies_nobody()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);

            var result = await service.AddCommentAsync(
                ticket.Id, "emp-1", "Any update on this?", isInternal: false, default);

            Assert.True(result.Success);
            Assert.Empty(notifications.Sent);
        }
    }

    [Fact]
    public async Task Fulfilling_a_request_tells_the_requester_which_asset_they_got()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var notifications = new FakeNotifications();
            var service = new TicketService(
                db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId),
                logger: null, lookups: null, notifications: notifications);

            var created = await service.CreateAsync(
                TicketTypes.Request, TicketCategory.Hardware, "Laptop for new officer", null,
                TicketPriority.Medium, null, "emp-1", default);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "AssetDesk-0356");

            var result = await service.FulfilAsync(
                created.Value!.Id, asset.Id, "Issued ThinkPad E14 with charger.", "staff-1", default);

            Assert.True(result.Success);
            var sent = Assert.Single(notifications.Sent);
            Assert.Equal("emp-1", sent.UserId);
            Assert.Equal(NotificationTypes.Success, sent.Type);

            // The asset tag is the useful part - it is what the requester goes and collects.
            Assert.Contains("AssetDesk-0356", sent.Message);
        }
    }

    /// <summary>
    /// The notification service is an optional constructor parameter, so every other test here
    /// hands it in by hand and would keep passing even if the container never supplied one -
    /// which would leave the feature dead in production and nothing to show for it. This
    /// composes the service the way Program.cs does and checks a notification actually comes
    /// out the far end.
    /// </summary>
    [Fact]
    public async Task Dependency_injection_supplies_the_notification_service()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");

            var notifications = new FakeNotifications();

            // Same shape and lifetimes as Program.cs, minus the pieces the ticket service
            // graph does not touch.
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<ITenantProvider>(new FakeTenantProvider(tenantId));
            services.AddSingleton<INotificationService>(notifications);
            services.AddSingleton<ILogger<TicketService>>(NullLogger<TicketService>.Instance);
            services.AddScoped<ITicketNumberAllocator, TicketNumberAllocator>();
            services.AddScoped<ILookupService, LookupService>();
            services.AddScoped<ITicketService, TicketService>();

            var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<ITicketService>();

            var created = await service.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null,
                TicketPriority.High, null, "emp-1", default);
            var ticket = created.Value!;

            await service.AssignAsync(ticket.Id, "staff-1", default);
            await service.ChangeStatusAsync(ticket.Id, TicketStatus.InProgress, default);
            await service.ResolveAsync(ticket.Id, "Replaced the fuser.", "staff-1", default);

            var sent = Assert.Single(notifications.Sent);
            Assert.Equal("emp-1", sent.UserId);
        }
    }

    [Fact]
    public async Task A_long_note_is_shortened_rather_than_dumped_into_the_bell()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (service, ticket, notifications) = await SetupAsync(db, tenantId);

            await service.AddCommentAsync(
                ticket.Id, "staff-1", new string('x', 500), isInternal: false, default);

            var sent = Assert.Single(notifications.Sent);
            Assert.EndsWith("…", sent.Message);
            Assert.DoesNotContain(new string('x', 200), sent.Message);
        }
    }
}
