using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Sentinel.Web.Localization;

public sealed class JsonStringLocalizer : IStringLocalizer
{
    private readonly LocalizationStore _store;

    public JsonStringLocalizer(LocalizationStore store) => _store = store;

    public LocalizedString this[string name]
    {
        get
        {
            var value = _store.Find(CultureInfo.CurrentUICulture.Name, name);
            return new LocalizedString(name, value ?? name, resourceNotFound: value is null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = _store.Find(CultureInfo.CurrentUICulture.Name, name);
            var format = value ?? name;

            // CurrentCulture (not CurrentUICulture) so numbers and dates inside a message
            // follow the user's formatting conventions.
            var formatted = string.Format(CultureInfo.CurrentCulture, format, arguments);
            return new LocalizedString(name, formatted, resourceNotFound: value is null);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _store.GetAll(CultureInfo.CurrentUICulture.Name)
            .Select(pair => new LocalizedString(pair.Key, pair.Value, resourceNotFound: false));
}

public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly LocalizationStore _store;

    public JsonStringLocalizerFactory(LocalizationStore store) => _store = store;

    // Every resource type shares one flat catalogue: the portal is small enough that split
    // per-view resource files would cost more bookkeeping than they save.
    public IStringLocalizer Create(Type resourceSource) => new JsonStringLocalizer(_store);

    public IStringLocalizer Create(string baseName, string location) => new JsonStringLocalizer(_store);
}

/// <summary>Marker type for <c>IStringLocalizer&lt;SharedResource&gt;</c> injection.</summary>
public sealed class SharedResource;
