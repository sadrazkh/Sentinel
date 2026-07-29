using System.Collections.Frozen;
using System.Text.Json;

namespace Sentinel.Web.Localization;

/// <summary>
/// Loads <c>Resources/{culture}.json</c> once at startup and serves lookups from memory.
/// <para>
/// JSON rather than .resx: the files stay readable and reviewable in a pull request, and a
/// translator can edit one flat file per language. The rest of the application still talks to
/// <see cref="Microsoft.Extensions.Localization.IStringLocalizer"/>, so moving to .resx later
/// would touch only this class and the factory beside it.
/// </para>
/// </summary>
public sealed class LocalizationStore
{
    public const string DefaultCulture = "fa";
    public const string FallbackCulture = "en";

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _byCulture;
    private readonly ILogger<LocalizationStore> _logger;

    public LocalizationStore(IWebHostEnvironment environment, ILogger<LocalizationStore> logger)
    {
        _logger = logger;

        var directory = Path.Combine(environment.ContentRootPath, "Resources");
        var loaded = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var culture = Path.GetFileNameWithoutExtension(file);

                try
                {
                    var json = File.ReadAllText(file);
                    var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                  ?? new Dictionary<string, string>();

                    loaded[culture] = entries.ToFrozenDictionary(StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // A malformed translation file must not take the site down; the key is
                    // rendered instead and the problem is logged loudly.
                    _logger.LogError(ex, "Could not load translations from {File}.", file);
                }
            }
        }
        else
        {
            _logger.LogWarning("Translation directory {Directory} does not exist.", directory);
        }

        _byCulture = loaded.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AvailableCultures => _byCulture.Keys;

    /// <summary>
    /// Resolves a key against the requested culture, then its neutral form, then the fallback
    /// language. Returns <c>null</c> when the key is unknown everywhere.
    /// </summary>
    public string? Find(string culture, string key)
    {
        if (TryFind(culture, key, out var value))
        {
            return value;
        }

        var separator = culture.IndexOf('-');
        if (separator > 0 && TryFind(culture[..separator], key, out value))
        {
            return value;
        }

        return TryFind(FallbackCulture, key, out value) ? value : null;
    }

    public IReadOnlyDictionary<string, string> GetAll(string culture)
    {
        if (_byCulture.TryGetValue(culture, out var exact))
        {
            return exact;
        }

        var separator = culture.IndexOf('-');
        if (separator > 0 && _byCulture.TryGetValue(culture[..separator], out var neutral))
        {
            return neutral;
        }

        return _byCulture.TryGetValue(FallbackCulture, out var fallback)
            ? fallback
            : FrozenDictionary<string, string>.Empty;
    }

    private bool TryFind(string culture, string key, out string? value)
    {
        value = null;
        return _byCulture.TryGetValue(culture, out var entries) && entries.TryGetValue(key, out value);
    }
}
