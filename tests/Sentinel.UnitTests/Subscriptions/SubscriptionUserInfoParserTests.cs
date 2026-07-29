using Sentinel.Application.Subscriptions;

namespace Sentinel.UnitTests.Subscriptions;

public sealed class SubscriptionUserInfoParserTests
{
    /// <summary>The exact header the sample subscription returns.</summary>
    private const string RealHeader =
        "upload=4414518; download=31219114; total=10737418240; expire=1787930497";

    [Fact]
    public void The_real_header_is_read_in_full()
    {
        var info = SubscriptionUserInfoParser.Parse(RealHeader);

        Assert.Equal(4_414_518, info.UploadBytes);
        Assert.Equal(31_219_114, info.DownloadBytes);
        Assert.Equal(10_737_418_240, info.TotalBytes);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_787_930_497),
            info.ExpiresAt);
    }

    [Fact]
    public void Used_and_remaining_are_derived_from_upload_plus_download()
    {
        var info = SubscriptionUserInfoParser.Parse(RealHeader);

        Assert.Equal(35_633_632, info.UsedBytes);
        Assert.Equal(10_701_784_608, info.RemainingBytes);
        Assert.Equal(0, info.UsedPercent);
    }

    [Fact]
    public void A_total_of_zero_means_unlimited_rather_than_exhausted()
    {
        // Panels use total=0 for unlimited. Reading it as a real quota would show every such
        // subscription as permanently out of data.
        var info = SubscriptionUserInfoParser.Parse("upload=1; download=2; total=0; expire=0");

        Assert.Null(info.TotalBytes);
        Assert.Null(info.RemainingBytes);
        Assert.Null(info.UsedPercent);
        Assert.False(info.IsQuotaExhausted);
    }

    [Fact]
    public void An_expiry_of_zero_means_no_expiry()
    {
        var info = SubscriptionUserInfoParser.Parse("expire=0");

        Assert.Null(info.ExpiresAt);
        Assert.False(info.IsExpiredAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_exhausted_quota_is_reported()
    {
        var info = SubscriptionUserInfoParser.Parse("upload=600; download=400; total=1000");

        Assert.Equal(0, info.RemainingBytes);
        Assert.Equal(100, info.UsedPercent);
        Assert.True(info.IsQuotaExhausted);
    }

    [Fact]
    public void Reporting_more_used_than_the_quota_does_not_produce_a_negative_remainder()
    {
        var info = SubscriptionUserInfoParser.Parse("upload=900; download=900; total=1000");

        Assert.Equal(0, info.RemainingBytes);
        Assert.Equal(100, info.UsedPercent);
    }

    [Fact]
    public void An_expiry_in_the_past_is_reported_as_expired()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();

        var info = SubscriptionUserInfoParser.Parse($"expire={past}");

        Assert.True(info.IsExpiredAt(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage without any pairs")]
    [InlineData("upload=; download=; total=")]
    public void A_missing_or_unusable_header_yields_nothing_rather_than_throwing(string? header)
    {
        var info = SubscriptionUserInfoParser.Parse(header);

        Assert.Null(info.TotalBytes);
        Assert.Null(info.ExpiresAt);
    }

    [Fact]
    public void Unknown_keys_and_odd_spacing_are_tolerated()
    {
        var info = SubscriptionUserInfoParser.Parse(
            "  UPLOAD=10 ;download=20;  something=else ; total=100  ");

        Assert.Equal(10, info.UploadBytes);
        Assert.Equal(20, info.DownloadBytes);
        Assert.Equal(100, info.TotalBytes);
    }

    [Fact]
    public void A_wildly_out_of_range_expiry_is_discarded_rather_than_throwing()
    {
        var info = SubscriptionUserInfoParser.Parse("expire=99999999999999999");

        Assert.Null(info.ExpiresAt);
    }

    [Fact]
    public void Negative_counters_are_discarded()
    {
        var info = SubscriptionUserInfoParser.Parse("upload=-5; download=-10; total=100");

        Assert.Null(info.UploadBytes);
        Assert.Null(info.DownloadBytes);
    }
}
