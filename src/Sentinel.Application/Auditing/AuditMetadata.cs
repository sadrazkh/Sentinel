using System.Text.Json;

namespace Sentinel.Application.Auditing;

/// <summary>
/// Builder for the JSON blob attached to an audit entry.
/// <para>
/// Key names are screened against <see cref="ForbiddenKeyFragments"/> so a secret can never
/// reach the audit table by accident, and values are length-capped so one oversized payload
/// cannot blow past the column limit. Screening happens here, at the single place metadata is
/// constructed, rather than being left to each call site to remember.
/// </para>
/// </summary>
public sealed class AuditMetadata
{
    public const int MaxValueLength = 256;
    public const int MaxEntries = 20;

    /// <summary>
    /// Case-insensitive substrings that must never appear in a metadata key. Matching a key
    /// is a programming error and throws, so it is caught by tests rather than silently
    /// leaking into storage.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenKeyFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "credential",
        "apikey", "api_key", "privatekey", "private_key", "hash",
        "securitystamp", "security_stamp", "cookie", "otp", "authorization",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static AuditMetadata Create() => new();

    public AuditMetadata Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        GuardKey(key);

        if (_values.Count >= MaxEntries && !_values.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"An audit metadata bag may hold at most {MaxEntries} entries.");
        }

        _values[key] = Truncate(value);
        return this;
    }

    public AuditMetadata Set(string key, object? value) =>
        Set(key, value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            DateTimeOffset d => d.ToUniversalTime().ToString("O"),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        });

    /// <summary>Records a before/after pair for a changed field.</summary>
    public AuditMetadata SetChange(string field, object? from, object? to) =>
        Set($"{field}.from", from).Set($"{field}.to", to);

    public bool IsEmpty => _values.Count == 0;

    public string? ToJson() => _values.Count == 0 ? null : JsonSerializer.Serialize(_values, SerializerOptions);

    private static void GuardKey(string key)
    {
        foreach (var fragment in ForbiddenKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Audit metadata key '{key}' looks like it carries a secret ('{fragment}'). " +
                    "Secrets must never be written to the audit log.",
                    nameof(key));
            }
        }
    }

    private static string? Truncate(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";
    }
}
