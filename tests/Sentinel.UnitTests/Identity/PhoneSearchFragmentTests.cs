using Sentinel.Application.Identity;

namespace Sentinel.UnitTests.Identity;

/// <summary>
/// Reducing a typed phone number to something that can match a stored one.
/// <para>
/// The two forms never look alike: a member's number is stored as "+989120000001" and nobody types
/// that. Searching for "0912…" matched nothing at all, because the leading zero is a national trunk
/// prefix the stored value does not carry — the term is not a substring of the number it names.
/// </para>
/// </summary>
public sealed class PhoneSearchFragmentTests
{
    /// <summary>How a member's number is actually stored, for the assertions below.</summary>
    private const string Stored = "+989120000001";

    [Theory]
    // As people write it: the national form with a trunk zero.
    [InlineData("09120000001", "9120000001")]
    // Part of it, which is what somebody types when they only remember the start.
    [InlineData("0912", "912")]
    // Already international, with and without the plus.
    [InlineData("+989120000001", "989120000001")]
    [InlineData("989120000001", "989120000001")]
    // The other international prefix.
    [InlineData("00989120000001", "989120000001")]
    // Separators people paste in from a contacts app.
    [InlineData("0912 000 0001", "9120000001")]
    [InlineData("0912-000-0001", "9120000001")]
    [InlineData("(0912) 0000001", "9120000001")]
    public void A_typed_number_is_reduced_to_digits_that_can_match(string typed, string expected) =>
        Assert.Equal(expected, PhoneNumberNormalizer.ToSearchFragment(typed));

    [Theory]
    [InlineData("۰۹۱۲۰۰۰۰۰۰۱")]
    [InlineData("٠٩١٢٠٠٠٠٠٠١")]
    public void Persian_and_arabic_digits_are_mapped(string typed)
    {
        // An operator on a Persian keyboard types Persian digits without thinking about it. Left
        // unmapped they are simply different characters and match nothing.
        var fragment = PhoneNumberNormalizer.ToSearchFragment(typed);

        Assert.Equal("9120000001", fragment);
        Assert.Contains(fragment!, Stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("09120000001")]
    [InlineData("0912")]
    [InlineData("9120000001")]
    [InlineData("+98912")]
    [InlineData("۰۹۱۲")]
    public void Every_shape_somebody_types_is_a_substring_of_the_stored_number(string typed)
    {
        // The property that matters, stated directly: whatever an operator types, the reduced form
        // has to appear inside the stored number, or the LIKE finds nothing.
        var fragment = PhoneNumberNormalizer.ToSearchFragment(typed);

        Assert.NotNull(fragment);
        Assert.Contains(fragment!, Stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sadra")]
    [InlineData("member.active")]
    [InlineData("someone@example.com")]
    public void A_term_with_no_digits_yields_nothing(string? typed) =>
        // So the caller adds no phone clause at all rather than one matching every number.
        Assert.Null(PhoneNumberNormalizer.ToSearchFragment(typed));

    [Fact]
    public void An_email_with_digits_still_yields_its_digits()
    {
        // Not a problem: the raw term is matched against the e-mail column in the same query, and a
        // stray digit clause only ever widens the result — it never hides a match.
        Assert.Equal("2024", PhoneNumberNormalizer.ToSearchFragment("user2024@example.com"));
    }
}
