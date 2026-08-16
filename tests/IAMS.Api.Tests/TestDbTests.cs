using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Tests;

public class TestDbTests
{
    [Fact]
    public async Task Create_gives_a_usable_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create();
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "IAMS-0001");

            var found = await db.Assets.SingleAsync(a => a.Id == asset.Id);

            Assert.Equal("IAMS-0001", found.AssetTag);
            Assert.Equal(tenantId, found.TenantId);
        }
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_assets()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(mine));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, mine);
            await TestDb.SeedTenantAsync(db, theirs);
            await TestDb.SeedAssetAsync(db, mine, "MINE-1");
            await TestDb.SeedAssetAsync(db, theirs, "THEIRS-1");

            var visible = await db.Assets.ToListAsync();

            Assert.Single(visible);
            Assert.Equal("MINE-1", visible[0].AssetTag);
        }
    }
}
