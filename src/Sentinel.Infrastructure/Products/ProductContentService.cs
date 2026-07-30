using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Content;
using Sentinel.Application.Features;
using Sentinel.Application.Products;
using Sentinel.Domain.Products;

namespace Sentinel.Infrastructure.Products;

/// <summary>
/// Loads product content and applies the viewer's audience.
/// <para>
/// The audience comes from <see cref="IProductLibraryService"/>, so "may this member read the
/// entitled pages" is answered by the same code that decides whether they may launch the product.
/// This service holds no access rule of its own.
/// </para>
/// </summary>
public sealed class ProductContentService : IProductContentService
{
    /// <summary>How many articles the product page previews before it just links to the index.</summary>
    private const int FeaturedArticleLimit = 5;

    private const int SearchResultLimit = 25;

    private readonly ISentinelDbContext _db;
    private readonly IProductLibraryService _library;
    private readonly IFeatureGate _features;

    public ProductContentService(
        ISentinelDbContext db,
        IProductLibraryService library,
        IFeatureGate features)
    {
        _db = db;
        _library = library;
        _features = features;
    }

    public async Task<ProductPageContent> GetPageContentAsync(
        Guid userId,
        string productKey,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(userId, productKey, cancellationToken);
        if (context is null)
        {
            return ProductPageContent.Empty;
        }

        var (productId, _, audience) = (context.ProductId, context.Product, context.Audience);

        var visible = VisibleTo(audience);

        // The audience filter runs after the fetch because it depends on an access decision and
        // is not expressible in SQL. Row counts here are small by construction — a product page
        // has a handful of sections, not thousands.
        //
        // The visibility marker travels beside the view rather than inside it: the view is what
        // reaches the browser, and shipping the marker would tell a member which parts of the
        // page they are being kept out of.
        var sectionRows = await _db.ProductSections
            .AsNoTracking()
            .Where(s => s.ProductId == productId && s.IsVisible)
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new
            {
                View = new ProductSectionView(
                    s.Id, s.Kind, s.TitleFa, s.TitleEn, s.BodyHtmlFa, s.BodyHtmlEn, s.DisplayOrder),
                s.Visibility,
            })
            .ToListAsync(cancellationToken);

        var visibleSections = sectionRows
            .Where(row => visible(row.Visibility))
            .Select(row => row.View)
            .ToList();

        var downloadRows = await _db.ProductDownloads
            .AsNoTracking()
            .Where(d => d.ProductId == productId && d.IsVisible)
            .OrderBy(d => d.DisplayOrder)
            .ThenBy(d => d.TitleEn)
            .Select(d => new
            {
                View = new ProductDownloadView(
                    d.Id, d.Platform, d.TitleFa, d.TitleEn, d.NoteFa, d.NoteEn,
                    d.Version, d.Checksum, d.SizeBytes, d.DisplayOrder),
                d.Visibility,
            })
            .ToListAsync(cancellationToken);

        var downloads = downloadRows
            .Where(row => visible(row.Visibility))
            .Select(row => row.View)
            .ToList();

        var articles = await ReadableArticlesAsync(productId, visible, cancellationToken);

        var documentationOn = _features.IsEnabled(FeatureNames.ProductDocumentation);

