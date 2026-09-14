using System.Security.Claims;
using System.Text;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Cross-tenant isolation for AttachmentsController - the same shape as
/// AssetTenantIsolationTests. The Asset and Attachment global query filters are bypassed outright
/// for a super admin, so a controller that leans on them lets a super admin whose current tenant
/// is A list, download, upload to and delete attachments on tenant B's assets by id. An upload
/// is worse than a read: SaveChanges stamps the new row with tenant A's TenantId, so the bytes
/// hang off B's asset but are metered against A's storage.
///
/// Every caller here has a current tenant, so a "Select an organisation first." guard cannot be
/// what refuses them - only an explicit TenantId predicate can.
/// </summary>
public class AttachmentTenantIsolationTests
{
    private sealed class RecordingFileStorage : IFileStorageService
    {
        public List<string> Saved { get; } = [];
        public List<string> Read { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType)
        {
            Saved.Add(fileName);
            return Task.FromResult($"stored-{fileName}");
        }

        public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName)
        {
            Read.Add(storedFileName);
            return Task.FromResult<(Stream, string)?>((new MemoryStream([1, 2, 3]), "application/pdf"));
        }

        public Task<bool> DeleteFileAsync(string storedFileName)
        {
            Deleted.Add(storedFileName);
            return Task.FromResult(true);
        }

