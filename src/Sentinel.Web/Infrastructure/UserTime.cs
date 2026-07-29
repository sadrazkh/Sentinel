using System.Globalization;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Renders UTC instants in the viewer's own time zone.
/// <para>
/// Everything is stored in UTC; conversion happens here, at the edge, and only for display.
/// The calendar follows the current culture, so a Persian UI shows Persian dates without any
/// separate conversion code.
/// </para>
/// </summary>
public static class UserTime
{
    public const string DefaultTimeZoneId = "Asia/Tehran";

    public static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            // .NET maps IANA ids on Windows as well, so one id works on every host.
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad stored value must not break the page; UTC is the safe, obvious fallback.
            return TimeZoneInfo.Utc;
        }
    }

    public static DateTimeOffset ToZone(DateTimeOffset instant, string? timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant.ToUniversalTime(), ResolveZone(timeZoneId));

    public static string Format(DateTimeOffset instant, string? timeZoneId, string format = "yyyy/MM/dd HH:mm") =>
        ToZone(instant, timeZoneId).ToString(format, CultureInfo.CurrentCulture);

    /// <summary>ISO-8601 UTC value for the <c>datetime</c> attribute of a &lt;time&gt; element.</summary>
    public static string ToIso8601(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
