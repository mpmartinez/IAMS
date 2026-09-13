using System.Text.Json;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

public class LicenceAuditTests
{
    private const string OldKey = "AAAAA-BBBBB-CCCCC-OLD1";
    private const string NewKey = "DDDDD-EEEEE-FFFFF-NEW2";

    private sealed class StubUser(string? id) : ICurrentUserAccessor
    {
        public string? GetUserId() => id;
    }

    private static (AppDbContext Db, SqliteConnection Conn) CreateAudited()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(
                new StubUser("admin-1"), NullLogger<AuditSaveChangesInterceptor>.Instance))
            .Options;

        var db = new AppDbContext(options, new FakeTenantProvider(null, isSuperAdmin: true));
        db.Database.EnsureCreated();
        return (db, connection);
    }

    [Fact]
    public async Task Changing_a_licence_key_is_recorded_without_either_value()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Adobe Acrobat Pro", LicenceModel = LicenceModels.PerUser,
                LicenceKey = OldKey
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            licence.LicenceKey = NewKey;
            licence.Notes = "Rotated after staff departure";
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(
                a => a.EntityType == nameof(SoftwareLicence) && a.Action == AuditActions.Updated);

            Assert.DoesNotContain(OldKey, update.Changes);
            Assert.DoesNotContain(NewKey, update.Changes);

            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("from").GetString());
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("to").GetString());

            // Redaction is for the key only - the rest of the row is still audited normally.
            Assert.Equal("Rotated after staff departure", changes["Notes"].GetProperty("to").GetString());
        }
    }

    [Fact]
    public async Task Setting_a_key_where_there_was_none_records_only_that_one_now_exists()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Adobe Acrobat Pro", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            licence.LicenceKey = NewKey;
            await db.SaveChangesAsync();

            var update = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.Updated);
            var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(update.Changes!)!;

            Assert.Equal(JsonValueKind.Null, changes["LicenceKey"].GetProperty("from").ValueKind);
            Assert.Equal("[redacted]", changes["LicenceKey"].GetProperty("to").GetString());
            Assert.DoesNotContain(NewKey, update.Changes);
        }
    }

    [Fact]
    public async Task Assigning_and_releasing_a_seat_are_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "user-1", "Maria Santos");
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Microsoft 365", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            var seat = new LicenceSeatAssignment
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, UserId = "user-1", AssignedByUserId = "admin-1"
            };
            db.LicenceSeatAssignments.Add(seat);
            await db.SaveChangesAsync();

            seat.ReleasedAt = DateTime.UtcNow;
            seat.ReleasedByUserId = "admin-1";
            await db.SaveChangesAsync();

            var entries = await db.AuditLogs
                .Where(a => a.EntityType == nameof(LicenceSeatAssignment))
                .OrderBy(a => a.Id)
                .ToListAsync();

            Assert.Equal(new[] { AuditActions.Created, AuditActions.Updated }, entries.Select(e => e.Action));
            Assert.All(entries, e => Assert.Equal(seat.Id.ToString(), e.EntityId));
        }
    }

    [Fact]
    public async Task Recording_an_entitlement_is_audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = CreateAudited();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var licence = new SoftwareLicence
            {
                TenantId = tenantId, Name = "Microsoft 365", LicenceModel = LicenceModels.PerUser
            };
            db.SoftwareLicences.Add(licence);
            await db.SaveChangesAsync();

            db.LicenceEntitlements.Add(new LicenceEntitlement
            {
                TenantId = tenantId, SoftwareLicenceId = licence.Id, SeatsAdded = 50, Cost = 35000m,
                CreatedByUserId = "admin-1"
            });
            await db.SaveChangesAsync();

            Assert.Single(await db.AuditLogs
                .Where(a => a.EntityType == nameof(LicenceEntitlement) && a.Action == AuditActions.Created)
                .ToListAsync());
        }
    }
}