        public bool IsValidFileType(string contentType) => true;
        public bool IsValidFileSize(long sizeBytes) => true;
    }

    private static AttachmentsController ControllerFor(
        Data.AppDbContext db, ITenantProvider tenantProvider, IFileStorageService storage) =>
        new(db, storage, new FakeLookupService(), new FakeSubscriptionService(), tenantProvider)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "root-1"), new Claim(ClaimTypes.Role, Roles.SuperAdmin)],
                        "TestAuth"))
                }
            }
        };

    private sealed record Seeded(
        Data.AppDbContext Db, Microsoft.Data.Sqlite.SqliteConnection Conn, Guid TenantA,
        Asset OwnAsset, Attachment OwnAttachment, Asset OtherAsset, Attachment OtherAttachment);

    /// <summary>
    /// Seeds tenants A and B with an asset and one attachment each. The context itself runs as a
    /// super admin in tenant A, so the global filter lets every row through - which is the
    /// production condition being reproduced.
    /// </summary>
    private static async Task<Seeded> SeedAsync()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantA, isSuperAdmin: true));

        await TestDb.SeedTenantAsync(db, tenantA);
        await TestDb.SeedTenantAsync(db, tenantB);
        // The caller's own user record, so an upload's UploadedByUser reference can load.
        await TestDb.SeedUserAsync(db, tenantA, "root-1", "Root");
        await TestDb.SeedUserAsync(db, tenantB, "user-b", "Uploader B");

        var own = await TestDb.SeedAssetAsync(db, tenantA, "LAP-A-001");
        var other = await TestDb.SeedAssetAsync(db, tenantB, "LAP-B-001");

        Attachment AttachmentOf(Guid tenantId, Asset asset, string uploader, long size) => new()
        {
            TenantId = tenantId,
            AssetId = asset.Id,
            FileName = $"{asset.AssetTag}.pdf",
            StoredFileName = $"stored-{asset.AssetTag}.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = size,
            Category = AttachmentCategories.Receipt,
            UploadedByUserId = uploader
        };
        var ownAttachment = AttachmentOf(tenantA, own, "root-1", 100);
        var otherAttachment = AttachmentOf(tenantB, other, "user-b", 5000);
        db.Attachments.AddRange(ownAttachment, otherAttachment);
        await db.SaveChangesAsync();

        // Precondition: the filter really is bypassed, so a pass below cannot come from it.
        Assert.True(await db.Assets.AnyAsync(a => a.Id == other.Id));
        Assert.True(await db.Attachments.AnyAsync(a => a.Id == otherAttachment.Id));

        db.ChangeTracker.Clear();
        return new Seeded(db, conn, tenantA, own, ownAttachment, other, otherAttachment);
    }

    private static FakeTenantProvider SuperAdminIn(Guid tenantId) => new(tenantId, isSuperAdmin: true);

    private static async Task<List<Attachment>> AllAttachmentsAsync(Data.AppDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.Attachments.IgnoreQueryFilters().ToListAsync();
    }

    private static IFormFile PdfOf(int bytes) =>
        new FormFile(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', bytes))), 0, bytes, "file", "upload.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

    // ---------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_super_admin_caller_cannot_list_another_tenants_asset_attachments()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetAttachments(s.OtherAsset.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_list_their_own_tenants_asset_attachments()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetAttachments(s.OwnAsset.Id);

            var list = Assert.IsType<List<AttachmentDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(s.OwnAttachment.Id, Assert.Single(list).Id);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_read_another_tenants_asset_attachment_summary()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetSummary(s.OtherAsset.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_callers_attachment_summary_counts_only_their_own_tenant()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetSummary(s.OwnAsset.Id);

            var summary = Assert.IsType<ApiResponse<AttachmentSummaryDto>>(
                Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
            Assert.Equal(1, summary.TotalCount);
            Assert.Equal(100, summary.TotalSizeBytes);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_read_another_tenants_attachment_metadata()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetAttachment(s.OtherAsset.Id, s.OtherAttachment.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_read_their_own_tenants_attachment_metadata()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), new RecordingFileStorage())
                .GetAttachment(s.OwnAsset.Id, s.OwnAttachment.Id);

            Assert.IsType<OkObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_download_another_tenants_attachment()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .DownloadAttachment(s.OtherAsset.Id, s.OtherAttachment.Id);

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Empty(storage.Read);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_download_their_own_tenants_attachment()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .DownloadAttachment(s.OwnAsset.Id, s.OwnAttachment.Id);

            Assert.IsType<FileStreamResult>(result);
            Assert.Equal([s.OwnAttachment.StoredFileName], storage.Read);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_super_admin_caller_cannot_delete_another_tenants_attachment()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .DeleteAttachment(s.OtherAsset.Id, s.OtherAttachment.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Empty(storage.Deleted);
            Assert.Contains(await AllAttachmentsAsync(s.Db), a => a.Id == s.OtherAttachment.Id);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_delete_their_own_tenants_attachment()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .DeleteAttachment(s.OwnAsset.Id, s.OwnAttachment.Id);

            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal([s.OwnAttachment.StoredFileName], storage.Deleted);
            Assert.DoesNotContain(await AllAttachmentsAsync(s.Db), a => a.Id == s.OwnAttachment.Id);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_cannot_upload_onto_another_tenants_asset()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .UploadAttachment(s.OtherAsset.Id, PdfOf(64), AttachmentCategories.Receipt);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            // Refused before the file is written, so nothing is orphaned in storage either.
            Assert.Empty(storage.Saved);
            Assert.Equal(2, (await AllAttachmentsAsync(s.Db)).Count);
        }
    }

    [Fact]
    public async Task A_super_admin_caller_can_still_upload_onto_their_own_tenants_asset()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var result = await ControllerFor(s.Db, SuperAdminIn(s.TenantA), storage)
                .UploadAttachment(s.OwnAsset.Id, PdfOf(64), AttachmentCategories.Receipt);

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var added = Assert.Single(await AllAttachmentsAsync(s.Db),
                a => a.Id != s.OwnAttachment.Id && a.Id != s.OtherAttachment.Id);
            Assert.Equal(s.OwnAsset.Id, added.AssetId);
            Assert.Equal(s.TenantA, added.TenantId);
        }
    }

    // Separate from the predicate tests above: this one is the guard. With no current tenant the
    // tenant-stamping in SaveChanges skips, and a super admin's bypassed filter would otherwise
    // find any tenant's asset.
    [Fact]
    public async Task Attachment_actions_with_no_current_tenant_are_refused()
    {
        var s = await SeedAsync();
        using (s.Db)
        using (s.Conn)
        {
            var storage = new RecordingFileStorage();
            var controller = ControllerFor(s.Db, new FakeTenantProvider(null, isSuperAdmin: true), storage);

            Assert.IsType<BadRequestObjectResult>((await controller.GetAttachments(s.OtherAsset.Id)).Result);
            Assert.IsType<BadRequestObjectResult>((await controller.GetSummary(s.OtherAsset.Id)).Result);
            Assert.IsType<BadRequestObjectResult>(
                (await controller.GetAttachment(s.OtherAsset.Id, s.OtherAttachment.Id)).Result);
            Assert.IsType<BadRequestObjectResult>(
                await controller.DownloadAttachment(s.OtherAsset.Id, s.OtherAttachment.Id));
            Assert.IsType<BadRequestObjectResult>(
                (await controller.DeleteAttachment(s.OtherAsset.Id, s.OtherAttachment.Id)).Result);
            Assert.IsType<BadRequestObjectResult>(
                (await controller.UploadAttachment(s.OtherAsset.Id, PdfOf(64), AttachmentCategories.Receipt)).Result);

            Assert.Empty(storage.Saved);
            Assert.Empty(storage.Read);
            Assert.Empty(storage.Deleted);
            Assert.Equal(2, (await AllAttachmentsAsync(s.Db)).Count);
        }
    }
}
