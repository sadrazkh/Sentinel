using Sentinel.Application.Common;
using Sentinel.Domain.Products;

namespace Sentinel.Application.Products;

/// <summary>
/// A section as the operator edits it.
/// <para>
/// Carries the <em>markup</em>, not the rendered HTML: the stored HTML is derived and must never
/// be round-tripped through a form, or an operator's save would re-render already-rendered output
/// and the markup source would be lost.
/// </para>
/// </summary>
public sealed record ProductSectionEditModel(
    Guid Id,
    Guid ProductId,
    ProductSectionKind Kind,
    ContentVisibility Visibility,
    string? TitleFa,
    string? TitleEn,
    string? MarkupFa,
    string? MarkupEn,
    int DisplayOrder,
    bool IsVisible,
    Guid? ConcurrencyToken);

public sealed record ProductSectionSaveRequest(
    ProductSectionKind Kind,
    ContentVisibility Visibility,
    string? TitleFa,
    string? TitleEn,
    string? MarkupFa,
    string? MarkupEn,
    int DisplayOrder,
    bool IsVisible,
    Guid? ConcurrencyToken);

public sealed record ProductDownloadEditModel(
    Guid Id,
    Guid ProductId,
    DownloadPlatform Platform,
    ContentVisibility Visibility,
    string TitleFa,
    string TitleEn,
    string? NoteFa,
    string? NoteEn,
    string Url,
    string? Version,
    string? Checksum,
    long? SizeBytes,
    int DisplayOrder,
    bool IsVisible,
    Guid? ConcurrencyToken);

public sealed record ProductDownloadSaveRequest(
    DownloadPlatform Platform,
    ContentVisibility Visibility,
    string TitleFa,
    string TitleEn,
    string? NoteFa,
    string? NoteEn,
    string Url,
    string? Version,
    string? Checksum,
    long? SizeBytes,
    int DisplayOrder,
    bool IsVisible,
    Guid? ConcurrencyToken);

public sealed record DocumentationArticleEditModel(
    Guid Id,
    Guid ProductId,
    Guid? CategoryId,
    string Slug,
    string TitleFa,
    string TitleEn,
    string? SummaryFa,
    string? SummaryEn,
    string? MarkupFa,
    string? MarkupEn,
    ContentVisibility Visibility,
    DownloadPlatform? Platform,
    int DisplayOrder,
    bool IsPublished,
    Guid? ConcurrencyToken);

public sealed record DocumentationArticleSaveRequest(
    Guid? CategoryId,
    /// <summary>Blank asks the service to derive one from the title.</summary>
    string? Slug,
    string TitleFa,
    string TitleEn,
    string? SummaryFa,
    string? SummaryEn,
    string? MarkupFa,
    string? MarkupEn,
    ContentVisibility Visibility,
    DownloadPlatform? Platform,
    int DisplayOrder,
    bool IsPublished,
    Guid? ConcurrencyToken);

public sealed record DocumentationCategorySaveRequest(
    string? Slug,
    string TitleFa,
    string TitleEn,
    string? IconName,
    int DisplayOrder,
    bool IsVisible,
    Guid? ConcurrencyToken);

/// <summary>A row in the operator's content list for one product.</summary>
public sealed record ProductContentSummary(
    int SectionCount,
    int DownloadCount,
    int CategoryCount,
    int ArticleCount,
    int UnpublishedArticleCount);

public static class ContentErrors
{
    public const string ProductNotFound = "admin.error.productNotFound";
    public const string SlugTaken = "admin.error.slugTaken";
    public const string SlugInvalid = "admin.error.slugInvalid";
    public const string SlugUnderivable = "admin.error.slugUnderivable";
    public const string DownloadUrlInvalid = "admin.error.downloadUrlInvalid";
    public const string CategoryNotFound = "admin.error.docCategoryNotFound";
    public const string StepOutOfRange = "admin.error.stepOutOfRange";
}

/// <summary>
/// Authoring for product content. Rendering markup to stored HTML happens here, once, on the way
/// in — so no read path can forget to do it and no view has to trust its input.
/// </summary>
public interface IProductContentAdminService
{
    Task<OperationResult<Guid>> SaveSectionAsync(
        Guid productId,
        Guid? sectionId,
        ProductSectionSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteSectionAsync(Guid sectionId, CancellationToken cancellationToken = default);

    Task<OperationResult<Guid>> SaveDownloadAsync(
        Guid productId,
        Guid? downloadId,
        ProductDownloadSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteDownloadAsync(Guid downloadId, CancellationToken cancellationToken = default);

    Task<OperationResult<Guid>> SaveCategoryAsync(
        Guid productId,
        Guid? categoryId,
        DocumentationCategorySaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<OperationResult<Guid>> SaveArticleAsync(
        Guid productId,
        Guid? articleId,
        DocumentationArticleSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteArticleAsync(Guid articleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an article's whole step list.
    /// <para>
    /// Replace-all rather than per-step patching: the steps are an ordered sequence, and
    /// reordering by editing individual rows means holding a unique (article, number) index open
    /// through a series of temporarily-colliding states. Renumbering the whole list in one
    /// transaction avoids that entirely.
    /// </para>
    /// <para>
    /// Existing images are carried over by position unless a step supplies
    /// <see cref="StepInput.ClearImage"/>, so re-saving the text of a step does not lose its
    /// screenshot.
    /// </para>
    /// </summary>
    Task<OperationResult> SaveStepsAsync(
        Guid articleId,
        IReadOnlyList<StepInput> steps,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches an image to one step.
    /// <para>
    /// The format is decided from the bytes here, not by the caller: the file name and the
    /// browser's Content-Type are attacker-influenced and never consulted. Same contract as the
    /// product icon upload, and for the same reason.
    /// </para>
    /// </summary>
    Task<OperationResult> SetStepImageAsync(
        Guid articleId,
        int stepNumber,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ClearStepImageAsync(
        Guid articleId,
        int stepNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>One step as the operator submits it. Order in the list is the step order.</summary>
public sealed record StepInput(
    string? TitleFa,
    string? TitleEn,
    string? BodyFa,
    string? BodyEn,
    bool ClearImage = false);

/// <summary>Read side of the content admin, kept separate so the write service holds no queries.</summary>
public interface IProductContentAdminQuery
{
    Task<ProductContentSummary?> GetSummaryAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductSectionEditModel>> ListSectionsAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<ProductSectionEditModel?> GetSectionAsync(Guid sectionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductDownloadEditModel>> ListDownloadsAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<ProductDownloadEditModel?> GetDownloadAsync(Guid downloadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentationArticleEditModel>> ListArticlesAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<DocumentationArticleEditModel?> GetArticleAsync(Guid articleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentationCategoryOption>> ListCategoriesAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    /// <summary>An article's steps in order, for the edit form.</summary>
    Task<IReadOnlyList<AdminStepRow>> ListStepsAsync(
        Guid articleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One step as the operator's form shows it. Carries the stored image name so the form can render
/// the current screenshot; the name is never accepted back from the form.
/// </summary>
public sealed record AdminStepRow(
    int StepNumber,
    string? TitleFa,
    string? TitleEn,
    string? BodyFa,
    string? BodyEn,
    string? MediaPath);

public sealed record DocumentationCategoryOption(
    Guid Id,
    string Slug,
    string TitleFa,
    string TitleEn,
    bool IsVisible);
