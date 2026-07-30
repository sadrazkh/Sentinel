using System.Globalization;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Plans;

namespace Sentinel.Web.Infrastructure;

/// <summary>Formats plan terms and maps the plan enums onto localisation keys.</summary>
public static class PlanPresentation
{
    public static string TabKey(VpnProductTab tab) => $"vpnTab.{Lower(tab)}";

    public static string EffectKey(AudienceEffect effect) => $"audienceEffect.{Lower(effect)}";

    public static string RuleKindKey(AudienceRuleKind kind) => $"audienceKind.{Lower(kind)}";

    /// <summary>
    /// A traffic allowance as a human quantity.
    /// <para>
    /// Binary units, matching what a customer's own device reports. Plans are round numbers of GiB
    /// in practice, so the decimal is dropped when it would only ever read as ".0".
    /// </para>
    /// </summary>
    public static string DescribeTraffic(long bytes, string unlimitedLabel)
    {
        if (bytes <= 0)
        {
            return unlimitedLabel;
        }

        const double gibibyte = 1024d * 1024 * 1024;
        var gib = bytes / gibibyte;

        if (gib >= 1024)
        {
            var tib = gib / 1024;
            return Format(tib, "TiB");
        }

        return gib >= 1 ? Format(gib, "GiB") : Format(bytes / (1024d * 1024), "MiB");

        static string Format(double value, string unit)
        {
            var rounded = Math.Round(value, 1);
            var format = Math.Abs(rounded - Math.Round(rounded)) < 0.05 ? "0" : "0.#";

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{rounded.ToString(format, CultureInfo.InvariantCulture)} {unit}");
        }
    }

    /// <summary>
    /// A price from its minor units.
    /// <para>
    /// The exponent is per currency: rials have none, most currencies have two. Dividing by a fixed
    /// hundred would show a rial price a hundred times too small.
    /// </para>
    /// </summary>
    public static string DescribePrice(long minorUnits, string currency, string freeLabel)
    {
        if (minorUnits <= 0)
        {
            return freeLabel;
        }

        var exponent = MinorUnitExponent(currency);
        var major = minorUnits / Math.Pow(10, exponent);

        // Grouped and rendered in the current culture, so a Persian page shows Persian digits.
        return major.ToString($"N{exponent}", CultureInfo.CurrentCulture) + " " + currency;
    }

    /// <summary>
    /// Currencies whose smallest unit *is* the unit. A short list because it only needs to cover
    /// what this portal actually prices in; anything unlisted gets the two-decimal default.
    /// </summary>
    private static int MinorUnitExponent(string currency) => currency.ToUpperInvariant() switch
    {
        "IRR" or "IRT" or "JPY" or "KRW" or "VND" or "CLP" or "ISK" => 0,
        _ => 2,
    };

    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString()!;

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
