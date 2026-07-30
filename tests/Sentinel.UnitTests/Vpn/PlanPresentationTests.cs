using System.Globalization;
using Sentinel.Web.Infrastructure;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// How plan terms read. Both of these get a customer's money or quota wrong when they are wrong, so
/// they are worth pinning down.
/// </summary>
public sealed class PlanPresentationTests
{
    private const string Unlimited = "Unlimited";
    private const string Free = "Free";

    // -------------------------------------------------------------------------- traffic ----

    [Fact]
    public void Zero_traffic_reads_as_unlimited() =>
        // The panel's own convention, carried through to the label rather than shown as "0".
        Assert.Equal(Unlimited, PlanPresentation.DescribeTraffic(0, Unlimited));

    [Fact]
    public void A_negative_allowance_also_reads_as_unlimited_rather_than_as_nonsense() =>
        Assert.Equal(Unlimited, PlanPresentation.DescribeTraffic(-1, Unlimited));

    [Theory]
    [InlineData(50L * 1024 * 1024 * 1024, "50 GiB")]
    [InlineData(1L * 1024 * 1024 * 1024, "1 GiB")]
    [InlineData(100L * 1024 * 1024 * 1024, "100 GiB")]
    public void A_round_allowance_drops_the_decimal(long bytes, string expected) =>
        Assert.Equal(expected, PlanPresentation.DescribeTraffic(bytes, Unlimited));

    [Fact]
    public void A_terabyte_scale_allowance_switches_unit() =>
        Assert.Equal("2 TiB", PlanPresentation.DescribeTraffic(2L * 1024 * 1024 * 1024 * 1024, Unlimited));

    [Fact]
    public void A_sub_gigabyte_allowance_is_shown_in_mebibytes() =>
        Assert.Equal("500 MiB", PlanPresentation.DescribeTraffic(500L * 1024 * 1024, Unlimited));

    [Fact]
    public void A_fractional_allowance_keeps_one_decimal() =>
        Assert.Equal("1.5 GiB", PlanPresentation.DescribeTraffic((long)(1.5 * 1024 * 1024 * 1024), Unlimited));

    // ---------------------------------------------------------------------------- price ----

    [Fact]
    public void Zero_reads_as_free() =>
        Assert.Equal(Free, PlanPresentation.DescribePrice(0, "IRR", Free));

    [Fact]
    public void A_rial_price_is_not_divided_by_a_hundred()
    {
        // IRR has no minor unit. Applying the usual two-decimal rule would show a price a hundred
        // times too small — the kind of mistake a customer notices and we do not.
        var formatted = PlanPresentation.DescribePrice(2_500_000, "IRR", Free);

        Assert.Contains("IRR", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(".", formatted, StringComparison.Ordinal);

        var digits = new string(formatted.Where(char.IsAsciiDigit).ToArray());
        Assert.Equal("2500000", digits);
    }

    [Fact]
    public void A_two_decimal_currency_is_divided_by_a_hundred()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal("12.34 EUR", PlanPresentation.DescribePrice(1234, "EUR", Free));
    }

    [Fact]
    public void An_unknown_currency_gets_the_two_decimal_default()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal("9.99 XYZ", PlanPresentation.DescribePrice(999, "XYZ", Free));
    }

    [Fact]
    public void A_price_is_grouped_in_the_readers_own_culture()
    {
        // Under fa-IR, .NET keeps ASCII digits but uses the Persian thousands separator (U+066C)
        // rather than a comma. That separator is the observable difference, and it is the reason
        // this formats through the current culture rather than the invariant one.
        using var persian = new CultureScope("fa-IR");
        var inPersian = PlanPresentation.DescribePrice(2_500_000, "IRR", Free);
        persian.Dispose();

        using var english = new CultureScope("en-US");
        var inEnglish = PlanPresentation.DescribePrice(2_500_000, "IRR", Free);

        Assert.Contains('٬', inPersian);
        Assert.Equal("2,500,000 IRR", inEnglish);
        Assert.NotEqual(inEnglish, inPersian);
    }

    /// <summary>Restores the ambient culture, so one test cannot bleed into the next.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
