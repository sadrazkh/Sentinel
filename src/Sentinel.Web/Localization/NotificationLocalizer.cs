using System.Globalization;
using Sentinel.Application.Notifications;

namespace Sentinel.Web.Localization;

/// <summary>
/// Resolves notification text against an explicit culture, reading the same JSON catalogues the
/// views use.
/// <para>
/// Deliberately not built on <c>IStringLocalizer</c>: that resolves against
/// <see cref="CultureInfo.CurrentUICulture"/>, which in a background sweep is whatever the
/// thread happens to carry rather than the language of the person being written to.
/// </para>
/// </summary>
public sealed class NotificationLocalizer : INotificationLocalizer
{
    private readonly LocalizationStore _store;

    public NotificationLocalizer(LocalizationStore store) => _store = store;

    public string Get(string key, string? culture, params object?[] arguments)
    {
        var resolved = _store.Find(culture ?? LocalizationStore.DefaultCulture, key) ?? key;

        if (arguments.Length == 0)
        {
            return resolved;
        }

        try
        {
            // Numbers and dates inside the message follow the recipient's language too.
            var formatCulture = ResolveFormatCulture(culture);
            return string.Format(formatCulture, resolved, arguments);
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder must not stop a warning going out.
            return resolved;
        }
    }

    private static CultureInfo ResolveFormatCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return CultureInfo.GetCultureInfo(PortalCultures.Persian);
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
