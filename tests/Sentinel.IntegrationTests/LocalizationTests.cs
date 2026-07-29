using System.Text.Json;
using System.Text.RegularExpressions;
using Sentinel.Application.Access;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Web.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Guards the translation catalogues.
/// <para>
/// A missing key does not crash anything — the localiser falls back to rendering the key
/// itself — which is exactly why it needs a test. Without one, "admin.audit.title" ships to a
/// customer as the page heading and nobody notices until they do.
/// </para>
/// </summary>
public sealed partial class LocalizationTests
{
    [GeneratedRegex("L\\[\\$?\"([a-z][A-Za-z0-9.]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizerUsageRegex();

    private static readonly string WebRoot = LocateWebProject();

    private static Dictionary<string, string> LoadCatalogue(string culture)
    {
        var path = Path.Combine(WebRoot, "Resources", $"{culture}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Theory]
    [InlineData("fa")]
    [InlineData("en")]
    public void Each_catalogue_is_valid_json_and_not_empty(string culture)
    {
        var catalogue = LoadCatalogue(culture);

        Assert.NotEmpty(catalogue);
        Assert.All(catalogue, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void The_two_catalogues_define_exactly_the_same_keys()
    {
        var persian = LoadCatalogue("fa");
        var english = LoadCatalogue("en");

        var missingFromEnglish = persian.Keys.Except(english.Keys).OrderBy(k => k).ToList();
        var missingFromPersian = english.Keys.Except(persian.Keys).OrderBy(k => k).ToList();

        Assert.True(
            missingFromEnglish.Count == 0,
            $"Keys present in fa.json but missing from en.json: {string.Join(", ", missingFromEnglish)}");

        Assert.True(
            missingFromPersian.Count == 0,
            $"Keys present in en.json but missing from fa.json: {string.Join(", ", missingFromPersian)}");
    }

    [Fact]
    public void Every_key_the_views_ask_for_exists_in_both_catalogues()
    {
        var persian = LoadCatalogue("fa");
        var english = LoadCatalogue("en");

        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(WebRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            foreach (Match match in LocalizerUsageRegex().Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);

        // Interpolated lookups such as L[$"role.{name}"] are skipped by the regex on purpose:
        // their key is only known at run time, so a static check would report false failures.
        var missing = used
            .Where(key => !persian.ContainsKey(key) || !english.ContainsKey(key))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Views reference keys that are not in both catalogues: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The keys the regex above deliberately skips: those built from an enum value at run time.
    /// They are the ones most likely to be forgotten, because adding an enum member compiles
    /// perfectly and only shows up as a raw key on the page.
    /// </summary>
    [Fact]
    public void Every_key_computed_from_an_enum_exists_in_both_catalogues()
    {
        var persian = LoadCatalogue("fa");
        var english = LoadCatalogue("en");

        var expected = new List<string>();

        foreach (var status in Enum.GetValues<ProductAccessStatus>())
        {
            expected.Add(ProductPresentation.StatusKey(status));
        }

        foreach (var status in Enum.GetValues<ProductReleaseStatus>())
        {
            expected.Add(ProductPresentation.ReleaseKey(status));
        }

        foreach (var type in Enum.GetValues<ProductType>())
        {
            expected.Add(ProductPresentation.TypeKey(type));
        }

        // Built inline by the admin product form, one checkbox per capability.
        foreach (var capability in Enum.GetValues<ProductCapability>())
        {
            if (capability == ProductCapability.None)
            {
                continue;
            }

            expected.Add($"capability.{capability.ToString().ToLowerInvariant()}");
        }

        foreach (var source in Enum.GetValues<EntitlementSource>())
        {
            expected.Add(ProductPresentation.SourceKey(source));
        }

        foreach (var reason in Enum.GetValues<AccessDenialReason>())
        {
            expected.Add(AccessPresentation.DenialReasonKey(reason));
        }

        foreach (var status in Enum.GetValues<MembershipStatus>())
        {
            expected.Add(AccessPresentation.MembershipStatusKey(status));
        }

        foreach (var tier in Enum.GetValues<MembershipTier>())
        {
            expected.Add(AccessPresentation.TierKey(tier));
        }

        var missing = expected
            .Distinct(StringComparer.Ordinal)
            .Where(key => !persian.ContainsKey(key) || !english.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Enum-derived keys missing from a catalogue: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Placeholder_counts_match_between_the_two_languages()
    {
        // A message with {0} in one language and none in the other throws at render time in
        // whichever language got it wrong, and only on the page that uses it.
        var persian = LoadCatalogue("fa");
        var english = LoadCatalogue("en");

        var mismatched = persian
            .Where(pair => english.ContainsKey(pair.Key))
            .Where(pair => CountPlaceholders(pair.Value) != CountPlaceholders(english[pair.Key]))
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(
            mismatched.Count == 0,
            $"These keys use a different number of placeholders per language: {string.Join(", ", mismatched)}");
    }

    private static int CountPlaceholders(string value) =>
        PlaceholderRegex().Matches(value).Select(m => m.Value).Distinct().Count();

    [GeneratedRegex(@"\{\d+\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Walks up from the test assembly to the repository root, so the test does not depend on
    /// where the runner happens to place the binaries.
    /// </summary>
    private static string LocateWebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Sentinel.Web");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Sentinel.Web project from the test output.");
    }
}
