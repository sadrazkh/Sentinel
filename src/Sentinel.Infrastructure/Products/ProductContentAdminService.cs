using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Application.Content;
using Sentinel.Application.Media;
using Sentinel.Application.Products;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Products;
using Sentinel.Infrastructure.Media;

namespace Sentinel.Infrastructure.Products;

/// <summary>
/// Authoring for product content.
/// <para>
/// The one place markup becomes HTML. Rendering on the way in means every read path gets
/// already-safe output, so no query and no view has to remember to render — and a path that
/// forgets shows nothing rather than shipping raw markup to a browser.
/// </para>
/// </summary>
public sealed class ProductContentAdminService : IProductContentAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly IAuditService _audit;

    // Named for its first use; the store itself is a flat directory of server-generated names
    // and holds documentation step images alongside product icons.
    private readonly IApplicationIconStorage _images;

    private readonly MediaStorageOptions _mediaOptions;
    private readonly TimeProvider _timeProvider;

    public ProductContentAdminService(
        ISentinelDbContext db,
        IAuditService audit,
        IApplicationIconStorage images,
        IOptions<MediaStorageOptions> mediaOptions,
        TimeProvider timeProvider)
    {
        _db = db;
        _audit = audit;
        _images = images;
        _mediaOptions = mediaOptions.Value;
        _timeProvider = timeProvider;
    }

    // ------------------------------------------------------------------------ sections ----

    public async Task<OperationResult<Guid>> SaveSectionAsync(
        Guid productId,
        Guid? sectionId,
        ProductSectionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await ProductExistsAsync(productId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ContentErrors.ProductNotFound);
        }

        ProductSection section;

        if (sectionId is { } id)
        {
            var existing = await _db.ProductSections
                .FirstOrDefaultAsync(s => s.Id == id && s.ProductId == productId, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(OperationErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            section = existing;
        }
        else
        {
            section = new ProductSection
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                ProductId = productId,
            };

            _db.ProductSections.Add(section);
        }

        section.Kind = request.Kind;
        section.Visibility = request.Visibility;
        section.TitleFa = Trim(request.TitleFa);
        section.TitleEn = Trim(request.TitleEn);
        section.MarkupFa = Trim(request.MarkupFa);
        section.MarkupEn = Trim(request.MarkupEn);
        section.BodyHtmlFa = RichTextRenderer.Render(section.MarkupFa, ContentLinkPolicy.IsAllowed);
        section.BodyHtmlEn = RichTextRenderer.Render(section.MarkupEn, ContentLinkPolicy.IsAllowed);
        section.DisplayOrder = request.DisplayOrder;
        section.IsVisible = request.IsVisible;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ContentSectionSaved, nameof(ProductSection), section.Id) with
            {
                // The body is deliberately not in the metadata: an audit row is not a content
                // archive, and product copy can be long enough to bloat the log.
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("kind", request.Kind)
                    .Set("visibility", request.Visibility),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(section.Id);
    }

    public Task<OperationResult> DeleteSectionAsync(
        Guid sectionId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            _db.ProductSections,
            sectionId,
            AuditActions.ContentSectionDeleted,
            nameof(ProductSection),
            section => AuditMetadata.Create().Set("productId", section.ProductId),
            cancellationToken);

    // ----------------------------------------------------------------------- downloads ----

    public async Task<OperationResult<Guid>> SaveDownloadAsync(
        Guid productId,
        Guid? downloadId,
        ProductDownloadSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await ProductExistsAsync(productId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ContentErrors.ProductNotFound);
        }

        // Validated here and again before the redirect. A row could predate this rule or arrive
        // through a restore, and the redirect is the moment a browser is told to follow it.
        if (!DownloadUrlPolicy.IsAllowed(request.Url))
        {
            return OperationResult<Guid>.Failure(ContentErrors.DownloadUrlInvalid);
        }

        ProductDownload download;

        if (downloadId is { } id)
        {
            var existing = await _db.ProductDownloads
                .FirstOrDefaultAsync(d => d.Id == id && d.ProductId == productId, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(OperationErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            download = existing;
        }
        else
        {
            download = new ProductDownload
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                ProductId = productId,
            };

            _db.ProductDownloads.Add(download);
        }

        download.Platform = request.Platform;
        download.Visibility = request.Visibility;
        download.TitleFa = request.TitleFa.Trim();
        download.TitleEn = request.TitleEn.Trim();
        download.NoteFa = Trim(request.NoteFa);
        download.NoteEn = Trim(request.NoteEn);
        download.Url = request.Url.Trim();
        download.Version = Trim(request.Version);
        download.Checksum = Trim(request.Checksum);
        download.SizeBytes = request.SizeBytes;
        download.DisplayOrder = request.DisplayOrder;
        download.IsVisible = request.IsVisible;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ContentDownloadSaved, nameof(ProductDownload), download.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("platform", request.Platform)
                    .Set("visibility", request.Visibility)
                    // The host, not the full URL: enough to see where downloads point without
                    // copying a long path into every audit row.
                    .Set("host", HostOf(download.Url)),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(download.Id);
    }

    public Task<OperationResult> DeleteDownloadAsync(
        Guid downloadId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            _db.ProductDownloads,
            downloadId,
            AuditActions.ContentDownloadDeleted,
            nameof(ProductDownload),
            download => AuditMetadata.Create().Set("productId", download.ProductId),
            cancellationToken);

    // ---------------------------------------------------------------------- categories ----

    public async Task<OperationResult<Guid>> SaveCategoryAsync(
        Guid productId,
        Guid? categoryId,
        DocumentationCategorySaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await ProductExistsAsync(productId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ContentErrors.ProductNotFound);
        }

        DocumentationCategory category;

        if (categoryId is { } id)
        {
            var existing = await _db.DocumentationCategories
                .FirstOrDefaultAsync(c => c.Id == id && c.ProductId == productId, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(OperationErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            category = existing;
        }
        else
        {
            category = new DocumentationCategory
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                ProductId = productId,
            };

            _db.DocumentationCategories.Add(category);
        }

        var slug = await ResolveSlugAsync(
            productId,
            request.Slug,
            request.TitleEn,
            request.TitleFa,
            categoryId,
            SlugScope.Category,
            cancellationToken);

        if (!slug.Succeeded)
        {
            return OperationResult<Guid>.Failure(slug.ErrorKey!);
        }

        category.Slug = slug.Value!;
        category.TitleFa = request.TitleFa.Trim();
        category.TitleEn = request.TitleEn.Trim();
        category.IconName = Trim(request.IconName);
        category.DisplayOrder = request.DisplayOrder;
        category.IsVisible = request.IsVisible;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ContentCategorySaved, nameof(DocumentationCategory), category.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("slug", category.Slug),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(category.Id);
    }

    public Task<OperationResult> DeleteCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default) =>
        // Articles filed under it survive and become uncategorised — the foreign key is SetNull,
        // so deleting a heading never destroys the writing under it.
        DeleteAsync(
            _db.DocumentationCategories,
            categoryId,
            AuditActions.ContentCategoryDeleted,
            nameof(DocumentationCategory),
            category => AuditMetadata.Create()
                .Set("productId", category.ProductId)
                .Set("slug", category.Slug),
            cancellationToken);

    // ------------------------------------------------------------------------ articles ----

    public async Task<OperationResult<Guid>> SaveArticleAsync(
        Guid productId,
        Guid? articleId,
        DocumentationArticleSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await ProductExistsAsync(productId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ContentErrors.ProductNotFound);
        }

        // A category from another product would silently file the article under a heading that
        // never renders, so the pairing is checked rather than only the existence.
        if (request.CategoryId is { } requestedCategory
            && !await _db.DocumentationCategories.AnyAsync(
                c => c.Id == requestedCategory && c.ProductId == productId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ContentErrors.CategoryNotFound);
        }

        DocumentationArticle article;

        if (articleId is { } id)
        {
            var existing = await _db.DocumentationArticles
                .FirstOrDefaultAsync(a => a.Id == id && a.ProductId == productId, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(OperationErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            article = existing;
        }
        else
        {
            article = new DocumentationArticle
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                ProductId = productId,
            };

            _db.DocumentationArticles.Add(article);
        }

        var slug = await ResolveSlugAsync(
            productId,
            request.Slug,
            request.TitleEn,
            request.TitleFa,
            articleId,
            SlugScope.Article,
            cancellationToken);

        if (!slug.Succeeded)
        {
            return OperationResult<Guid>.Failure(slug.ErrorKey!);
        }

        article.Slug = slug.Value!;
        article.CategoryId = request.CategoryId;
        article.TitleFa = request.TitleFa.Trim();
        article.TitleEn = request.TitleEn.Trim();
        article.SummaryFa = Trim(request.SummaryFa);
        article.SummaryEn = Trim(request.SummaryEn);
        article.MarkupFa = Trim(request.MarkupFa);
        article.MarkupEn = Trim(request.MarkupEn);
        article.BodyHtmlFa = RichTextRenderer.Render(article.MarkupFa, ContentLinkPolicy.IsAllowed);
        article.BodyHtmlEn = RichTextRenderer.Render(article.MarkupEn, ContentLinkPolicy.IsAllowed);
        article.Visibility = request.Visibility;
        article.Platform = request.Platform;
        article.DisplayOrder = request.DisplayOrder;
        article.IsPublished = request.IsPublished;

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.ContentArticleSaved, nameof(DocumentationArticle), article.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("productId", productId)
                    .Set("slug", article.Slug)
                    .Set("visibility", request.Visibility)
                    .Set("published", request.IsPublished),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(article.Id);
    }

    public Task<OperationResult> DeleteArticleAsync(
        Guid articleId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            _db.DocumentationArticles,
            articleId,
            AuditActions.ContentArticleDeleted,
            nameof(DocumentationArticle),
            article => AuditMetadata.Create()
                .Set("productId", article.ProductId)
                .Set("slug", article.Slug),
            cancellationToken);

    // --------------------------------------------------------------------------- steps ----

    public async Task<OperationResult> SaveStepsAsync(
        Guid articleId,
        IReadOnlyList<StepInput> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var article = await _db.DocumentationArticles
            .FirstOrDefaultAsync(a => a.Id == articleId, cancellationToken);

        if (article is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        var existing = await _db.DocumentationSteps
            .Where(s => s.ArticleId == articleId)
            .OrderBy(s => s.StepNumber)
            .ToListAsync(cancellationToken);

        // Images are carried over by position, so editing the wording of step three keeps its
        // screenshot. An operator who reorders steps also moves the images, which is what
        // "position" means and is the least surprising behaviour.
        var carriedImages = existing.Select(step => step.MediaPath).ToList();

        var orphanedImages = new List<string>();

        // Removed wholesale rather than reconciled: the unique (article, number) index makes an
        // in-place renumber pass through colliding states, and this is one statement.
        _db.DocumentationSteps.RemoveRange(existing);

        var now = _timeProvider.GetUtcNow();

        for (var index = 0; index < steps.Count; index++)
        {
            var input = steps[index];

            var carried = index < carriedImages.Count ? carriedImages[index] : null;

            if (input.ClearImage && carried is not null)
            {
                orphanedImages.Add(carried);
                carried = null;
            }

            _db.DocumentationSteps.Add(new DocumentationStep
            {
                Id = SequentialGuid.New(now),
                ArticleId = articleId,
                StepNumber = index + 1,
                TitleFa = Trim(input.TitleFa),
                TitleEn = Trim(input.TitleEn),
                BodyFa = Trim(input.BodyFa),
                BodyEn = Trim(input.BodyEn),
                MediaPath = carried,
            });
        }

        // Steps beyond the new length lose their images too, or the files would leak.
        for (var index = steps.Count; index < carriedImages.Count; index++)
        {
            if (carriedImages[index] is { } stranded)
            {
                orphanedImages.Add(stranded);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // After the commit: a failed delete leaves an unreferenced file, which is untidy. Doing
        // it first would risk deleting an image the transaction then rolls back to still needing.
        foreach (var stored in orphanedImages)
        {
            await _images.DeleteAsync(stored, cancellationToken);
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> SetStepImageAsync(
        Guid articleId,
        int stepNumber,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var step = await _db.DocumentationSteps
            .FirstOrDefaultAsync(
                s => s.ArticleId == articleId && s.StepNumber == stepNumber, cancellationToken);

        if (step is null)
        {
            return OperationResult.Failure(ContentErrors.StepOutOfRange);
        }

        if (declaredLength > _mediaOptions.MaxIconBytes)
        {
            return OperationResult.Failure(CatalogErrors.IconTooLarge);
        }

        // Buffered in full first, bounded by the size limit above, so the signature check and the
        // write see the *same* bytes. Sniffing a header then copying from a stream the client
        // still controls lets a client send different bytes the second time.
        using var buffer = new MemoryStream(capacity: (int)Math.Max(declaredLength, 1));
        await content.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            return OperationResult.Failure(CatalogErrors.IconEmpty);
        }

        if (buffer.Length > _mediaOptions.MaxIconBytes)
        {
            // The declared length was a lie; the actual bytes are what count.
            return OperationResult.Failure(CatalogErrors.IconTooLarge);
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        if (bytes.Length < ImageSignature.RequiredHeaderBytes)
        {
            return OperationResult.Failure(CatalogErrors.IconNotAnImage);
        }

        // Neither the file name nor the browser's content type is consulted. Only the bytes.
        var format = ImageSignature.Detect(bytes[..ImageSignature.RequiredHeaderBytes]);

        if (format == ImageFormat.Unknown)
        {
            return OperationResult.Failure(CatalogErrors.IconNotAnImage);
        }

        buffer.Position = 0;

        var previous = step.MediaPath;
        var stored = await _images.SaveAsync(buffer, format, cancellationToken);

        step.MediaPath = stored.StoredName;

        await _db.SaveChangesAsync(cancellationToken);

        // The replaced file goes only after the new name is committed, so a crash between the two
        // leaves an unreferenced file rather than a row pointing at nothing.
        if (previous is not null)
        {
            await _images.DeleteAsync(previous, cancellationToken);
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> ClearStepImageAsync(
        Guid articleId,
        int stepNumber,
        CancellationToken cancellationToken = default)
    {
        var step = await _db.DocumentationSteps
            .FirstOrDefaultAsync(
                s => s.ArticleId == articleId && s.StepNumber == stepNumber, cancellationToken);

        if (step is null)
        {
            return OperationResult.Failure(ContentErrors.StepOutOfRange);
        }

        var previous = step.MediaPath;
        step.MediaPath = null;

        await _db.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            await _images.DeleteAsync(previous, cancellationToken);
        }

        return OperationResult.Success();
    }

    // ------------------------------------------------------------------------- helpers ----

    private enum SlugScope
    {
        Category,
        Article,
    }

    /// <summary>
    /// Settles on a slug: the operator's if they supplied one, otherwise derived from the title.
    /// <para>
    /// Uniqueness is scoped to the product and excludes the row being edited, so re-saving an
    /// article without touching its slug does not push it to <c>-2</c>.
    /// </para>
    /// </summary>
    private async Task<OperationResult<string>> ResolveSlugAsync(
        Guid productId,
        string? requested,
        string titleEn,
        string titleFa,
        Guid? excludeId,
        SlugScope scope,
        CancellationToken cancellationToken)
    {
        string candidate;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            candidate = requested.Trim().ToLowerInvariant();

            if (!ContentSlug.IsValid(candidate))
            {
                return OperationResult<string>.Failure(ContentErrors.SlugInvalid);
            }
        }
        else
        {
            // English title first, Persian as a fallback that will usually fail to reduce — at
            // which point the operator is asked for a slug rather than given a meaningless one.
            candidate = ContentSlug.TryDerive(titleEn)
                        ?? ContentSlug.TryDerive(titleFa)
                        ?? string.Empty;

            if (candidate.Length == 0)
            {
                return OperationResult<string>.Failure(ContentErrors.SlugUnderivable);
            }
        }

        var taken = scope == SlugScope.Category
            ? await _db.DocumentationCategories
                .Where(c => c.ProductId == productId && (excludeId == null || c.Id != excludeId))
                .Select(c => c.Slug)
                .ToListAsync(cancellationToken)
            : await _db.DocumentationArticles
                .Where(a => a.ProductId == productId && (excludeId == null || a.Id != excludeId))
                .Select(a => a.Slug)
                .ToListAsync(cancellationToken);

        var takenSet = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An explicitly typed slug that collides is reported rather than silently renamed: the
        // operator asked for that exact URL and should be told they cannot have it.
        if (!string.IsNullOrWhiteSpace(requested) && takenSet.Contains(candidate))
        {
            return OperationResult<string>.Failure(ContentErrors.SlugTaken);
        }

        return OperationResult<string>.Success(ContentSlug.MakeUnique(candidate, takenSet));
    }

    private Task<bool> ProductExistsAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(p => p.Id == productId, cancellationToken);

    private async Task<OperationResult> DeleteAsync<TEntity>(
        DbSet<TEntity> set,
        Guid id,
        string action,
        string entityType,
        Func<TEntity, AuditMetadata> describe,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var entity = await set.FindAsync([id], cancellationToken);

        if (entity is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        set.Remove(entity);

        await _audit.RecordAsync(
            AuditEntry.For(action, entityType, id) with { Metadata = describe(entity) },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
}
