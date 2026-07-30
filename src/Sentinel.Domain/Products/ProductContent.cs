using Sentinel.Domain.Common;

namespace Sentinel.Domain.Products;

/// <summary>
/// How visible a piece of product content is.
/// <para>
/// Kept as an explicit enum rather than a bool because "anyone signed in" and "only people who
/// hold the product" are genuinely different audiences, and a third — pre-purchase marketing
/// copy that must be readable by someone deciding whether to buy — is what makes a bool wrong.
/// </para>
/// </summary>
public enum ContentVisibility
{
    /// <summary>Readable by any signed-in member who can see the product at all.</summary>
    Public = 0,

    /// <summary>Only for members whose access to the product is currently usable.</summary>
    Entitled = 1,

    /// <summary>Operators only. The parking place for a draft, not a way to hide a secret.</summary>
    Internal = 2,
}

public enum ProductSectionKind
{
    /// <summary>Prose. Rendered as sanitised rich text.</summary>
    Text = 0,

    /// <summary>A bulleted feature list, one item per line.</summary>
    Features = 1,

    /// <summary>Question and answer pairs.</summary>
    Faq = 2,

    /// <summary>Release notes for one version.</summary>
    Changelog = 3,

    /// <summary>Requirements or compatibility notes.</summary>
    Requirements = 4,
}

/// <summary>
/// One block of content on a product page.
/// <para>
/// Sections rather than a single description column: a product page is assembled from parts that
/// appear and disappear independently, and each part carries its own visibility. Storing it as one
/// blob would mean the whole page shares one audience.
/// </para>
/// </summary>
public class ProductSection : IConcurrencyAware, ITimestamped
{
    public const int TitleMaxLength = 200;
    public const int MarkupMaxLength = 20_000;

    /// <summary>
    /// Generous relative to the markup limit: rendering adds tags, and the encoder turns one
    /// character into up to six. A rendered body must never be truncated by the column.
    /// </summary>
    public const int BodyHtmlMaxLength = MarkupMaxLength * 4;

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public ProductSectionKind Kind { get; set; } = ProductSectionKind.Text;

    public ContentVisibility Visibility { get; set; } = ContentVisibility.Public;

    public string? TitleFa { get; set; }

    public string? TitleEn { get; set; }

    /// <summary>
    /// What the operator typed, in the restricted markup subset. The editable source of truth —
    /// the rendered form below is derived from it and is never round-tripped through a form.
    /// </summary>
    public string? MarkupFa { get; set; }

    public string? MarkupEn { get; set; }

    /// <summary>
    /// The rendered HTML, produced once on save.
    /// <para>
    /// Rendering on the way in rather than on the way out means a read path that forgets to
    /// render cannot produce stored XSS — it just shows nothing — and the cost is paid once per
    /// edit instead of once per page view.
    /// </para>
    /// </summary>
    public string? BodyHtmlFa { get; set; }

    public string? BodyHtmlEn { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
