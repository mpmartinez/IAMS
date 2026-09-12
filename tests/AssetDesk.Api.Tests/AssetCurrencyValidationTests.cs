using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// A currency without a sane rate is how the figures get quietly wrong: a rate of zero
/// removes the asset from every total, and "PHP at 58.20" multiplies a peso figure by 58.
/// </summary>
public class AssetCurrencyValidationTests
{
    private static CreateAssetDto NewAsset(string currency, decimal rate) => new()
    {
        Name = "Test Laptop",
        DeviceType = DeviceTypes.Laptop,
        Status = AssetStatus.Available,
        PurchasePrice = 1200m,
        Currency = currency,
        ExchangeRate = rate
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_rate_of_zero_or_less_is_rejected(decimal rate)
    {
        var error = CurrencyRules.Validate(Currencies.USD, rate);
        Assert.NotNull(error);
        Assert.Contains("greater than zero", error);
    }

    [Fact]
    public void PHP_must_be_booked_at_exactly_one()
    {
        var error = CurrencyRules.Validate(Currencies.PHP, 58.20m);
        Assert.NotNull(error);
        Assert.Contains("PHP", error);
        Assert.Contains("exactly 1", error);
    }

    [Fact]
    public void PHP_at_one_is_accepted()
    {
        Assert.Null(CurrencyRules.Validate(Currencies.PHP, 1m));
    }

    [Fact]
    public void USD_at_a_real_rate_is_accepted()
    {
        Assert.Null(CurrencyRules.Validate(Currencies.USD, 58.20m));
    }

    [Fact]
    public void USD_left_at_the_default_rate_of_one_is_rejected()
    {
        var error = CurrencyRules.Validate(Currencies.USD, 1m);
        Assert.NotNull(error);
        Assert.Contains("rate is required", error);
    }

    // The rules above are only worth anything if the endpoints actually consult them. Until
    // these two arrived, deleting the rateError block from either CreateAsset or UpdateAsset
    // left the whole suite green.

    private static AssetsController ControllerFor(AppDbContext db) =>
        new(db, null!, null!, new LookupService(db), new AssetTagGenerator(db));

    private static string FailureMessage(ActionResult<ApiResponse<AssetDto>> result)
    {
        var body = Assert.IsType<ApiResponse<AssetDto>>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.NotNull(body.Message);
        return body.Message;
    }

    [Fact]
    public async Task CreateAsset_rejects_USD_at_the_default_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await ControllerFor(db).CreateAsset(NewAsset(Currencies.USD, 1m));

            Assert.Contains("rate is required", FailureMessage(result));
            Assert.Empty(db.Assets);
        }
    }

    [Fact]
    public async Task CreateAsset_accepts_USD_at_a_real_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            var result = await ControllerFor(db).CreateAsset(NewAsset(Currencies.USD, 58.20m));

            Assert.IsType<CreatedAtActionResult>(result.Result);
            var stored = db.Assets.Single();
            Assert.Equal(Currencies.USD, stored.Currency);
            Assert.Equal(58.20m, stored.ExchangeRate);
        }
    }

    [Fact]
    public async Task UpdateAsset_rejects_switching_to_USD_without_sending_a_rate()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");
            asset.PurchasePrice = 50000m;
            asset.Currency = Currencies.PHP;
            asset.ExchangeRate = 1m;
            await db.SaveChangesAsync();

            // Only the currency is sent. The stored rate is still 1, so the pair that WOULD be
            // saved is USD at 1 - the effective-pair logic (dto.X ?? asset.X) is what has to
            // catch this, and a check on dto.ExchangeRate alone would wave it through.
            var result = await ControllerFor(db).UpdateAsset(
                asset.Id, new UpdateAssetDto { Currency = Currencies.USD });

            Assert.Contains("rate is required", FailureMessage(result));

            db.ChangeTracker.Clear();
            var unchanged = db.Assets.Single();
            Assert.Equal(Currencies.PHP, unchanged.Currency);
            Assert.Equal(1m, unchanged.ExchangeRate);
        }
    }

    [Fact]
    public async Task UpdateAsset_rejects_a_rate_on_an_asset_that_stays_in_pesos()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0002");
            asset.Currency = Currencies.PHP;
            asset.ExchangeRate = 1m;
            await db.SaveChangesAsync();

            // The mirror image: a rate arrives with no currency, so the effective pair is the
            // stored PHP against the new rate.
            var result = await ControllerFor(db).UpdateAsset(
                asset.Id, new UpdateAssetDto { ExchangeRate = 58.20m });

            Assert.Contains("exactly 1", FailureMessage(result));

            db.ChangeTracker.Clear();
            Assert.Equal(1m, db.Assets.Single().ExchangeRate);
        }
    }
}
