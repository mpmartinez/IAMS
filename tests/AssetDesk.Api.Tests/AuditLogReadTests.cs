using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Covers the read side of the audit trail - AuditController. The write side lives in
/// AuditLogTests, which exercises the interceptor.
/// </summary>
public class AuditLogReadTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AuditController Controller(AppDbContext db, ITenantProvider provider) =>
        new(db, provider, NullLogger<AuditController>.Instance);

    private static PagedResponse<AuditLogDto> Unwrap(
        ActionResult<ApiResponse<PagedResponse<AuditLogDto>>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<PagedResponse<AuditLogDto>>>(ok.Value);
        Assert.True(response.Success);
        return response.Data!;
    }

    private static AuditLog Row(
        Guid tenantId,
        string entityType = nameof(Asset),
        string entityId = "1",
        string action = AuditActions.Updated,
        string? userId = "user-1",
        string? changes = null,
        DateTime? timestamp = null) => new()
        {
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            UserId = userId,
            Changes = changes,
            Timestamp = timestamp ?? new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)
        };

    // ---- tenant isolation -------------------------------------------------------------

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_entries()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            await TestDb.SeedTenantAsync(db, TenantB);
            db.AuditLogs.AddRange(
                Row(TenantA, entityId: "a-1"),
                Row(TenantB, entityId: "b-1"));
            await db.SaveChangesAsync();

            var page = Unwrap(await Controller(db, new FakeTenantProvider(TenantA)).GetAuditLog());

            Assert.Equal(1, page.TotalCount);
            Assert.Equal("a-1", Assert.Single(page.Items).EntityId);
        }
    }

    [Fact]
    public async Task A_super_admin_sees_every_tenant_and_can_tell_them_apart()
    {
        // The global query filter is bypassed for a super-admin, so the rows interleave. The
        // DTO carries TenantId precisely so that view is not silently misleading.
        var (db, conn) = TestDb.Create();
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            await TestDb.SeedTenantAsync(db, TenantB);
            db.AuditLogs.AddRange(Row(TenantA), Row(TenantB));
            await db.SaveChangesAsync();

            var page = Unwrap(await Controller(db, new FakeTenantProvider(null, isSuperAdmin: true))
                .GetAuditLog());

            Assert.Equal(2, page.TotalCount);
            Assert.Equal([TenantA, TenantB], page.Items.Select(i => i.TenantId).OrderBy(t => t));
        }
    }

    // ---- filters ----------------------------------------------------------------------

    [Fact]
    public async Task Filters_narrow_the_result_and_compose()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            db.AuditLogs.AddRange(
                Row(TenantA, nameof(Asset), "1", AuditActions.Created, "user-1"),
                Row(TenantA, nameof(Asset), "2", AuditActions.Updated, "user-1"),
                Row(TenantA, nameof(Ticket), "9", AuditActions.Created, "user-2"));
            await db.SaveChangesAsync();

            var controller = Controller(db, new FakeTenantProvider(TenantA));

            Assert.Equal(2, Unwrap(await controller.GetAuditLog(entityType: nameof(Asset))).TotalCount);
            Assert.Equal(2, Unwrap(await controller.GetAuditLog(action: AuditActions.Created)).TotalCount);
            Assert.Equal(2, Unwrap(await controller.GetAuditLog(userId: "user-1")).TotalCount);
            Assert.Equal(1, Unwrap(await controller.GetAuditLog(entityType: nameof(Asset), entityId: "2")).TotalCount);

            // Composed: Asset AND Created AND user-1 leaves exactly the first row.
            var composed = Unwrap(await controller.GetAuditLog(
                entityType: nameof(Asset), action: AuditActions.Created, userId: "user-1"));
            Assert.Equal("1", Assert.Single(composed.Items).EntityId);
        }
    }

    [Fact]
    public async Task The_date_range_is_inclusive_at_both_ends()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            var first = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            db.AuditLogs.AddRange(
                Row(TenantA, entityId: "before", timestamp: first.AddDays(-1)),
                Row(TenantA, entityId: "start", timestamp: first),
                Row(TenantA, entityId: "end", timestamp: first.AddDays(2)),
                Row(TenantA, entityId: "after", timestamp: first.AddDays(3)));
            await db.SaveChangesAsync();

            var page = Unwrap(await Controller(db, new FakeTenantProvider(TenantA))
                .GetAuditLog(fromDate: first, toDate: first.AddDays(2)));

            Assert.Equal(["end", "start"], page.Items.Select(i => i.EntityId));
        }
    }

    // ---- ordering and paging ----------------------------------------------------------

    [Fact]
    public async Task Entries_written_in_one_batch_page_deterministically()
    {
        // One SaveChanges writes a batch whose rows share a timestamp. Without the Id
        // tie-break the database may order them differently per query, and a row can appear
        // on two pages or on none.
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            var sameInstant = new DateTime(2026, 9, 2, 8, 30, 0, DateTimeKind.Utc);
            for (var i = 1; i <= 6; i++)
                db.AuditLogs.Add(Row(TenantA, entityId: i.ToString(), timestamp: sameInstant));
            await db.SaveChangesAsync();

            var controller = Controller(db, new FakeTenantProvider(TenantA));
            var first = Unwrap(await controller.GetAuditLog(page: 1, pageSize: 3));
            var second = Unwrap(await controller.GetAuditLog(page: 2, pageSize: 3));

            Assert.Equal(6, first.TotalCount);
            // Newest first: the highest Id leads, and the two pages partition the set.
            Assert.Equal(["6", "5", "4"], first.Items.Select(i => i.EntityId));
            Assert.Equal(["3", "2", "1"], second.Items.Select(i => i.EntityId));
        }
    }

    [Fact]
    public async Task Page_size_is_clamped_and_page_number_floored()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            db.AuditLogs.Add(Row(TenantA));
            await db.SaveChangesAsync();

            var controller = Controller(db, new FakeTenantProvider(TenantA));

            Assert.Equal(100, Unwrap(await controller.GetAuditLog(pageSize: 5000)).PageSize);
            Assert.Equal(1, Unwrap(await controller.GetAuditLog(pageSize: 0)).PageSize);
            Assert.Equal(1, Unwrap(await controller.GetAuditLog(page: -3)).Page);
        }
    }

    // ---- Changes parsing --------------------------------------------------------------

    [Fact]
    public async Task A_changes_blob_becomes_a_field_level_diff()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            db.AuditLogs.Add(Row(TenantA, changes:
                """{"Status":{"from":"Available","to":"InUse"},"PurchasePrice":{"from":null,"to":1250.5}}"""));
            await db.SaveChangesAsync();

            var entry = Assert.Single(Unwrap(
                await Controller(db, new FakeTenantProvider(TenantA)).GetAuditLog()).Items);

            var status = entry.Changes.Single(c => c.Field == "Status");
            Assert.Equal("Available", status.From);
            Assert.Equal("InUse", status.To);

            // Non-string values keep their JSON form; a JSON null reads as no value.
            var price = entry.Changes.Single(c => c.Field == "PurchasePrice");
            Assert.Null(price.From);
            Assert.Equal("1250.5", price.To);
        }
    }

    [Fact]
    public async Task Created_and_deleted_entries_carry_no_diff()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            db.AuditLogs.Add(Row(TenantA, action: AuditActions.Created, changes: null));
            await db.SaveChangesAsync();

            var entry = Assert.Single(Unwrap(
                await Controller(db, new FakeTenantProvider(TenantA)).GetAuditLog()).Items);

            Assert.Empty(entry.Changes);
        }
    }

    [Fact]
    public async Task An_unreadable_blob_costs_its_own_diff_not_the_whole_page()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            db.AuditLogs.AddRange(
                Row(TenantA, entityId: "broken", changes: "{not json at all"),
                Row(TenantA, entityId: "fine", changes: """{"Status":{"from":"A","to":"B"}}"""));
            await db.SaveChangesAsync();

            var page = Unwrap(await Controller(db, new FakeTenantProvider(TenantA)).GetAuditLog());

            Assert.Equal(2, page.TotalCount);
            Assert.Empty(page.Items.Single(i => i.EntityId == "broken").Changes);
            Assert.Single(page.Items.Single(i => i.EntityId == "fine").Changes);
        }
    }

    // ---- who did it -------------------------------------------------------------------

    [Fact]
    public async Task The_actor_is_named_or_falls_back_without_losing_the_entry()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            await TestDb.SeedUserAsync(db, TenantA, "user-1", "Ada Lovelace");
            db.AuditLogs.AddRange(
                Row(TenantA, entityId: "known", userId: "user-1"),
                // The audit row deliberately outlives the account it names.
                Row(TenantA, entityId: "deleted", userId: "user-gone"),
                Row(TenantA, entityId: "system", userId: null));
            await db.SaveChangesAsync();

            var page = Unwrap(await Controller(db, new FakeTenantProvider(TenantA)).GetAuditLog());

            Assert.Equal("Ada Lovelace", page.Items.Single(i => i.EntityId == "known").UserName);
            Assert.Equal("Unknown user", page.Items.Single(i => i.EntityId == "deleted").UserName);
            Assert.Equal("System", page.Items.Single(i => i.EntityId == "system").UserName);
        }
    }

    // ---- filter options ---------------------------------------------------------------

    [Fact]
    public async Task Filter_options_report_only_what_this_tenant_has()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(TenantA));
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, TenantA);
            await TestDb.SeedTenantAsync(db, TenantB);
            db.AuditLogs.AddRange(
                Row(TenantA, nameof(Asset), action: AuditActions.Created),
                Row(TenantA, nameof(Asset), action: AuditActions.Updated),
                Row(TenantB, nameof(Ticket), action: AuditActions.Deleted));
            await db.SaveChangesAsync();

            var result = await Controller(db, new FakeTenantProvider(TenantA)).GetFilterOptions();
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var options = Assert.IsType<ApiResponse<AuditFilterOptionsDto>>(ok.Value).Data!;

            Assert.Equal([nameof(Asset)], options.EntityTypes);
            Assert.Equal([AuditActions.Created, AuditActions.Updated], options.Actions);
        }
    }
}
