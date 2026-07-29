namespace Sentinel.Application.Products;

/// <summary>
/// The single authority for what a member sees in the product library.
/// <para>
/// It assembles the inputs and defers to <see cref="Access.AccessRuleEvaluator"/> for the
/// security answer and <see cref="Access.ProductAccessRules"/> for the presentation of it.
/// No rule lives here, so the library and the launch endpoint cannot drift apart.
/// </para>
/// </summary>
public interface IProductLibraryService
{
    Task<ProductLibraryView> GetLibraryAsync(
        Guid userId,
        ProductLibraryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One product by key. Returns <c>null</c> when it does not exist <em>or</em> when this
    /// member may not see it — the two are deliberately indistinguishable, so the details URL
    /// is not a way of enumerating unreleased products.
    /// </summary>
    Task<ProductDetail?> GetDetailAsync(
        Guid userId,
        string key,
        CancellationToken cancellationToken = default);
}
