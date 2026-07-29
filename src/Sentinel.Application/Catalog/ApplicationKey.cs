using System.Text.RegularExpressions;

namespace Sentinel.Application.Catalog;

/// <summary>
/// The stable slug that identifies an application in URLs and configuration.
/// <para>
/// Constrained to lower-case letters, digits and single hyphens because it appears in the
/// launch path <c>/apps/{key}/open</c>. Anything looser would need escaping at every use, and
/// the one that got forgotten would be the bug.
/// </para>
/// </summary>
public static partial class ApplicationKey
{
    public const int MinLength = 2;
    public const int MaxLength = 64;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length is >= MinLength and <= MaxLength
        && Pattern().IsMatch(key);
}
