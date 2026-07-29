using Sentinel.Domain.Common;

namespace Sentinel.Domain.Products;

/// <summary>
/// Groups products in the library. Presentation only — a category never grants or withholds
/// access, so moving a product between categories can never change who may use it.
/// </summary>
public class ProductCategory : ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;

    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string NameFa { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    /// <summary>Inline SVG path data for the category glyph, from a fixed internal set.</summary>
    public string? IconName { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
