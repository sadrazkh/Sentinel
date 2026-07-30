using Sentinel.Application.Content;

namespace Sentinel.UnitTests.Content;

public sealed class ContentSlugTests
{
    [Theory]
    [InlineData("getting-started")]
    [InlineData("windows")]
    [InlineData("v2")]
    [InlineData("a1-b2-c3")]
    public void A_wellformed_slug_is_accepted(string slug) =>
        Assert.True(ContentSlug.IsValid(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]                       // shorter than the minimum
    [InlineData("Getting-Started")]         // upper case
    [InlineData("getting started")]         // space
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("../escape")]
    [InlineData("under_score")]
    [InlineData("راهنما")]                  // no ASCII form
    [InlineData("percent%20encoded")]
    public void A_malformed_slug_is_refused(string? slug) =>
        Assert.False(ContentSlug.IsValid(slug));

    [Fact]
    public void A_slug_at_the_length_limit_is_accepted_and_one_over_is_not()
    {
        Assert.True(ContentSlug.IsValid(new string('a', ContentSlug.MaxLength)));
        Assert.False(ContentSlug.IsValid(new string('a', ContentSlug.MaxLength + 1)));
    }

    // ------------------------------------------------------------------------- deriving ----

    [Theory]
    [InlineData("Getting Started", "getting-started")]
    [InlineData("  Trim  Me  ", "trim-me")]
    [InlineData("Windows 11 / Setup", "windows-11-setup")]
    [InlineData("Café Réservé", "cafe-reserve")]
    [InlineData("C# and .NET", "c-and-net")]
    [InlineData("Multiple   spaces", "multiple-spaces")]
    public void A_title_derives_the_expected_slug(string title, string expected) =>
        Assert.Equal(expected, ContentSlug.TryDerive(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("راهنمای نصب")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void A_title_with_no_ascii_form_derives_nothing(string? title) =>
        // Returning null rather than inventing a slug: an unreadable URL segment is worse than
        // asking the operator to type one.
        Assert.Null(ContentSlug.TryDerive(title));

    [Fact]
    public void A_derived_slug_is_always_valid()
    {
        // Whatever comes out of TryDerive must pass IsValid, or the two would disagree and the
        // save path would reject its own suggestion.
        var titles = new[]
        {
            "A", "AB", "Hello, World!", new string('x', 400), "Ünïcödé Tïtlé",
            "1234567890", "a-b-c", "  --  ", "Mixed راهنما English",
        };

        foreach (var title in titles)
        {
            if (ContentSlug.TryDerive(title) is { } slug)
            {
                Assert.True(ContentSlug.IsValid(slug), $"'{title}' derived the invalid slug '{slug}'.");
            }
        }
    }

    [Fact]
    public void A_long_title_is_cut_at_a_word_boundary()
    {
        var slug = ContentSlug.TryDerive(string.Join(' ', Enumerable.Repeat("segment", 40)))!;

        Assert.True(slug.Length <= ContentSlug.MaxLength);
        Assert.DoesNotContain("segmen-", slug, StringComparison.Ordinal);
        Assert.EndsWith("segment", slug, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ uniqueness ----

    [Fact]
    public void An_unused_slug_is_returned_unchanged() =>
        Assert.Equal("guide", ContentSlug.MakeUnique("guide", new HashSet<string>()));

    [Fact]
    public void A_taken_slug_gains_a_numeric_suffix()
    {
        var taken = new HashSet<string> { "guide" };

        Assert.Equal("guide-2", ContentSlug.MakeUnique("guide", taken));
    }

    [Fact]
    public void The_suffix_climbs_past_every_taken_variant()
    {
        var taken = new HashSet<string> { "guide", "guide-2", "guide-3" };

        Assert.Equal("guide-4", ContentSlug.MakeUnique("guide", taken));
    }

    [Fact]
    public void A_slug_at_the_length_limit_still_gets_a_usable_suffix()
    {
        // Appending without shortening would push it over the limit, get truncated by the column,
        // and collide again — the bug this shortening exists to prevent.
        var atLimit = new string('a', ContentSlug.MaxLength);
        var taken = new HashSet<string> { atLimit };

        var unique = ContentSlug.MakeUnique(atLimit, taken);

        Assert.True(unique.Length <= ContentSlug.MaxLength);
        Assert.True(ContentSlug.IsValid(unique));
        Assert.DoesNotContain(unique, taken);
        Assert.EndsWith("-2", unique, StringComparison.Ordinal);
    }
}

public sealed class ContentLinkPolicyTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/docs/guide?x=1#top")]
    [InlineData("https://sub.example.co.uk/a/b")]
    [InlineData("mailto:support@example.com")]
    [InlineData("/products/vault")]
    [InlineData("/products/vault/docs/getting-started")]
    public void An_allowed_target_passes(string target) =>
        Assert.True(ContentLinkPolicy.IsAllowed(target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("http://example.com/x")]           // plain http would downgrade the reader
    [InlineData("http://localhost:5000/x")]        // no loopback exception in prose
    [InlineData("https://user:secret@example.com")]
    [InlineData("mailto:notanaddress")]
    [InlineData("example.com/no-scheme")]
    public void A_refused_target_fails(string? target) =>
        Assert.False(ContentLinkPolicy.IsAllowed(target));

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("/x:y")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r/evil.example")]
    [InlineData("https://exa\tmple.com/x")]
    public void A_path_that_a_browser_would_read_as_external_is_refused(string target) =>
        // "//host" is protocol-relative and "/\host" is treated the same way by browsers, so
        // neither is the portal-relative link it appears to be. A raw tab or newline is the
        // sneakier form: browsers strip it before parsing, so "/<tab>/evil" becomes "//evil".
        Assert.False(ContentLinkPolicy.IsAllowed(target));

    [Fact]
    public void A_percent_encoded_control_character_stays_a_relative_path()
    {
        // Not the same hazard: a percent-escape is not decoded before the URL is resolved, so
        // this really does stay on the portal's own origin.
        Assert.True(ContentLinkPolicy.IsAllowed("/%09/still-relative"));
    }

    [Fact]
    public void An_over_long_target_is_refused() =>
        Assert.False(ContentLinkPolicy.IsAllowed(
            "https://example.com/" + new string('a', ContentLinkPolicy.MaxLength)));
}
