namespace Sentinel.Application.Products;

/// <summary>
/// Serves product content with this member's audience already applied.
/// <para>
/// Visibility is resolved here, once, against the same access decision the rest of the portal
/// uses — never in a view. A template that decided for itself what to show would be a second
/// implementation of the rule, and the two would eventually disagree.
/// </para>
/// </summary>
public interface IProductContentService
{
    /// <summary>
    /// Sections, downloads and a documentation preview for the product page. Returns
    /// <see cref="ProductPageContent.Empty"/> when the member cannot see the product at all.
    /// </summary>
    Task<ProductPageContent> GetPageContentAsync(
        Guid userId,
        string productKey,
        CancellationToken cancellationToken = default);

    /// <summary>The documentation index. <c>null</c> when the product is not visible to this member.</summary>
    Task<DocumentationIndexView?> GetDocumentationIndexAsync(
        Guid userId,
        string productKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One article by slug. <c>null</c> when it does not exist, is unpublished, or is out of this
    /// member's audience — all three answer the same way, so the URL cannot be used to probe.
    /// </summary>
    Task<DocumentationArticleView?> GetArticleAsync(
        Guid userId,
        string productKey,
        string slug,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a download to its destination, or refuses. <c>null</c> when no such download
    /// exists for that product.
    /// </summary>
    Task<DownloadResolution?> ResolveDownloadAsync(
        Guid userId,
        string productKey,
        Guid downloadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches article titles, summaries and bodies within one product, restricted to what this
    /// member may read.
    /// </summary>
    Task<IReadOnlyList<DocumentationArticleSummary>> SearchArticlesAsync(
        Guid userId,
        string productKey,
        string term,
        CancellationToken cancellationToken = default);
}
