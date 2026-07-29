using System.Globalization;

namespace Sentinel.Web.Localization;

/// <summary>
/// The two cultures the portal ships with. Kept as an explicit allow-list because the value
/// arrives from a cookie or query string and is fed to the localisation middleware.
/// </summary>
public static class PortalCultures
{
    public const string Persian = "fa-IR";
    public const string English = "en-US";

    public static readonly string[] All = [Persian, English];

    public static bool IsSupported(string? culture) =>
        culture is not null && All.Contains(culture, StringComparer.OrdinalIgnoreCase);

    /// <summary>Right-to-left is a property of the language, so it is derived, never configured twice.</summary>
    public static bool IsRightToLeft(CultureInfo culture) => culture.TextInfo.IsRightToLeft;

    public static string DirectionAttribute(CultureInfo culture) => IsRightToLeft(culture) ? "rtl" : "ltr";
}
