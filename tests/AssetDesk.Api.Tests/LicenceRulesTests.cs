using AssetDesk.Api.Entities;
using AssetDesk.Shared;

namespace AssetDesk.Api.Tests;

public class LicenceRulesTests
{
    private static readonly DateTime Today = new(2026, 9, 13);

    [Fact]
    public void A_licence_with_no_expiry_is_perpetual()
    {
        Assert.Equal(LicenceRenewalStatuses.Perpetual, LicenceRules.RenewalStatus(null, Today));
        Assert.Null(LicenceRules.DaysUntilExpiry(null, Today));
    }

    [Theory]
    [InlineData(-1, LicenceRenewalStatuses.Expired)]
    [InlineData(0, LicenceRenewalStatuses.Due)]
    [InlineData(90, LicenceRenewalStatuses.Due)]
    [InlineData(91, LicenceRenewalStatuses.Active)]
    public void Renewal_status_follows_the_ninety_day_window(int daysAhead, string expected)
    {
        Assert.Equal(expected, LicenceRules.RenewalStatus(Today.AddDays(daysAhead), Today));
    }

    [Fact]
    public void Days_until_expiry_ignores_the_time_of_day()
    {
        // An expiry stored as midnight UTC and a "today" taken mid-afternoon are still one
        // calendar day apart, not zero days and a fraction.
        var expiresAt = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var afternoon = new DateTime(2026, 9, 13, 15, 30, 0, DateTimeKind.Utc);

        Assert.Equal(1, LicenceRules.DaysUntilExpiry(expiresAt, afternoon));
    }

    [Theory]
    [InlineData(LicenceRenewalStatuses.Due, true)]
    [InlineData(LicenceRenewalStatuses.Expired, true)]
    [InlineData(LicenceRenewalStatuses.Active, false)]
    [InlineData(LicenceRenewalStatuses.Perpetual, false)]
    public void Only_due_and_expired_licences_need_renewal_attention(string status, bool expected)
    {
        Assert.Equal(expected, LicenceRules.NeedsRenewalAttention(status));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ABC12", "****")]
    [InlineData("ABCD123", "****")]
    [InlineData("ABCD1234", "****-1234")]
    [InlineData("XXXXX-XXXXX-XXXXX-X7Q2", "****-X7Q2")]
    public void A_key_is_masked_to_at_most_its_last_four_characters(string? key, string? expected)
    {
        Assert.Equal(expected, LicenceRules.Mask(key));
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(null, AssetStatus.Retired, true)]
    [InlineData(null, AssetStatus.Lost, true)]
    [InlineData(null, AssetStatus.InUse, false)]
    [InlineData(null, AssetStatus.Maintenance, false)]
    public void A_seat_is_reclaimable_when_its_holder_is_gone(bool? userIsActive, string? assetStatus, bool expected)
    {
        Assert.Equal(expected, LicenceRules.IsReclaimable(userIsActive, assetStatus));
    }
}
