using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Products;

namespace Sentinel.Web.Models.Products;

/// <summary>
/// The library's query as it arrives from the browser.
/// <para>
/// A bound model rather than loose parameters so the search term has a length limit: it is
/// echoed back into the input, and an unbounded string is an unbounded page.
/// </para>
/// </summary>
public sealed class ProductLibraryInput
{
    [StringLength(64)]
    public string? Category { get; set; }

    [StringLength(80)]
    public string? Search { get; set; }

    public ProductLibraryQuery ToQuery(ProductLibraryScope scope) => new(
        string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
        string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
        scope);
}

public sealed class ProductLibraryViewModel
{
    public required ProductLibraryView Library { get; init; }

    public required ProductLibraryScope Scope { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>Whether the "discover" tab is offered at all, from the feature flag.</summary>
    public required bool DiscoveryEnabled { get; init; }

    public string? Search => Library.Query.Search;

    public string? Category => Library.Query.CategoryKey;

    /// <summary>True when the page is empty only because of the search or category filter.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Category);
}

public sealed class ProductDetailViewModel
{
    public required ProductDetail Detail { get; init; }

    public required string TimeZoneId { get; init; }

    public required bool DocumentationEnabled { get; init; }
}
