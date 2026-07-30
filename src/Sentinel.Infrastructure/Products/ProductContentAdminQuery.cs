using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Products;

namespace Sentinel.Infrastructure.Products;

/// <summary>
/// The operator's view of product content. Reads the markup, not the rendered HTML: an edit form
/// must hand back what was typed, or saving would re-render already-rendered output and lose the
/// source.
/// </summary>
public sealed class ProductContentAdminQuery : IProductContentAdminQuery
{
    private readonly ISentinelDbContext _db;

    public ProductContentAdminQuery(ISentinelDbContext db) => _db = db;

    public async Task<ProductContentSummary?> GetSummaryAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return null;
        }

        // Counted in SQL rather than by loading the rows: this feeds a badge on a list page and
        // has no business reading content bodies to produce a number.
        return new ProductContentSummary(
            await _db.ProductSections.CountAsync(s => s.ProductId == productId, cancellationToken),
            await _db.ProductDownloads.CountAsync(d => d.ProductId == productId, cancellationToken),
            await _db.DocumentationCategories.CountAsync(c => c.ProductId == productId, cancellationToken),
            await _db.DocumentationArticles.CountAsync(a => a.ProductId == productId, cancellationToken),
            await _db.DocumentationArticles.CountAsync(
                a => a.ProductId == productId && !a.IsPublished, cancellationToken));
    }

    public async Task<IReadOnlyList<ProductSectionEditModel>> ListSectionsAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await _db.ProductSections
            .AsNoTracking()
            .Where(s => s.ProductId == productId)
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new ProductSectionEditModel(
                s.Id, s.ProductId, s.Kind, s.Visibility, s.TitleFa, s.TitleEn,
                s.MarkupFa, s.MarkupEn, s.DisplayOrder, s.IsVisible, s.ConcurrencyToken))
            .ToListAsync(cancellationToken);

    public Task<ProductSectionEditModel?> GetSectionAsync(
        Guid sectionId,
        CancellationToken cancellationToken = default) =>
        _db.ProductSections
            .AsNoTracking()
            .Where(s => s.Id == sectionId)
            .Select(s => new ProductSectionEditModel(
                s.Id, s.ProductId, s.Kind, s.Visibility, s.TitleFa, s.TitleEn,
                s.MarkupFa, s.MarkupEn, s.DisplayOrder, s.IsVisible, s.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<ProductDownloadEditModel>> ListDownloadsAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await _db.ProductDownloads
            .AsNoTracking()
            .Where(d => d.ProductId == productId)
            .OrderBy(d => d.DisplayOrder)
            .ThenBy(d => d.TitleEn)
            .Select(d => new ProductDownloadEditModel(
                d.Id, d.ProductId, d.Platform, d.Visibility, d.TitleFa, d.TitleEn,
                d.NoteFa, d.NoteEn, d.Url, d.Version, d.Checksum, d.SizeBytes,
                d.DisplayOrder, d.IsVisible, d.ConcurrencyToken))
            .ToListAsync(cancellationToken);

    public Task<ProductDownloadEditModel?> GetDownloadAsync(
        Guid downloadId,
        CancellationToken cancellationToken = default) =>
        _db.ProductDownloads
            .AsNoTracking()
            .Where(d => d.Id == downloadId)
            .Select(d => new ProductDownloadEditModel(
                d.Id, d.ProductId, d.Platform, d.Visibility, d.TitleFa, d.TitleEn,
                d.NoteFa, d.NoteEn, d.Url, d.Version, d.Checksum, d.SizeBytes,
                d.DisplayOrder, d.IsVisible, d.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<DocumentationArticleEditModel>> ListArticlesAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await _db.DocumentationArticles
            .AsNoTracking()
            .Where(a => a.ProductId == productId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.TitleEn)
            // The body is left out of the list projection on purpose: a list of forty articles
            // has no reason to pull forty article bodies across the wire.
            .Select(a => new DocumentationArticleEditModel(
                a.Id, a.ProductId, a.CategoryId, a.Slug, a.TitleFa, a.TitleEn,
                a.SummaryFa, a.SummaryEn, null, null, a.Visibility, a.Platform,
                a.DisplayOrder, a.IsPublished, a.ConcurrencyToken))
            .ToListAsync(cancellationToken);

    public Task<DocumentationArticleEditModel?> GetArticleAsync(
        Guid articleId,
        CancellationToken cancellationToken = default) =>
        _db.DocumentationArticles
            .AsNoTracking()
            .Where(a => a.Id == articleId)
            .Select(a => new DocumentationArticleEditModel(
                a.Id, a.ProductId, a.CategoryId, a.Slug, a.TitleFa, a.TitleEn,
                a.SummaryFa, a.SummaryEn, a.MarkupFa, a.MarkupEn, a.Visibility, a.Platform,
                a.DisplayOrder, a.IsPublished, a.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<DocumentationCategoryOption>> ListCategoriesAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await _db.DocumentationCategories
            .AsNoTracking()
            .Where(c => c.ProductId == productId)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.TitleEn)
            .Select(c => new DocumentationCategoryOption(
                c.Id, c.Slug, c.TitleFa, c.TitleEn, c.IsVisible))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AdminStepRow>> ListStepsAsync(
        Guid articleId,
        CancellationToken cancellationToken = default) =>
        await _db.DocumentationSteps
            .AsNoTracking()
            .Where(s => s.ArticleId == articleId)
            .OrderBy(s => s.StepNumber)
            .Select(s => new AdminStepRow(
                s.StepNumber, s.TitleFa, s.TitleEn, s.BodyFa, s.BodyEn, s.MediaPath))
            .ToListAsync(cancellationToken);
}
