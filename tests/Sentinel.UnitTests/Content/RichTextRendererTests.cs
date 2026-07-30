using Sentinel.Application.Content;

namespace Sentinel.UnitTests.Content;

/// <summary>
/// The renderer's whole security claim is that it encodes first and only ever adds tags it chose
/// itself. These tests attack that claim from every direction a sanitiser would normally have to
/// defend, and then check the markup actually works.
/// </summary>
public sealed class RichTextRendererTests
{
    private static string? Render(string? markup) =>
        RichTextRenderer.Render(markup, ContentLinkPolicy.IsAllowed);

    // ------------------------------------------------------------------------- escaping ----

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<div onclick=\"steal()\">x</div>")]
    [InlineData("<!--<script>alert(1)</script>-->")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">")]
    public void No_caller_supplied_tag_ever_survives(string markup)
    {
        var html = Render(markup);

        Assert.NotNull(html);

        // Asserted on structure, not on substrings. A word like "onerror" legitimately survives
        // as escaped text — inert, because the "<" that would have made it an attribute became
        // "&lt;". What must hold is that every tag in the output is one this renderer built, so
        // that is what gets checked.
        AssertOnlyRendererTags(html!);
    }

    /// <summary>
    /// Every <c>&lt;</c> in the output must open or close a tag from the renderer's own set, with
    /// only the attributes it writes. Anything else means caller markup reached the output live.
    /// </summary>
    private static void AssertOnlyRendererTags(string html)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "h3", "h4", "h5", "p", "br", "strong", "em", "code", "a", "ul", "ol", "li",
        };

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(html, "<[^>]*>?").Cast<System.Text.RegularExpressions.Match>())
        {
            var tag = match.Value;

            var name = System.Text.RegularExpressions.Regex.Match(tag, "^</?([a-zA-Z0-9]+)");
            Assert.True(name.Success, $"Output contains a '<' that does not open a tag: {tag}");

            Assert.True(
                allowed.Contains(name.Groups[1].Value),
                $"Output contains a tag this renderer never emits: {tag}");

            // The only attributes the renderer writes are on <a>. Any attribute on any other tag
            // would mean caller text got inside a tag.
            var hasAttribute = tag.Contains('=', StringComparison.Ordinal);

            if (hasAttribute)
            {
                Assert.Equal("a", name.Groups[1].Value);
                Assert.DoesNotMatch(@"\son[a-z]+\s*=", tag);
            }
        }
    }

    [Fact]
    public void The_emitted_tag_set_is_exactly_what_this_renderer_constructs()
    {
        // A whitelist asserted from the outside. If a future change starts emitting a new tag,
        // this fails and the change gets looked at rather than shipping quietly.
        var markup = string.Join('\n',
            "# Heading",
            "## Sub",
            "### Deep",
            "",
            "Paragraph with **bold**, *italic*, `code` and [a link](https://example.com/x).",
            "",
            "- one",
            "- two",
            "",
            "1. first",
            "2. second");

        var html = Render(markup)!;

        AssertOnlyRendererTags(html);

        // And every construct in the markup actually produced its tag, so the check above is
        // passing because the set is right rather than because the output is empty.
        foreach (var expected in new[] { "<h3>", "<h4>", "<h5>", "<p>", "<strong>", "<em>", "<code>", "<a ", "<ul>", "<ol>", "<li>" })
        {
            Assert.Contains(expected, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_quote_inside_text_cannot_break_out_of_an_attribute()
    {
        var html = Render("[label](https://example.com/\"onmouseover=\"alert(1))");

        Assert.NotNull(html);
        Assert.DoesNotContain("onmouseover=\"alert", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Persian_text_passes_through_intact()
    {
        var html = Render("**راهنمای نصب** برای ویندوز")!;

        Assert.Contains("<strong>راهنمای نصب</strong>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------- links ----

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://example.com/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("https://user:secret@example.com/x")]
    public void A_refused_link_target_keeps_its_label_and_loses_its_href(string target)
    {
        var html = Render($"See [the guide]({target}) for details.")!;

        Assert.Contains("the guide", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/guide")]
    [InlineData("mailto:support@example.com")]
    [InlineData("/products/vault")]
    public void An_allowed_link_target_is_emitted(string target)
    {
        var html = Render($"See [the guide]({target}).")!;

        Assert.Contains("<a href=", html, StringComparison.Ordinal);
        Assert.Contains("the guide</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_emitted_link_carries_noopener_and_noreferrer()
    {
        // These point off-site by definition; the destination must get neither a window handle
        // nor a referrer naming the portal.
        var html = Render("[x](https://example.com/y)")!;

        Assert.Contains("rel=\"nofollow noopener noreferrer\"", html, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------- blocks ----

    [Fact]
    public void Headings_start_at_h3_so_the_page_outline_stays_wellformed()
    {
        // The product page already owns h1 and h2. An operator heading that emitted h1 would
        // produce two document titles.
        var html = Render("# Top\n\n## Second\n\n### Third")!;

        Assert.Contains("<h3>Top</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Second</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<h5>Third</h5>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Four_hashes_are_text_not_a_heading()
    {
        var html = Render("#### not a heading")!;

        Assert.DoesNotContain("<h", html, StringComparison.Ordinal);
        Assert.Contains("#### not a heading", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hash_without_a_space_is_text()
    {
        var html = Render("#hashtag")!;

        Assert.Contains("<p>#hashtag</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bullet_lines_become_one_list()
    {
        var html = Render("- one\n- two\n- three")!;

        Assert.Equal(1, CountOccurrences(html, "<ul>"));
        Assert.Equal(1, CountOccurrences(html, "</ul>"));
        Assert.Equal(3, CountOccurrences(html, "<li>"));
    }

    [Fact]
    public void Numbered_lines_become_an_ordered_list()
    {
        var html = Render("1. first\n2. second")!;

        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.Contains("<li>first</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>second</li>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_list_kind_closes_the_previous_list()
    {
        var html = Render("- bullet\n1. number")!;

        Assert.Contains("</ul>", html, StringComparison.Ordinal);
        Assert.Contains("<ol>", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("</ul>", StringComparison.Ordinal) < html.IndexOf("<ol>", StringComparison.Ordinal),
            "The bullet list must close before the ordered list opens.");
    }

    [Fact]
    public void Every_opened_tag_is_closed()
    {
        // Unbalanced output would leak into the surrounding page layout.
        var markup = string.Join('\n',
            "# Heading", "", "- a", "- b", "", "1. x", "", "Trailing paragraph", "", "- unclosed at end");

        var html = Render(markup)!;

        foreach (var tag in new[] { "p", "ul", "ol", "li", "h3" })
        {
            Assert.Equal(CountOccurrences(html, $"<{tag}>"), CountOccurrences(html, $"</{tag}>"));
        }
    }

    [Fact]
    public void Consecutive_lines_join_with_a_line_break_and_a_blank_line_starts_a_paragraph()
    {
        var html = Render("line one\nline two\n\nsecond paragraph")!;

        Assert.Equal(2, CountOccurrences(html, "<p>"));
        Assert.Contains("line one<br />line two", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- emphasis ----

    [Fact]
    public void Emphasis_does_not_span_a_line()
    {
        // An unclosed marker would otherwise swallow the rest of the document.
        var html = Render("*unclosed\nnext line")!;

        Assert.DoesNotContain("<em>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asterisk_inside_a_word_is_left_alone()
    {
        var html = Render("file*name and 2*3 arithmetic")!;

        Assert.DoesNotContain("<em>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bold_wins_over_italic_for_a_double_marker()
    {
        var html = Render("**strong**")!;

        Assert.Contains("<strong>strong</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<em>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_is_not_reinterpreted_as_markup()
    {
        var html = Render("Run `git commit -m \"**not bold**\"` first.")!;

        Assert.Contains("<code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>", html, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------- degenerate ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void Nothing_in_means_nothing_out(string? markup)
    {
        Assert.Null(Render(markup));
    }

    [Fact]
    public void A_long_document_renders_without_error()
    {
        var markup = string.Join("\n\n", Enumerable.Range(0, 500)
            .Select(index => $"## Section {index}\n\nBody **{index}** with [link](https://example.com/{index})."));

        var html = Render(markup);

        Assert.NotNull(html);
        Assert.Equal(500, CountOccurrences(html!, "<h4>"));
    }

    // ------------------------------------------------------------------------ plain text ----

    [Fact]
    public void Plain_text_strips_markup_and_never_produces_html()
    {
        var text = RichTextRenderer.ToPlainText(
            "## Title\n\nSome **bold** and [a link](https://example.com) and `code`.")!;

        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.Contains("bold", text, StringComparison.Ordinal);
        Assert.Contains("a link", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
