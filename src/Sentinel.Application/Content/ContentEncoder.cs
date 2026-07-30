using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Sentinel.Application.Content;

/// <summary>
/// The HTML encoder used for stored content.
/// <para>
/// <see cref="HtmlEncoder.Default"/> escapes everything outside Basic Latin, so a Persian
/// paragraph comes back as a wall of <c>&amp;#x631;</c> entities — several times its original
/// size, unreadable in the database, and painful to diff. Widening the safe list to the Arabic
/// ranges is the same adjustment <c>WebEncoderOptions</c> makes for Razor output; this instance
/// exists because the Application layer renders content outside the request pipeline and cannot
/// reach the configured one.
/// </para>
/// <para>
/// Widening the safe list does not weaken the escaping that matters: the five characters that
/// carry meaning in HTML — <c>&lt; &gt; &amp; " '</c> — are still escaped, because they are not
/// in any of the ranges added below.
/// </para>
/// </summary>
public static class ContentEncoder
{
    public static readonly HtmlEncoder Html = HtmlEncoder.Create(new TextEncoderSettings(
        UnicodeRanges.BasicLatin,
        UnicodeRanges.Latin1Supplement,
        UnicodeRanges.LatinExtendedA,
        UnicodeRanges.Arabic,
        UnicodeRanges.ArabicSupplement,
        UnicodeRanges.ArabicExtendedA,
        UnicodeRanges.ArabicPresentationFormsA,
        UnicodeRanges.ArabicPresentationFormsB,
        // Persian text routinely mixes in these: the zero-width non-joiner, Persian digits and
        // the general punctuation block that carries the RTL marks.
        UnicodeRanges.GeneralPunctuation));
}
