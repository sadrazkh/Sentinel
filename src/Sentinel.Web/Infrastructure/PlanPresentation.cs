using System.Globalization;
using Sentinel.Domain.Billing;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Migration;
using Sentinel.Vpn.Plans;

namespace Sentinel.Web.Infrastructure;

/// <summary>Formats plan terms and maps the plan enums onto localisation keys.</summary>
public static class PlanPresentation
{
    public static string TabKey(VpnProductTab tab) => $"vpnTab.{Lower(tab)}";

    public static string EffectKey(AudienceEffect effect) => $"audienceEffect.{Lower(effect)}";

    public static string RuleKindKey(AudienceRuleKind kind) => $"audienceKind.{Lower(kind)}";

    public static string ServiceStatusKey(CustomerServiceStatus status) =>
        $"serviceStatus.{Lower(status)}";

    /// <summary>
    /// The badge a service's status wears.
    /// <para>
    /// Three of the nine states are a customer's normal life (working, waiting, finished) and one —
    /// <see cref="CustomerServiceStatus.NeedsAttention"/> — is the portal admitting it does not know.
    /// That one is deliberately a warning rather than a danger: nothing is broken from the member's
    /// side yet, and an alarming badge for "we are checking" is how a support queue fills up.
    /// </para>
    /// </summary>
    public static string ServiceStatusBadgeClass(CustomerServiceStatus status) => status switch
    {
        CustomerServiceStatus.Active => "badge--success",
        CustomerServiceStatus.Pending or CustomerServiceStatus.Provisioning => "badge--info",
        CustomerServiceStatus.NeedsAttention => "badge--warning",
        CustomerServiceStatus.Expired or CustomerServiceStatus.Exhausted => "badge--danger",
        _ => "badge--neutral",
    };

    public static string MigrationStepKey(MigrationStep step) => $"migrationStep.{Lower(step)}";

    public static string WalletKindKey(WalletTransactionKind kind) => $"walletKind.{Lower(kind)}";

    /// <summary>
    /// The badge a migration step wears.
    /// <para>
    /// <see cref="MigrationStep.Detaching"/> is a warning rather than a success even though nothing
    /// has gone wrong: it is the window in which the customer is live on two panels, and an operator
    /// glancing at the page should see the one row that is costing something.
    /// </para>
    /// </summary>
    public static string MigrationStepBadgeClass(MigrationStep step) => step switch
    {
        MigrationStep.Completed => "badge--success",
        MigrationStep.Planned or MigrationStep.Creating or MigrationStep.Verifying => "badge--info",
        MigrationStep.Detaching or MigrationStep.NeedsAttention => "badge--warning",
        MigrationStep.Abandoned => "badge--danger",
        _ => "badge--neutral",
    };

    /// <summary>
    /// A duration reduced to one unit and its localisation key — enough resolution to judge a
    /// migration window, and left for the view to render so the unit is not a hard-coded "m" on a
    /// Persian page.
    /// </summary>
    public static (string Key, int Value) DescribeDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? ("duration.hours", (int)span.TotalHours)
            : span.TotalMinutes >= 1
                ? ("duration.minutes", (int)span.TotalMinutes)
                : ("duration.seconds", (int)span.TotalSeconds);
    }

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
    /// An amount of money, plainly.
    /// <para>
    /// Separate from <see cref="DescribePrice"/> because that one renders zero as "Free" — right for
    /// a price list, wrong for a balance. An empty wallet holds nothing; it is not free.
    /// </para>
    /// </summary>
    public static string DescribeMoney(long minorUnits, string currency)
    {
        var exponent = MinorUnitExponent(currency);
        var major = minorUnits / Math.Pow(10, exponent);

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
