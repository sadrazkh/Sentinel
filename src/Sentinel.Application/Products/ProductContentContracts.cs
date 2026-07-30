using Sentinel.Domain.Products;

namespace Sentinel.Application.Products;

/// <summary>One rendered content block, already filtered for this member's audience.</summary>
public sealed record ProductSectionView(
    Guid Id,
    ProductSectionKind Kind,
    string? TitleFa,
    string? TitleEn,
    /// <summary>Pre-rendered, already-safe HTML. See <see cref="Content.RichTextRenderer"/>.</summary>
    string? BodyHtmlFa,
    string? BodyHtmlEn,
    int DisplayOrder);

/// <summary>
/// A download offer. Carries no URL: the member gets a portal link that re-checks access and
/// then redirects, so a locked download cannot be lifted out of the page source.
/// </summary>
public sealed record ProductDownloadView(
    Guid Id,
    DownloadPlatform Platform,
    string TitleFa,
    string TitleEn,
    string? NoteFa,
    string? NoteEn,
    string? Version,
    string? Checksum,
    long? SizeBytes,
    int DisplayOrder);

public sealed record DocumentationArticleSummary(
    Guid Id,
    string Slug,
    string TitleFa,
    string TitleEn,
    string? SummaryFa,
    string? SummaryEn,
    DownloadPlatform? Platform,
    int DisplayOrder);

public sealed record DocumentationCategoryView(
    Guid Id,
    string Slug,
    string TitleFa,
    string TitleEn,
    string? IconName,
    int DisplayOrder,
    IReadOnlyList<DocumentationArticleSummary> Articles);

public sealed record DocumentationStepView(
    int StepNumber,
    string? TitleFa,
    string? TitleEn,
    string? BodyFa,
    string? BodyEn,
    string? MediaPath);

/// <summary>
/// One article, with its neighbours resolved so the page can offer previous/next without a
/// second round trip and without the client having to know the ordering rule.
/// </summary>
public sealed record DocumentationArticleView(
    Guid Id,
    string Slug,
    string ProductKey,
    string TitleFa,
    string TitleEn,
    string? SummaryFa,
    string? SummaryEn,
    string? BodyHtmlFa,
    string? BodyHtmlEn,
    DownloadPlatform? Platform,
    string? CategoryTitleFa,
    string? CategoryTitleEn,
    IReadOnlyList<DocumentationStepView> Steps,
    DocumentationArticleSummary? Previous,
    DocumentationArticleSummary? Next,
    DateTimeOffset UpdatedAt);

/// <summary>The documentation index for one product: categories, their articles, and loose ones.</summary>
public sealed record DocumentationIndexView(
    string ProductKey,
    string ProductNameFa,
    string ProductNameEn,
    IReadOnlyList<DocumentationCategoryView> Categories,
    IReadOnlyList<DocumentationArticleSummary> Uncategorised)
{
    public int ArticleCount =>
        Categories.Sum(category => category.Articles.Count) + Uncategorised.Count;
}

/// <summary>Everything the product page needs beyond the card itself.</summary>
public sealed record ProductPageContent(
    IReadOnlyList<ProductSectionView> Sections,
    IReadOnlyList<ProductDownloadView> Downloads,
    IReadOnlyList<DocumentationArticleSummary> FeaturedArticles,
    int TotalArticleCount)
{
    public static readonly ProductPageContent Empty = new([], [], [], 0);

    public bool HasAnything =>
        Sections.Count > 0 || Downloads.Count > 0 || TotalArticleCount > 0;
}

/// <summary>The outcome of resolving a download. Mirrors the launch resolution deliberately.</summary>
public sealed record DownloadResolution(
    Guid DownloadId,
    Guid ProductId,
    string ProductKey,
    string TitleEn,
    bool IsAllowed,
    /// <summary>Populated only when allowed, so a refused download cannot leak its destination.</summary>
    string? Url);
