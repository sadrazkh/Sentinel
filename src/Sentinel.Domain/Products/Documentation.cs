using Sentinel.Domain.Common;

namespace Sentinel.Domain.Products;

/// <summary>
/// A group of articles for one product — "Getting started", "Troubleshooting", "Per-platform
/// setup". Scoped to a product rather than global, so two products can both have a "Getting
/// started" without either having to invent a unique name for it.
/// </summary>
public class DocumentationCategory : IConcurrencyAware, ITimestamped
{
    public const int SlugMaxLength = 80;
    public const int TitleMaxLength = 160;

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Unique within the product. Lower-case, hyphenated, safe in a URL path segment.</summary>
    public string Slug { get; set; } = string.Empty;

    public string TitleFa { get; set; } = string.Empty;

    public string TitleEn { get; set; } = string.Empty;

    public string? IconName { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<DocumentationArticle> Articles { get; set; } = new List<DocumentationArticle>();
}

/// <summary>
/// One documentation article. Body prose plus optional numbered steps — a setup guide is mostly
/// steps, a concept page is mostly prose, and both are the same entity so navigation, search and
/// visibility do not need two implementations.
/// </summary>
public class DocumentationArticle : IConcurrencyAware, ITimestamped
{
    public const int SlugMaxLength = 120;
    public const int TitleMaxLength = 200;
    public const int SummaryMaxLength = 400;
    public const int MarkupMaxLength = 40_000;

    /// <summary>See <see cref="ProductSection.BodyHtmlMaxLength"/> for why this is larger.</summary>
    public const int BodyHtmlMaxLength = MarkupMaxLength * 4;

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public Guid? CategoryId { get; set; }

    public DocumentationCategory? Category { get; set; }

    /// <summary>Unique within the product, so the URL is `/products/{key}/docs/{slug}`.</summary>
    public string Slug { get; set; } = string.Empty;

    public string TitleFa { get; set; } = string.Empty;

    public string TitleEn { get; set; } = string.Empty;

    public string? SummaryFa { get; set; }

    public string? SummaryEn { get; set; }

    /// <summary>The operator's markup — the editable source. See <see cref="ProductSection.MarkupFa"/>.</summary>
    public string? MarkupFa { get; set; }

    public string? MarkupEn { get; set; }

    /// <summary>Rendered once on save. See <see cref="ProductSection.BodyHtmlFa"/>.</summary>
    public string? BodyHtmlFa { get; set; }

    public string? BodyHtmlEn { get; set; }

    public ContentVisibility Visibility { get; set; } = ContentVisibility.Public;

    public DownloadPlatform? Platform { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsPublished { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<DocumentationStep> Steps { get; set; } = new List<DocumentationStep>();
}

/// <summary>
/// One numbered step in a guide. Separate rows rather than markup inside the body so a step can
/// carry its own image and be reordered without editing prose.
/// </summary>
public class DocumentationStep : ITimestamped
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 4_000;
    public const int MediaPathMaxLength = 512;

    public Guid Id { get; set; }

    public Guid ArticleId { get; set; }

    public DocumentationArticle? Article { get; set; }

    public int StepNumber { get; set; }

    public string? TitleFa { get; set; }

    public string? TitleEn { get; set; }

    /// <summary>Plain text, encoded on render. Steps do not need rich formatting.</summary>
    public string? BodyFa { get; set; }

    public string? BodyEn { get; set; }

    /// <summary>Stored image file name, served through the media endpoint like a product icon.</summary>
    public string? MediaPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
