using System.Text;

namespace Sentinel.Application.Identity;

/// <summary>
/// Reduces a typed phone number to one canonical form so it can be stored uniquely and looked
/// up with an indexed equality match.
/// <para>
/// Without this, "۰۹۱۲ ۱۲۳ ۴۵۶۷", "09121234567" and "+989121234567" are three different
/// strings for one number — which means three accounts could claim the same phone, and
/// signing in by phone would depend on typing it exactly as it was saved.
/// </para>
/// </summary>
public static class PhoneNumberNormalizer
{
    /// <summary>Assumed when the input carries no international prefix. Iran.</summary>
    public const string DefaultCountryCode = "98";

    /// <summary>E.164 allows at most fifteen digits after the plus.</summary>
    public const int MaxDigits = 15;

    private const int MinDigits = 8;

    /// <summary>
    /// How many digits must follow the country code before a value that merely *starts* with
    /// the country code is read as already-international rather than as a local number.
    /// <para>
    /// "989121234567" is genuinely ambiguous: it could be +98 followed by a ten-digit national
    /// number, or a local number that happens to begin 98. Nine is chosen because a full
    /// national number is at least that long, so the international reading is the likely one —
    /// while a shorter tail (a local number starting "98…") still gets the country code
    /// prepended.
    /// </para>
    /// </summary>
    private const int MinNationalDigitsForImplicitInternational = 9;

    public const int MaxLength = MaxDigits + 1;

    /// <summary>
    /// Returns the canonical <c>+&lt;digits&gt;</c> form, or <c>null</c> when the input is
    /// empty or cannot be a phone number. Never throws — a bad value is simply not a phone.
    /// </summary>
    public static string? Normalize(string? input, string countryCode = DefaultCountryCode)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new StringBuilder(input.Length);
        var hasPlus = false;

        foreach (var character in input)
        {
            var mapped = MapDigit(character);

            if (mapped is not null)
            {
                digits.Append(mapped.Value);
                continue;
            }

            // A plus is only meaningful as the very first character; anywhere else it is noise.
            if (character == '+' && digits.Length == 0 && !hasPlus)
            {
                hasPlus = true;
                continue;
            }

            // Separators people type — spaces, dashes, brackets, dots — are dropped.
            // Anything else (letters, for instance) makes this not a phone number.
            if (!IsIgnorableSeparator(character))
            {
                return null;
            }
        }

        var raw = digits.ToString();

        if (raw.Length == 0)
        {
            return null;
        }

        var canonical = hasPlus
            ? raw
            : raw switch
            {
                // 00 is the international prefix in most of the world.
                ['0', '0', .. var international] => international,

                // A single leading zero is the national trunk prefix; it is dropped and the
                // configured country code takes its place.
                ['0', .. var national] => countryCode + national,

                // No prefix at all. If it already opens with the country code and enough
                // digits follow, it is an international number that lost its plus — which is
                // how numbers arrive from SMS gateways. Otherwise it is a local subscriber
                // number and the country code goes in front.
                _ when LooksAlreadyInternational(raw, countryCode) => raw,

                _ => countryCode + raw,
            };

        if (canonical.Length is < MinDigits or > MaxDigits)
        {
            return null;
        }

        return "+" + canonical;
    }

    private static bool LooksAlreadyInternational(string digits, string countryCode) =>
        digits.StartsWith(countryCode, StringComparison.Ordinal)
        && digits.Length - countryCode.Length >= MinNationalDigitsForImplicitInternational;

    /// <summary>
    /// Maps ASCII, Persian (U+06F0–U+06F9) and Arabic-Indic (U+0660–U+0669) digits to ASCII.
    /// A Persian keyboard produces the first two interchangeably.
    /// </summary>
    /// <summary>
    /// The digits of a partial phone number, ready to match against a stored E.164 value.
    /// <para>
    /// Searching needs something <see cref="Normalize"/> cannot give it: a term like "0912" is not
    /// a phone number — too short to canonicalise — yet it is exactly what somebody types to find
    /// one. Matching it literally fails, because the stored form is "+989120000001" and contains no
    /// "0912" anywhere.
    /// </para>
    /// <para>
    /// So this keeps only digits, maps Persian and Arabic ones to ASCII, and drops the national
    /// trunk prefix that the stored form does not carry. "0912" becomes "912", which is a substring
    /// of the stored number; "+98912" becomes "98912", which also is.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when the term has no digits — a name, an address — so the caller knows
    /// not to add a phone clause at all.
    /// </para>
    /// </summary>
    public static string? ToSearchFragment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new StringBuilder(input.Length);

        foreach (var character in input)
        {
            if (MapDigit(character) is { } mapped)
            {
                digits.Append(mapped);
            }
        }

        var raw = digits.ToString();

        // Trim the prefixes a stored E.164 number does not have: the international "00" and the
        // national trunk "0". Only one of them, and only at the front.
        var trimmed = raw switch
        {
            ['0', '0', .. var international] => international,
            ['0', .. var national] => national,
            _ => raw,
        };

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static char? MapDigit(char character) => character switch
    {
        >= '0' and <= '9' => character,
        >= '۰' and <= '۹' => (char)('0' + (character - '۰')),
        >= '٠' and <= '٩' => (char)('0' + (character - '٠')),
        _ => null,
    };

    private static bool IsIgnorableSeparator(char character) =>
        character is ' ' or '-' or '(' or ')' or '.' or '/' or '‌' or ' ';
}
