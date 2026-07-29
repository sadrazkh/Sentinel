using Sentinel.Application.Identity;

namespace Sentinel.UnitTests.Identity;

public sealed class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+989121234567")]
    [InlineData("989121234567")]
    [InlineData("09121234567")]
    [InlineData("0912 123 4567")]
    [InlineData("0912-123-4567")]
    [InlineData("(0912) 123 4567")]
    [InlineData("  09121234567  ")]
    [InlineData("00989121234567")]
    public void Every_way_of_writing_one_number_reduces_to_the_same_value(string input)
    {
        // This is the whole point: without it these are eight different strings, and signing
        // in by phone would depend on typing it exactly as it was saved.
        Assert.Equal("+989121234567", PhoneNumberNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("۰۹۱۲۱۲۳۴۵۶۷")]
    [InlineData("٠٩١٢١٢٣٤٥٦٧")]
    [InlineData("۰۹۱۲ ۱۲۳ ۴۵۶۷")]
    public void Persian_and_arabic_indic_digits_are_understood(string input)
    {
        // A Persian keyboard produces these interchangeably with ASCII digits.
        Assert.Equal("+989121234567", PhoneNumberNormalizer.Normalize(input));
    }

    [Fact]
    public void A_local_number_that_merely_starts_with_the_country_code_stays_local()
    {
        // "9812345678" is a ten-digit national number that happens to begin 98. Only eight
        // digits would follow the country code, which is too few for the international
        // reading, so the country code is prepended instead of assumed.
        Assert.Equal("+989812345678", PhoneNumberNormalizer.Normalize("9812345678"));
    }

    [Fact]
    public void A_number_from_another_country_keeps_its_own_code()
    {
        Assert.Equal("+442071234567", PhoneNumberNormalizer.Normalize("+44 20 7123 4567"));
    }

    [Fact]
    public void The_default_country_code_is_configurable()
    {
        Assert.Equal("+12125551234", PhoneNumberNormalizer.Normalize("2125551234", countryCode: "1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_value_is_not_a_phone_number(string? input)
    {
        Assert.Null(PhoneNumberNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0912abc4567")]
    [InlineData("+98 912 EXTRA 4567")]
    [InlineData("<script>")]
    public void Anything_containing_letters_is_rejected(string input)
    {
        Assert.Null(PhoneNumberNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("+1")]
    public void A_value_that_is_too_short_is_rejected(string input)
    {
        Assert.Null(PhoneNumberNormalizer.Normalize(input));
    }

    [Fact]
    public void A_value_longer_than_e164_allows_is_rejected()
    {
        Assert.Null(PhoneNumberNormalizer.Normalize("+" + new string('9', 16)));
    }

    [Fact]
    public void A_plus_anywhere_but_the_start_makes_it_invalid()
    {
        Assert.Null(PhoneNumberNormalizer.Normalize("0912+1234567"));
    }

    [Fact]
    public void The_result_always_fits_the_database_column()
    {
        var normalized = PhoneNumberNormalizer.Normalize("+" + new string('9', PhoneNumberNormalizer.MaxDigits));

        Assert.NotNull(normalized);
        Assert.True(normalized!.Length <= PhoneNumberNormalizer.MaxLength);
    }
}
