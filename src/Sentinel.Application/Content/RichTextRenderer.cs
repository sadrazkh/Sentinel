using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Sentinel.Application.Content;

/// <summary>
/// Turns a restricted markup subset into HTML.
/// <para>
/// <b>This is deliberately not an HTML sanitiser.</b> A sanitiser parses attacker-influenced
/// HTML and tries to decide which parts are safe — a game where every parser quirk, every
/// mutation-XSS trick and every new attribute is a chance to be wrong. Here the input is
/// HTML-encoded <em>first</em>, so no caller-supplied <c>&lt;</c> survives at all, and the
/// only tags in the output are ones this file constructed. There is nothing to get wrong about
/// which tags to allow, because none of the caller's tags are ever kept.
/// </para>
/// <para>
/// The cost is that operators write a small markup language instead of pasting HTML. For product
/// descriptions and setup guides — headings, emphasis, links, lists, code — that trade is worth
/// making, and it means the portal ships no HTML-parsing dependency on untrusted input.
/// </para>
/// <para>
/// Output is stored, not rendered on the fly, so a view that forgets to mark it safe simply shows
/// the markup rather than producing stored XSS.
/// </para>
/// </summary>
public static partial class RichTextRenderer
{
    /// <summary>The markup this renderer understands, for the operator-facing help text.</summary>
    public const string SyntaxHelpKey = "content.markup.help";

    [GeneratedRegex(@"\*\*(?<text>[^*\r\n]+)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![*\w])\*(?<text>[^*\r\n]+)\*(?![*\w])", RegexOptions.CultureInvariant)]
    private static partial Regex Italic();

