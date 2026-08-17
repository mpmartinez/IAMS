using System.Security.Claims;
using IAMS.Api.Authorization;
using IAMS.Api.Controllers;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAMS.Api.Tests;

/// <summary>
/// Regression coverage for the role-check -&gt; permission-claim conversion in
/// TicketCommentsController, TicketsController and AssetsController. These controllers are
/// constructed directly (no WebApplicationFactory in this suite) with a hand-built
/// ClaimsPrincipal on ControllerContext.HttpContext.User - the same technique
/// TicketCommentVisibilityTests uses one layer down, at the mapper.
///
/// Each test here is written to fail against the pre-fix role-based predicate
/// (`User.IsInRole("Admin") || User.IsInRole("Staff")`) and pass against the current
/// permission-claim predicate - see task-5-tests-report.md for the discrimination check.
/// </summary>
public class PermissionGateTests
{
    private static ClaimsPrincipal BuildPrincipal(
        string[]? roles = null, string[]? permissions = null, string userId = "user-1")
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles ?? [])
            claims.Add(new Claim(ClaimTypes.Role, role));
        foreach (var permission in permissions ?? [])
            claims.Add(new Claim(Permissions.ClaimType, permission));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static void SetUser(ControllerBase controller, ClaimsPrincipal principal) =>
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

    private static TicketService BuildTicketService(Data.AppDbContext db, Guid tenantId) =>
        new(db, new TicketNumberAllocator(db), new FakeTenantProvider(tenantId));

    private static TicketsController BuildTicketsController(
        ITicketService tickets, Data.AppDbContext db, Guid tenantId) =>
        new(
            tickets,
            db,
            new UnusedSubscriptionService(),
            new FakeTenantProvider(tenantId),
            NullLogger<TicketsController>.Instance);

    private static AssetsController BuildAssetsController(Data.AppDbContext db) =>
        new(db, new UnusedQrCodeService(), new UnusedAssetImportService(), new UnusedLookupService());

    // These three dependencies are never touched by the code paths under test (scan/{tag} and
    // Tickets.Get read straight from the DbContext), so a throwing stub both satisfies the
    // constructor and proves the assumption: if a test ever hits one of these, it fails loudly
    // instead of silently returning a default value that could mask a real behavioural change.
    private class UnusedSubscriptionService : ISubscriptionService
    {
        public Task<bool> CanCreateAssetAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> CanCreateUserAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes) => throw new NotSupportedException();
        public Task<bool> CanCreateTicketAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateAssetCountAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateUserCountAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateStorageUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<TenantUsageDto> GetUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> IsSubscriptionActiveAsync(Guid tenantId) => throw new NotSupportedException();
    }

    private class UnusedQrCodeService : IQrCodeService
    {
        public byte[] GeneratePng(string content, int pixelsPerModule = 10) => throw new NotSupportedException();
        public string GenerateSvg(string content, int pixelsPerModule = 10) => throw new NotSupportedException();
        public string GenerateAssetUrl(string assetTag, string? baseUrl = null) => throw new NotSupportedException();
    }

    private class UnusedAssetImportService : IAssetImportService
    {
        public Task<ImportAssetsResultDto> ImportAsync(Stream xlsxStream, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private class UnusedLookupService : ILookupService
    {
        public Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<string>> GetActiveValuesAsync(string lookupType, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private class UnusedFileStorageService : IFileStorageService
    {
        public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName) => throw new NotSupportedException();
        public Task<bool> DeleteFileAsync(string storedFileName) => throw new NotSupportedException();
        public bool IsValidFileType(string contentType) => throw new NotSupportedException();
        public bool IsValidFileSize(long sizeBytes) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------------------------------
    // 1. The inversion guard: TicketCommentsController.Add
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_forbids_a_queue_viewer_without_manage_rights_from_posting_an_internal_comment()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Queue Viewer, No Manage Rights");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            var controller = new TicketCommentsController(tickets, db);
            // Holds the Staff role label (so it clears the read gate under both the old and
            // new predicate - this isolates the assertion to the write gate specifically) plus
            // TicketsQueue, but a tenant admin has NOT granted this principal TicketsManage.
            // Not the ticket's requester either. Only TicketsManage may author an internal note.
            SetUser(controller, BuildPrincipal(
                roles: [Roles.Staff], permissions: [Permissions.TicketsQueue], userId: "staff-1"));

            var result = await controller.Add(
                created.Value!.Id,
                new AddTicketCommentRequest { Body = "Hidden from the requester", IsInternal = true },
                default);

            Assert.IsType<ForbidResult>(result.Result);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }

    // ---------------------------------------------------------------------------------------
    // 2. The revocation case: TicketsController.Get
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_forbids_a_Staff_role_holder_with_no_queue_claim_from_reading_another_users_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Staff Role, Revoked Queue Claim");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            var controller = BuildTicketsController(tickets, db, tenantId);
            // Under the old role-based code, IsInRole("Staff") alone let this user through.
            // Under permission claims, the Staff role with no TicketsQueue claim (revoked)
            // must be forbidden from reading someone else's ticket.
            SetUser(controller, BuildPrincipal(roles: [Roles.Staff], userId: "staff-1"));

            var result = await controller.Get(created.Value!.Id, default);

            Assert.IsType<ForbidResult>(result.Result);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 3. The grant case: TicketsController.Get
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_allows_a_custom_role_holder_with_the_queue_permission_and_includes_internal_comments()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "field-1", "Field Tech");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);
            await tickets.AddCommentAsync(created.Value!.Id, "field-1", "Internal triage note", true, default);

            var controller = BuildTicketsController(tickets, db, tenantId);
            // No built-in role claim at all - a tenant-defined custom role name - but does
            // hold the TicketsQueue permission claim directly.
            SetUser(controller, BuildPrincipal(
                roles: ["FieldTech"], permissions: [Permissions.TicketsQueue], userId: "field-1"));

            var result = await controller.Get(created.Value.Id, default);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<TicketDto>>(ok.Value);
            Assert.True(body.Success);
            Assert.Contains(body.Data!.Comments, c => c.IsInternal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 4. Financial redaction: AssetsController.GetAssetByTag
    // ---------------------------------------------------------------------------------------

    private static async Task<Guid> SeedAssetWithFinancialsAsync(Data.AppDbContext db, string assetTag)
    {
        var tenantId = Guid.NewGuid();
        await TestDb.SeedTenantAsync(db, tenantId);
        var asset = await TestDb.SeedAssetAsync(db, tenantId, assetTag);
        asset.PurchasePrice = 1234.56m;
        asset.PurchaseDate = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        asset.WarrantyProvider = "Dell ProSupport";
        await db.SaveChangesAsync();
        return tenantId;
    }

    [Fact]
    public async Task GetAssetByTag_redacts_financials_without_the_AssetsView_permission()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await SeedAssetWithFinancialsAsync(db, "LAP-001");

            var controller = BuildAssetsController(db);
            SetUser(controller, BuildPrincipal(userId: "emp-1")); // no roles, no permissions

            var result = await controller.GetAssetByTag("LAP-001");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<AssetDto>>(ok.Value);
            Assert.Null(body.Data!.PurchasePrice);
            Assert.Null(body.Data.PurchaseDate);
            Assert.Null(body.Data.WarrantyProvider);
        }
    }

    [Fact]
    public async Task GetAssetByTag_includes_financials_with_the_AssetsView_permission()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await SeedAssetWithFinancialsAsync(db, "LAP-002");

            var controller = BuildAssetsController(db);
            SetUser(controller, BuildPrincipal(permissions: [Permissions.AssetsView], userId: "staff-1"));

            var result = await controller.GetAssetByTag("LAP-002");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<AssetDto>>(ok.Value);
            Assert.Equal(1234.56m, body.Data!.PurchasePrice);
            Assert.NotNull(body.Data.PurchaseDate);
            Assert.Equal("Dell ProSupport", body.Data.WarrantyProvider);
        }
    }

    [Fact]
    public async Task GetAssetByTag_SuperAdmin_role_sees_financials_with_zero_permission_claims()
    {
        // Pins the SuperAdmin bypass in HasPermission as a deliberate decision: SuperAdmin
        // sees everything even holding no explicit permission claims at all.
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null, isSuperAdmin: true));
        using (db)
        using (conn)
        {
            await SeedAssetWithFinancialsAsync(db, "LAP-003");

            var controller = BuildAssetsController(db);
            SetUser(controller, BuildPrincipal(roles: [Roles.SuperAdmin], userId: "root-1"));

            var result = await controller.GetAssetByTag("LAP-003");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<AssetDto>>(ok.Value);
            Assert.Equal(1234.56m, body.Data!.PurchasePrice);
            Assert.NotNull(body.Data.PurchaseDate);
            Assert.Equal("Dell ProSupport", body.Data.WarrantyProvider);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 5. Owner without queue rights cannot see internal notes: TicketCommentsController.List
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task List_omits_internal_comments_for_the_requester_holding_only_TicketsFile()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Queue Worker");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);
            await tickets.AddCommentAsync(created.Value!.Id, "staff-1", "Visible to requester", false, default);
            await tickets.AddCommentAsync(created.Value.Id, "staff-1", "Internal-only triage note", true, default);

            var controller = new TicketCommentsController(tickets, db);
            // The requester themself, holding the Staff role label (so the ownership read gate
            // passes identically under the old single-role check and the new permission check -
            // this isolates the assertion to the internal-comment EF filter) plus TicketsFile,
            // but NOT the TicketsQueue permission a tenant admin has revoked from this role.
            SetUser(controller, BuildPrincipal(
                roles: [Roles.Staff], permissions: [Permissions.TicketsFile], userId: "emp-1"));

            var result = await controller.List(created.Value.Id, default);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<List<TicketCommentDto>>>(ok.Value);
            Assert.True(body.Success);
            Assert.Single(body.Data!);
            Assert.DoesNotContain(body.Data!, c => c.IsInternal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 6. The not-my-ticket write gate: TicketCommentsController.Add and
    //    TicketAttachmentsController.UploadAttachment both require iams:tickets:manage (not the
    //    read-only iams:tickets:queue) to write to a ticket the actor does not own, while the
    //    ticket's own requester needs only iams:tickets:file. Each direction is a one-line
    //    predicate (`!CanManageQueue && ticket.RequesterUserId != CurrentUserId`) a refactor
    //    could silently revert either way, so both the forbidding and the allowing side get a
    //    dedicated test here rather than relying on the existing internal-comment test (which
    //    also trips the separate IsInternal-only gate and so does not discriminate this one).
    // ---------------------------------------------------------------------------------------

    /// <summary>Throws on every member - proves a Forbid short-circuits before the write path
    /// (the real ticket/asset services, or the ticket store itself) is ever touched.</summary>
    private sealed class ThrowingTicketService : ITicketService
    {
        public Task<ServiceResult<Ticket>> CreateAsync(string type, string category, string title, string? description, string priority, int? assetId, string requesterUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Ticket?> GetAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<Ticket> Items, int TotalCount, int Page, int PageSize)> ListAsync(TicketQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketSummary> GetSummaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult> AssignAsync(int id, string assigneeUserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult> ChangeStatusAsync(int id, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult> ResolveAsync(int id, string resolution, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult<TicketComment>> AddCommentAsync(int ticketId, string userId, string body, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServiceResult> FulfilAsync(int ticketId, int assetId, string resolution, string actingUserId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubFileStorageService : IFileStorageService
    {
        public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType) => Task.FromResult("stored-file.txt");
        public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName) => throw new NotSupportedException();
        public Task<bool> DeleteFileAsync(string storedFileName) => throw new NotSupportedException();
        public bool IsValidFileType(string contentType) => true;
        public bool IsValidFileSize(long sizeBytes) => true;
    }

    private sealed class StubUploadAllowedSubscriptionService : ISubscriptionService
    {
        public Task<bool> CanCreateAssetAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> CanCreateUserAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes) => Task.FromResult(true);
        public Task<bool> CanCreateTicketAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateAssetCountAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateUserCountAsync(Guid tenantId) => throw new NotSupportedException();
        public Task UpdateStorageUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<TenantUsageDto> GetUsageAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<bool> IsSubscriptionActiveAsync(Guid tenantId) => throw new NotSupportedException();
    }

    private sealed class StubActiveLookupService : ILookupService
    {
        public Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetActiveValuesAsync(string lookupType, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static IFormFile BuildFormFile(string content = "attachment body")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new Microsoft.AspNetCore.Http.FormFile(stream, 0, bytes.Length, "file", "note.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
    }

    [Fact]
    public async Task Add_forbids_a_queue_viewer_without_manage_rights_from_posting_a_non_internal_comment_on_someone_elses_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Queue Viewer, No Manage Rights");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            var controller = new TicketCommentsController(tickets, db);
            // Not internal this time - isolates the ownership check (CanManageQueue vs
            // ticket.RequesterUserId) from the separate "only manage may write internal notes"
            // gate the existing internal-comment test also trips.
            SetUser(controller, BuildPrincipal(permissions: [Permissions.TicketsQueue], userId: "staff-1"));

            var result = await controller.Add(
                created.Value!.Id,
                new AddTicketCommentRequest { Body = "Visible reply", IsInternal = false },
                default);

            Assert.IsType<ForbidResult>(result.Result);
            Assert.Equal(0, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task Add_allows_the_ticket_owner_holding_only_TicketsFile_to_comment_on_their_own_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            var controller = new TicketCommentsController(tickets, db);
            SetUser(controller, BuildPrincipal(permissions: [Permissions.TicketsFile], userId: "emp-1"));

            var result = await controller.Add(
                created.Value!.Id,
                new AddTicketCommentRequest { Body = "Following up on my own ticket", IsInternal = false },
                default);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var body = Assert.IsType<ApiResponse<TicketCommentDto>>(ok.Value);
            Assert.True(body.Success);
            Assert.Equal(1, await db.TicketComments.CountAsync());
        }
    }

    [Fact]
    public async Task UploadAttachment_forbids_a_queue_viewer_without_manage_rights_from_uploading_to_someone_elses_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");
            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Queue Viewer, No Manage Rights");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            // Every dependency past the permission gate throws - a passing test here can only
            // mean the controller returned Forbid before touching any of them.
            var controller = new TicketAttachmentsController(
                db, new UnusedFileStorageService(), new UnusedSubscriptionService(),
                new FakeTenantProvider(tenantId), new UnusedLookupService());
            SetUser(controller, BuildPrincipal(permissions: [Permissions.TicketsQueue], userId: "staff-1"));

            var result = await controller.UploadAttachment(
                created.Value!.Id, BuildFormFile(), "General", null, default);

            Assert.IsType<ForbidResult>(result.Result);
            Assert.Equal(0, await db.TicketAttachments.CountAsync());
        }
    }

    [Fact]
    public async Task UploadAttachment_allows_the_ticket_owner_holding_only_TicketsFile_to_upload_to_their_own_ticket()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Requester");

            var tickets = BuildTicketService(db, tenantId);
            var created = await tickets.CreateAsync(
                TicketTypes.Incident, TicketCategory.Hardware, "Printer jams", null, TicketPriority.Low,
                null, "emp-1", default);

            var controller = new TicketAttachmentsController(
                db, new StubFileStorageService(), new StubUploadAllowedSubscriptionService(),
                new FakeTenantProvider(tenantId), new StubActiveLookupService());
            SetUser(controller, BuildPrincipal(permissions: [Permissions.TicketsFile], userId: "emp-1"));

            var result = await controller.UploadAttachment(
                created.Value!.Id, BuildFormFile(), "General", null, default);

            var created201 = Assert.IsType<CreatedAtActionResult>(result.Result);
            var body = Assert.IsType<ApiResponse<TicketAttachmentDto>>(created201.Value);
            Assert.True(body.Success);
            Assert.Equal(1, await db.TicketAttachments.CountAsync());
        }
    }

    // ---------------------------------------------------------------------------------------
    // 7. TicketsController.Fulfil requires iams:assignments:assign in addition to ticket-queue
    //    management - a one-line predicate (User.HasPermission(Permissions.AssignmentsAssign))
    //    a refactor could silently drop, re-opening the gap where queue management alone let a
    //    holder hand out assets through this endpoint.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Fulfil_forbids_an_actor_with_queue_management_but_not_assignment_permission()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            // ThrowingTicketService proves FulfilAsync is never reached - the Forbid must come
            // from the permission check alone, before the ticket store is touched.
            var controller = BuildTicketsController(new ThrowingTicketService(), db, tenantId);
            // Holds queue-management (what the [Authorize(Policy = "CanManageTicketQueue")] route
            // policy would check in production) but NOT AssignmentsAssign - the explicit in-body
            // check this fix added.
            SetUser(controller, BuildPrincipal(permissions: [Permissions.TicketsManage], userId: "staff-1"));

            var result = await controller.Fulfil(
                1, new FulfilTicketRequest { AssetId = 1, Resolution = "Issued." }, default);

            Assert.IsType<ForbidResult>(result.Result);
        }
    }
}