        return new ProductPageContent(
            visibleSections,
            downloads,
            documentationOn ? articles.Take(FeaturedArticleLimit).ToList() : [],
            documentationOn ? articles.Count : 0);
    }

    public async Task<DocumentationIndexView?> GetDocumentationIndexAsync(
        Guid userId,
        string productKey,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(userId, productKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var (productId, product, audience) = (context.ProductId, context.Product, context.Audience);
        var visible = VisibleTo(audience);

        var articles = await ReadableArticlesWithCategoryAsync(productId, visible, cancellationToken);

        var categories = await _db.DocumentationCategories
            .AsNoTracking()
            .Where(c => c.ProductId == productId && c.IsVisible)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.TitleEn)
            .Select(c => new { c.Id, c.Slug, c.TitleFa, c.TitleEn, c.IconName, c.DisplayOrder })
            .ToListAsync(cancellationToken);

        var grouped = categories
            .Select(category => new DocumentationCategoryView(
                category.Id,
                category.Slug,
                category.TitleFa,
                category.TitleEn,
                category.IconName,
                category.DisplayOrder,
                articles
                    .Where(article => article.CategoryId == category.Id)
                    .Select(article => article.Summary)
                    .ToList()))
            // A category the member can read nothing in is not shown: an empty heading tells them
            // only that something exists which they cannot have.
            .Where(view => view.Articles.Count > 0)
            .ToList();

        var categorised = categories.Select(category => category.Id).ToHashSet();

        var uncategorised = articles
            .Where(article => article.CategoryId is null || !categorised.Contains(article.CategoryId.Value))
            .Select(article => article.Summary)
            .ToList();

        return new DocumentationIndexView(
            product.Key, product.NameFa, product.NameEn, grouped, uncategorised);
    }

    public async Task<DocumentationArticleView?> GetArticleAsync(
        Guid userId,
        string productKey,
        string slug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var context = await ResolveAsync(userId, productKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var (productId, product, audience) = (context.ProductId, context.Product, context.Audience);
        var visible = VisibleTo(audience);

        var normalizedSlug = slug.Trim().ToLowerInvariant();

        var article = await _db.DocumentationArticles
            .AsNoTracking()
            .Where(a => a.ProductId == productId && a.Slug == normalizedSlug && a.IsPublished)
            .Select(a => new
            {
                a.Id, a.Slug, a.TitleFa, a.TitleEn, a.SummaryFa, a.SummaryEn,
                a.BodyHtmlFa, a.BodyHtmlEn, a.Platform, a.Visibility, a.UpdatedAt, a.DisplayOrder,
                CategoryTitleFa = a.Category == null ? null : a.Category.TitleFa,
                CategoryTitleEn = a.Category == null ? null : a.Category.TitleEn,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Absent, unpublished and out-of-audience all answer the same way, so the URL cannot be
        // used to establish that an article exists.
        if (article is null || !visible(article.Visibility))
        {
            return null;
        }

        var steps = await _db.DocumentationSteps
            .AsNoTracking()
            .Where(s => s.ArticleId == article.Id)
            .OrderBy(s => s.StepNumber)
            .Select(s => new DocumentationStepView(
                s.StepNumber, s.TitleFa, s.TitleEn, s.BodyFa, s.BodyEn, s.MediaPath))
            .ToListAsync(cancellationToken);

        // Neighbours are taken from the same readable set the index shows, so "next" never lands
        // on something the member would be refused.
        var readable = await ReadableArticlesAsync(productId, visible, cancellationToken);
        var position = readable.FindIndex(candidate => candidate.Id == article.Id);

        return new DocumentationArticleView(
            article.Id,
            article.Slug,
            product.Key,
            article.TitleFa,
            article.TitleEn,
            article.SummaryFa,
            article.SummaryEn,
            article.BodyHtmlFa,
            article.BodyHtmlEn,
            article.Platform,
            article.CategoryTitleFa,
            article.CategoryTitleEn,
            steps,
            position > 0 ? readable[position - 1] : null,
            position >= 0 && position < readable.Count - 1 ? readable[position + 1] : null,
            article.UpdatedAt);
    }

    public async Task<DownloadResolution?> ResolveDownloadAsync(
        Guid userId,
        string productKey,
        Guid downloadId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(userId, productKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var (productId, product, audience) = (context.ProductId, context.Product, context.Audience);

        var download = await _db.ProductDownloads
            .AsNoTracking()
            .Where(d => d.Id == downloadId && d.ProductId == productId && d.IsVisible)
            .Select(d => new { d.Id, d.TitleEn, d.Url, d.Visibility })
            .FirstOrDefaultAsync(cancellationToken);

        if (download is null)
        {
            return null;
        }

        var allowed = VisibleTo(audience)(download.Visibility);

        return new DownloadResolution(
            download.Id,
            productId,
            product.Key,
            download.TitleEn,
            allowed,
            // Attached only on success, so a refused download cannot be turned into a way of
            // reading the URL — the same rule the application launch follows.
            allowed ? download.Url : null);
    }

    public async Task<IReadOnlyList<DocumentationArticleSummary>> SearchArticlesAsync(
        Guid userId,
        string productKey,
        string term,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var context = await ResolveAsync(userId, productKey, cancellationToken);
        if (context is null)
        {
            return [];
        }

        var (productId, _, audience) = (context.ProductId, context.Product, context.Audience);
        var visible = VisibleTo(audience);
        var needle = term.Trim();

        // EF.Functions.Like with a parameterised pattern, so the term never reaches SQL as
        // concatenated text. The wildcards an operator could type are escaped first, otherwise a
        // search for "%" would match every row.
        var pattern = $"%{Escape(needle)}%";

        var rows = await _db.DocumentationArticles
            .AsNoTracking()
            .Where(a => a.ProductId == productId && a.IsPublished)
            .Where(a => EF.Functions.Like(a.TitleFa, pattern)
                        || EF.Functions.Like(a.TitleEn, pattern)
                        || (a.SummaryFa != null && EF.Functions.Like(a.SummaryFa, pattern))
                        || (a.SummaryEn != null && EF.Functions.Like(a.SummaryEn, pattern))
                        // Searched against the markup, not the rendered HTML: a term like "code"
                        // would otherwise match every article that contains a code span.
                        || (a.MarkupFa != null && EF.Functions.Like(a.MarkupFa, pattern))
                        || (a.MarkupEn != null && EF.Functions.Like(a.MarkupEn, pattern)))
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.TitleEn)
            .Select(a => new
            {
                Summary = new DocumentationArticleSummary(
                    a.Id, a.Slug, a.TitleFa, a.TitleEn, a.SummaryFa, a.SummaryEn,
                    a.Platform, a.DisplayOrder),
                a.Visibility,
            })
            // Bounded in SQL rather than after loading: a permissive term must not pull the whole
            // table into memory before the audience filter thins it out.
            .Take(SearchResultLimit * 4)
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => visible(row.Visibility))
            .Select(row => row.Summary)
            .Take(SearchResultLimit)
            .ToList();
    }

    // ------------------------------------------------------------------------- helpers ----

    private static string Escape(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    private static Func<ContentVisibility, bool> VisibleTo(ContentAudience audience) =>
        audience.Allows;

    private sealed record ProductRef(string Key, string NameFa, string NameEn);

    /// <summary>
    /// The product plus what this viewer may read of it — resolved once per request and then
    /// passed around, so no method re-derives the audience and risks a different answer.
    /// </summary>
    private sealed record ContentContext(Guid ProductId, ProductRef Product, ContentAudience Audience);

    /// <summary>
    /// Finds the product and works out what this member may read of it. <c>null</c> when the
    /// product does not exist or is not visible to them.
    /// </summary>
    private async Task<ContentContext?> ResolveAsync(
        Guid userId,
        string productKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(productKey))
        {
            return null;
        }

        var detail = await _library.GetDetailAsync(userId, productKey, cancellationToken);

        // GetDetailAsync already answers "absent or invisible" identically, which is the
        // behaviour every caller of this service wants too.
        if (detail is null)
        {
            return null;
        }

        return new ContentContext(
            detail.Card.Id,
            new ProductRef(detail.Card.Key, detail.Card.NameFa, detail.Card.NameEn),
            ContentAudience.From(detail.Card.Access));
    }

    private async Task<List<DocumentationArticleSummary>> ReadableArticlesAsync(
        Guid productId,
        Func<ContentVisibility, bool> visible,
        CancellationToken cancellationToken)
    {
        var withCategory = await ReadableArticlesWithCategoryAsync(productId, visible, cancellationToken);

        return withCategory.Select(article => article.Summary).ToList();
    }

    private sealed record ArticleRow(DocumentationArticleSummary Summary, Guid? CategoryId);

    private async Task<List<ArticleRow>> ReadableArticlesWithCategoryAsync(
        Guid productId,
        Func<ContentVisibility, bool> visible,
        CancellationToken cancellationToken)
    {
        var rows = await _db.DocumentationArticles
            .AsNoTracking()
            .Where(a => a.ProductId == productId && a.IsPublished)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.TitleEn)
            .Select(a => new
            {
                Summary = new DocumentationArticleSummary(
                    a.Id, a.Slug, a.TitleFa, a.TitleEn, a.SummaryFa, a.SummaryEn,
                    a.Platform, a.DisplayOrder),
                a.CategoryId,
                a.Visibility,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => visible(row.Visibility))
            .Select(row => new ArticleRow(row.Summary, row.CategoryId))
            .ToList();
    }

}
