using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Controllers;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

/// <summary>
/// Attachment delete is uploader-only. This is a swap, not a widening: before this rule the action
/// carried [Authorize(Policy = "CanManageTicketQueue")], so queue managers could delete and the
/// person who uploaded a file could not. QueueManager_WhoDidNotUpload_CannotDelete is the half that
/// changed, and it fails against the old behaviour.
/// </summary>
public class TicketAttachmentDeleteTests
{
    private static ClaimsPrincipal Principal(string userId, params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TicketAttachmentsController Controller(
        AppDbContext db, IFileStorageService storage, Guid tenantId, ClaimsPrincipal user)
    {
        var controller = new TicketAttachmentsController(
            db, storage, new FakeSubscriptionService(),
            new FakeTenantProvider(tenantId), new FakeLookupService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
        return controller;
    }

    private static async Task<(Ticket Ticket, TicketAttachment Attachment)> SeedAsync(
        AppDbContext db, Guid tenantId, string uploaderId)
    {
        await TestDb.SeedTenantAsync(db, tenantId);
        await TestDb.SeedUserAsync(db, tenantId, uploaderId, "Uploader");

        var ticket = new Ticket
        {
            TenantId = tenantId,
            TicketNumber = 1,
            Type = TicketTypes.Incident,
            Category = TicketCategory.Other,
            Title = "Broken screen",
            Status = TicketStatus.New,
            Priority = TicketPriority.Medium,
            RequesterUserId = uploaderId
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var attachment = new TicketAttachment
        {
            TenantId = tenantId,
            TicketId = ticket.Id,
            FileName = "photo.jpg",
            StoredFileName = "stored-photo.jpg",
            ContentType = "image/jpeg",
            FileSizeBytes = 1234,
            Category = "Other",
            UploadedByUserId = uploaderId
        };
        db.TicketAttachments.Add(attachment);
        await db.SaveChangesAsync();

        return (ticket, attachment);
    }

    [Fact]
    public async Task Uploader_CanDeleteTheirOwnAttachment()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using var _ = conn;
        var (ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, tenantId, Principal("uploader-1"));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsNotType<ForbidResult>(result.Result);
        Assert.False(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Contains("stored-photo.jpg", storage.Deleted);
    }

    [Fact]
    public async Task AnotherUser_CannotDelete_AndTheAttachmentSurvives()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using var _ = conn;
        var (ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, tenantId, Principal("someone-else"));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.True(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task QueueManager_WhoDidNotUpload_CannotDelete()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using var _ = conn;
        var (ticket, attachment) = await SeedAsync(db, tenantId, "uploader-1");

        var storage = new FakeFileStorageService();
        var controller = Controller(db, storage, tenantId,
            Principal("it-staff", Permissions.TicketsManage, Permissions.TicketsQueue));

        var result = await controller.DeleteAttachment(ticket.Id, attachment.Id, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.True(await db.TicketAttachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Empty(storage.Deleted);
    }
}
