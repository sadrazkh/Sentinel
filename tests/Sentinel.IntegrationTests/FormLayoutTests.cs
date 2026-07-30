using System.Text.RegularExpressions;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The form system's structural rules, checked against the markup rather than the rendering.
/// <para>
/// A browser is where alignment is finally judged, but these three rules are the ones that break it
/// and they are all visible in the source. Each was a real defect before it was a test: fields
/// stretched by a neighbour's hint, hints written with a class that had no styles behind it, and a
/// row holding more fields than its columns — which sends the extra ones back into the first line's
/// tracks and stacks them on top of it.
/// </para>
/// </summary>
public sealed partial class FormLayoutTests
{
    private static readonly string WebRoot = LocateWebProject();

    [GeneratedRegex(@"<div class=""(form-row[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex FormRowRegex();

    /// <summary>How many columns each row modifier declares, mirroring the stylesheet.</summary>
    private static readonly (string Modifier, int Columns)[] RowColumns =
    [
        ("form-row--4", 4),
        ("form-row--3", 3),
        ("form-row--2", 2),
        ("form-row--lead", 2),
        ("form-row--trail", 2),
    ];

    private static IEnumerable<string> Views() =>
        Directory.EnumerateFiles(WebRoot, "*.cshtml", SearchOption.AllDirectories);

    [Fact]
    public void No_row_holds_more_fields_than_it_has_columns()
    {
        // The one that actually stacks controls on top of each other. Subgrid gives a row three
        // tracks; a field that does not fit on the line is placed back into those same three.
        var offenders = new List<string>();

        foreach (var path in Views())
        {
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                var match = FormRowRegex().Match(lines[i]);

                if (!match.Success)
                {
                    continue;
                }

                var classes = match.Groups[1].Value;

                var columns = RowColumns
                    .FirstOrDefault(entry => classes.Contains(entry.Modifier, StringComparison.Ordinal))
                    .Columns;

                if (columns == 0)
                {
                    columns = 2;
                }

                var indent = lines[i].Length - lines[i].TrimStart().Length;
                var fields = 0;

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var lineIndent = lines[j].Length - lines[j].TrimStart().Length;

                    if (lines[j].Trim() == "</div>" && lineIndent == indent)
                    {
                        break;
                    }

                    if (lines[j].Contains("class=\"field\"", StringComparison.Ordinal)
                        && lineIndent == indent + 4)
                    {
                        fields++;
                    }
                }

                if (fields > columns)
                {
                    offenders.Add(
                        $"{Path.GetFileName(path)}:{i + 1} — {fields} fields in {columns} columns");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Rows that would wrap and collide:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void A_hint_inside_a_field_uses_the_hint_class()
    {
        // `.field__hint` was written in five views before it existed in the stylesheet, so those
        // hints rendered as full-size body text. The class exists now; this keeps the two in step
        // and stops the older `.text-xs.text-muted` spelling creeping back inside a field.
        var offenders = new List<string>();

        foreach (var path in Views())
        {
            var lines = File.ReadAllLines(path);
            var depth = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var indent = line.Length - line.TrimStart().Length;

                if (line.Contains("class=\"field\"", StringComparison.Ordinal))
                {
                    depth = indent;
                    continue;
                }

                // A field ends at its own closing tag.
                if (depth >= 0 && line.Trim() == "</div>" && indent <= depth)
                {
                    depth = -1;
                    continue;
                }

                if (depth >= 0
                    && line.Contains("class=\"text-xs text-muted\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Hints inside a field that skip .field__hint:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_form_lays_its_fields_out_with_an_inline_grid()
    {
        // Inline `grid-template-columns` is what the row modifiers replaced. Left in place it opts
        // that row out of the shared bands, which is how one form drifts from the rest.
        var offenders = new List<string>();

        foreach (var path in Views())
        {
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("class=\"grid\" style=\"grid-template-columns", StringComparison.Ordinal))
                {
                    continue;
                }

                var next = lines.Skip(i + 1).Take(2)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;

                if (next.Contains("class=\"field\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Field rows still using an inline grid:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_stylesheet_defines_every_form_class_the_views_ask_for()
    {
        // The failure that started this: a class used in markup and absent from the stylesheet is
        // invisible — the element simply renders unstyled, and nothing reports it.
        var css = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "css", "sentinel.css"));

        string[] required =
        [
            ".form", ".form-section", ".form-section__head", ".form-section__title",
            ".form-section__note", ".form-row", ".form-row--2", ".form-row--3", ".form-row--4",
            ".form-row--lead", ".form-row--trail", ".form-row__note", ".field", ".field__label",
            ".field__hint", ".field__control", ".field__error", ".checkbox", ".checkbox-field",
            ".checkbox-group", ".filter-bar", ".form-actions",
        ];

        var missing = required.Where(name => !css.Contains(name + " ", StringComparison.Ordinal)
                                             && !css.Contains(name + ",", StringComparison.Ordinal)
                                             && !css.Contains(name + "{", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "Undefined form classes: " + string.Join(", ", missing));
    }

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

        throw new InvalidOperationException("Could not locate the Sentinel.Web project.");
    }
}
