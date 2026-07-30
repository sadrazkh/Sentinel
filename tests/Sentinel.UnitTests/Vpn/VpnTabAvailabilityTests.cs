using Sentinel.Vpn.Plans;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// Which tabs the VPN product page offers. A tab with nothing behind it must not be shown — a
/// heading that leads to an empty panel is worse than no heading.
/// </summary>
public sealed class VpnTabAvailabilityTests
{
    private static VpnTabAvailability Nothing => new(false, 0, 0, 0, 0);

    [Fact]
    public void Overview_is_always_offered()
    {
        // It carries the product description, which exists even before any plan is configured.
        Assert.True(Nothing.IsAvailable(VpnProductTab.Overview));
    }

    [Theory]
    [InlineData(VpnProductTab.Services)]
    [InlineData(VpnProductTab.Configurations)]
    [InlineData(VpnProductTab.Downloads)]
    [InlineData(VpnProductTab.Tutorials)]
    public void An_empty_tab_is_not_offered(VpnProductTab tab) =>
        Assert.False(Nothing.IsAvailable(tab));

    [Fact]
    public void A_tab_with_content_is_offered()
    {
        var availability = new VpnTabAvailability(
            HasPlans: true, ServiceCount: 2, ConfigurationCount: 9, DownloadCount: 1, ArticleCount: 3);

        foreach (var tab in Enum.GetValues<VpnProductTab>())
        {
            Assert.True(availability.IsAvailable(tab), $"{tab} should be offered.");
        }
    }

    [Fact]
    public void Each_tab_reports_its_own_count()
    {
        var availability = new VpnTabAvailability(true, 2, 9, 1, 3);

        Assert.Equal(2, availability.CountFor(VpnProductTab.Services));
        Assert.Equal(9, availability.CountFor(VpnProductTab.Configurations));
        Assert.Equal(1, availability.CountFor(VpnProductTab.Downloads));
        Assert.Equal(3, availability.CountFor(VpnProductTab.Tutorials));

        // Overview is not a count of anything.
        Assert.Equal(0, availability.CountFor(VpnProductTab.Overview));
    }

    [Fact]
    public void Plans_alone_do_not_open_the_services_tab()
    {
        // A price list is not a service. Offering "My services" to somebody with none would be a
        // heading onto an empty panel.
        var availability = new VpnTabAvailability(
            HasPlans: true, ServiceCount: 0, ConfigurationCount: 0, DownloadCount: 0, ArticleCount: 0);

        Assert.False(availability.IsAvailable(VpnProductTab.Services));
        Assert.True(availability.IsAvailable(VpnProductTab.Overview));
    }

    [Fact]
    public void An_unrecognised_tab_is_not_offered()
    {
        // A new enum member added without a branch must not become visible by default.
        var availability = new VpnTabAvailability(true, 5, 5, 5, 5);

        Assert.False(availability.IsAvailable((VpnProductTab)999));
    }
}