    [GeneratedRegex(@"`(?<text>[^`\r\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

    // The label deliberately excludes ] and the target excludes ) and whitespace, so the match
    // cannot run past its own delimiters into neighbouring text.
    [GeneratedRegex(@"\[(?<label>[^\]\r\n]+)\]\((?<target>[^)\s\r\n]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    /// <summary>
    /// Renders markup to HTML that is safe to emit without further escaping.
    /// </summary>
    /// <param name="markup">Operator-entered markup. <c>null</c> or blank yields <c>null</c>.</param>
    /// <param name="linkPolicy">
    /// Decides whether a link target may be emitted. Supplied by the caller so the same renderer
    /// serves contexts with different rules, and so a missing policy is a compile error rather
    /// than an accidental default of "allow everything".
    /// </param>
    public static string? Render(string? markup, Func<string, bool> linkPolicy)
    {
        ArgumentNullException.ThrowIfNull(linkPolicy);

        if (string.IsNullOrWhiteSpace(markup))
        {
            return null;
        }

        var html = new StringBuilder();

        // Normalise line endings so block detection does not depend on the operator's platform.
        var lines = markup.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var listKind = ListKind.None;
        var paragraph = new List<string>();

        void CloseParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            html.Append("<p>").Append(string.Join("<br />", paragraph)).Append("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (listKind == ListKind.None)
            {
                return;
            }

            html.Append(listKind == ListKind.Ordered ? "</ol>" : "</ul>");
            listKind = ListKind.None;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                CloseParagraph();
                CloseList();
                continue;
            }

            // ---- headings -------------------------------------------------------------
            var headingLevel = 0;
            while (headingLevel < line.Length && line[headingLevel] == '#')
            {
                headingLevel++;
            }

            if (headingLevel is >= 1 and <= 3
                && headingLevel < line.Length
                && line[headingLevel] == ' ')
            {
                CloseParagraph();
                CloseList();

                // Offset by two: a product page already owns h1 and h2, so operator headings
                // start at h3 and the document outline stays well-formed.
                var tag = $"h{headingLevel + 2}";
                html.Append('<').Append(tag).Append('>')
                    .Append(RenderInline(line[(headingLevel + 1)..], linkPolicy))
                    .Append("</").Append(tag).Append('>');
                continue;
            }

            // ---- bullet list ----------------------------------------------------------
            if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal))
            {
                CloseParagraph();

                if (listKind != ListKind.Unordered)
                {
                    CloseList();
                    html.Append("<ul>");
                    listKind = ListKind.Unordered;
                }

                html.Append("<li>").Append(RenderInline(line[2..], linkPolicy)).Append("</li>");
                continue;
            }

            // ---- numbered list -------------------------------------------------------
            if (TryReadOrderedItem(line, out var itemText))
            {
                CloseParagraph();

                if (listKind != ListKind.Ordered)
                {
                    CloseList();
                    html.Append("<ol>");
                    listKind = ListKind.Ordered;
                }

                html.Append("<li>").Append(RenderInline(itemText, linkPolicy)).Append("</li>");
                continue;
            }

            // ---- paragraph text ------------------------------------------------------
            CloseList();
            paragraph.Add(RenderInline(line, linkPolicy));
        }

        CloseParagraph();
        CloseList();

        var result = html.ToString();

        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Strips markup to plain text, for a summary or a search index. Never produces HTML.
    /// </summary>
    public static string? ToPlainText(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return null;
        }

        var text = markup.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        text = Link().Replace(text, match => match.Groups["label"].Value);
        text = Bold().Replace(text, match => match.Groups["text"].Value);
        text = Italic().Replace(text, match => match.Groups["text"].Value);
        text = InlineCode().Replace(text, match => match.Groups["text"].Value);
        text = text.TrimStart('#', ' ', '-', '*');

        var collapsed = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? null : collapsed;
    }

    private enum ListKind
    {
        None,
        Unordered,
        Ordered,
    }

    private static bool TryReadOrderedItem(string line, out string text)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        // "1. " through "999. " — a longer run of digits is far more likely to be prose.
        if (digits is >= 1 and <= 3
            && digits + 1 < line.Length
            && line[digits] == '.'
            && line[digits + 1] == ' ')
        {
            text = line[(digits + 2)..];
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Encodes the text, then re-introduces only the inline tags this method decided to add.
    /// <para>
    /// The encode happens first and unconditionally. That ordering is the whole security
    /// argument: after it, the string contains no live markup from the caller, so the
    /// substitutions below can only ever add tags of our own choosing.
    /// </para>
    /// </summary>
    private static string RenderInline(string text, Func<string, bool> linkPolicy)
    {
        var encoded = ContentEncoder.Html.Encode(text);

        // Links first: the label may itself contain emphasis, and running emphasis first would
        // let a `*` inside a URL split the match.
        encoded = Link().Replace(encoded, match =>
        {
            var label = match.Groups["label"].Value;

            // The target went through the encoder with everything else, so decode it back to
            // compare against the policy — then emit the encoded form, never the decoded one.
            var target = System.Net.WebUtility.HtmlDecode(match.Groups["target"].Value);

            if (!linkPolicy(target))
            {
                // Refused targets keep their label and lose their link, so the sentence still
                // reads and nothing silently disappears.
                return label;
            }

            var encodedTarget = ContentEncoder.Html.Encode(target);

            // rel="nofollow noopener noreferrer" on every emitted link: these point off-site by
            // definition, and the destination must not get a window handle or a referrer.
            return $"<a href=\"{encodedTarget}\" rel=\"nofollow noopener noreferrer\" " +
                   $"target=\"_blank\">{label}</a>";
        });

        // Code spans are lifted out before emphasis runs and put back afterwards. Replacing them
        // in place would leave their contents exposed to the emphasis passes below, so
        // `git commit -m "**x**"` would come back with a <strong> inside the <code>.
        var codeSpans = new List<string>();

        encoded = InlineCode().Replace(encoded, match =>
        {
            codeSpans.Add(match.Groups["text"].Value);

            return Placeholder(codeSpans.Count - 1);
        });

        encoded = Bold().Replace(encoded, match => $"<strong>{match.Groups["text"].Value}</strong>");
        encoded = Italic().Replace(encoded, match => $"<em>{match.Groups["text"].Value}</em>");

        for (var index = 0; index < codeSpans.Count; index++)
        {
            encoded = encoded.Replace(
                Placeholder(index),
                $"<code>{codeSpans[index]}</code>",
                StringComparison.Ordinal);
        }

        return encoded;
    }

    /// <summary>
    /// A marker standing in for a lifted code span.
    /// <para>
    /// Built from C0 control characters on purpose. The encoder has already run by the time a
    /// placeholder is inserted, and it turns a raw U+0001 in the operator's text into
    /// <c>&amp;#x1;</c> — so no caller-supplied text can contain the raw form, and nobody can
    /// forge a placeholder to smuggle content past the emphasis passes. A readable marker like
    /// <c>[[code0]]</c> would be typeable, and therefore forgeable.
    /// </para>
    /// </summary>
    private static string Placeholder(int index) => $"\u0001{index}\u0002";
}
