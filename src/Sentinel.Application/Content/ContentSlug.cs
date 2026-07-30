using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sentinel.Application.Content;

/// <summary>
/// The URL segment that identifies a documentation category or article.
/// <para>
/// Same shape as <see cref="Catalog.ApplicationKey"/> — lower-case letters, digits, single
/// hyphens — because it lands in a path like <c>/products/{key}/docs/{slug}</c>. A looser
/// alphabet would need escaping at every use site, and the one that got forgotten would be
/// the bug.
/// </para>
/// </summary>
public static partial class ContentSlug
{
    public const int MinLength = 2;
    public const int MaxLength = 120;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugRun();

    public static bool IsValid(string? slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && slug.Length is >= MinLength and <= MaxLength
        && Pattern().IsMatch(slug);

    /// <summary>
    /// Derives a slug from a title.
    /// <para>
    /// Persian and Arabic letters have no ASCII equivalent, so a Persian-only title reduces to
    /// nothing here and the caller must fall back — this returns <c>null</c> rather than
    /// inventing something, because a slug nobody can read is worse than one the operator was
    /// asked to type.
    /// </para>
    /// </summary>
    public static string? TryDerive(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Decompose so an accented Latin letter contributes its base letter rather than being
        // dropped entirely: "Café" should become "cafe", not "caf".
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        var candidate = NonSlugRun()
            .Replace(stripped.ToString().ToLowerInvariant(), "-")
            .Trim('-');

        if (candidate.Length > MaxLength)
        {
            // Cut at a hyphen where possible so the result is not a truncated word.
            candidate = candidate[..MaxLength].TrimEnd('-');

            var lastHyphen = candidate.LastIndexOf('-');
            if (lastHyphen >= MinLength)
            {
                candidate = candidate[..lastHyphen];
            }
        }

        return IsValid(candidate) ? candidate : null;
    }

    /// <summary>
    /// Makes a candidate unique against slugs already taken, by appending <c>-2</c>, <c>-3</c>
    /// and so on.
    /// <para>
    /// The suffix is applied to a base that has been shortened first, so a slug at the length
    /// limit does not silently lose its suffix and collide again.
    /// </para>
    /// </summary>
    public static string MakeUnique(string candidate, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var tail = $"-{suffix}";
            var room = MaxLength - tail.Length;
            var head = candidate.Length > room ? candidate[..room].TrimEnd('-') : candidate;
            var attempt = head + tail;

            if (!taken.Contains(attempt))
            {
                return attempt;
            }
        }

        throw new InvalidOperationException(
            $"Could not derive a unique slug from '{candidate}' after 998 attempts.");
    }
}
