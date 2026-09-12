using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Tag generation existed twice before this class - once in AssetsController and once in
/// AssetImportService, identical except for the importer's sequence cache. These pin the
/// behaviour both had, so the extraction can be shown to preserve it.
/// </summary>
public class AssetTagGeneratorTests
{
    [Theory]
    [InlineData("Laptop", "LAP")]
    [InlineData("Desktop", "DSK")]
    [InlineData("Monitor", "MON")]
    [InlineData("Phone", "PHN")]
    [InlineData("Tablet", "TAB")]
    [InlineData("Printer", "PRN")]
    [InlineData("Network", "NET")]
    [InlineData("Server", "SVR")]
    [InlineData("Peripheral", "PER")]
    [InlineData("Software", "SFT")]
    [InlineData("Other", "OTH")]
    [InlineData("Drone", "OTH")]
    public void Each_device_type_maps_to_its_prefix(string deviceType, string expected)
    {
        Assert.Equal(expected, AssetTagGenerator.PrefixFor(deviceType));
    }

    [Fact]
    public async Task The_first_tag_of_the_day_is_sequence_one()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);

            var tag = await generator.NextAsync(DeviceTypes.Laptop, new Dictionary<string, int>());

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", tag);
        }
    }

    [Fact]
    public async Task Successive_calls_sharing_a_cache_do_not_collide()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);
            var cache = new Dictionary<string, int>();

            var first = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var second = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var third = await generator.NextAsync(DeviceTypes.Laptop, cache);

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", first);
            Assert.Equal($"LAP-{today}-0002", second);
            Assert.Equal($"LAP-{today}-0003", third);
        }
    }

    [Fact]
    public async Task The_sequence_continues_past_tags_already_in_the_database()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            await TestDb.SeedAssetAsync(db, tenantId, $"LAP-{today}-0007");

            var generator = new AssetTagGenerator(db);
            var tag = await generator.NextAsync(DeviceTypes.Laptop, new Dictionary<string, int>());

            Assert.Equal($"LAP-{today}-0008", tag);
        }
    }

    [Fact]
    public async Task Different_device_types_have_independent_sequences()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var generator = new AssetTagGenerator(db);
            var cache = new Dictionary<string, int>();

            var laptop = await generator.NextAsync(DeviceTypes.Laptop, cache);
            var monitor = await generator.NextAsync(DeviceTypes.Monitor, cache);

            var today = DateTime.UtcNow.ToString("yyyyMMdd");
            Assert.Equal($"LAP-{today}-0001", laptop);
            Assert.Equal($"MON-{today}-0001", monitor);
        }
    }
}
