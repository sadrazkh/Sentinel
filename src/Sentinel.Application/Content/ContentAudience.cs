using Sentinel.Application.Access;
using Sentinel.Domain.Products;

namespace Sentinel.Application.Content;

/// <summary>
/// Which content audiences a given viewer belongs to.
/// <para>
/// A record rather than a pair of booleans passed around, so adding an audience later is one
/// change here instead of a new parameter threaded through every call site.
/// </para>
/// </summary>
public sealed record ContentAudience(bool CanSeeProduct, bool IsEntitled, bool IsOperator)
{
    /// <summary>Nobody: used when the product itself is not visible to the viewer.</summary>
    public static readonly ContentAudience None = new(false, false, false);

    /// <summary>
    /// Derives the audience from a product access decision.
    /// <para>
    /// "Entitled" is taken from <see cref="ProductAccessDecision.IsUsable"/> rather than from the
    /// presence of a grant: a member whose access has lapsed should read the public pages and not
    /// the entitled ones, which is exactly what usability means and not what holding a row means.
    /// </para>
    /// </summary>
    public static ContentAudience From(ProductAccessDecision decision, bool isOperator = false)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new ContentAudience(decision.CanView, decision.IsUsable, isOperator);
    }

    /// <summary>Whether a piece of content at this visibility may be shown to this viewer.</summary>
    public bool Allows(ContentVisibility visibility)
    {
        if (!CanSeeProduct && !IsOperator)
        {
            return false;
        }

        return visibility switch
        {
            // Readable by anyone who can see the product. This is the pre-purchase audience:
            // somebody deciding whether to obtain the product needs to be able to read about it.
            ContentVisibility.Public => true,

            // Only while access is actually usable. Setup instructions that name real hosts or
            // configuration belong here, not in Public.
            ContentVisibility.Entitled => IsEntitled || IsOperator,

            // The parking place for a draft. Deliberately not a place to keep a secret: it keeps
            // unfinished work off the public page, and operators can read it.
            ContentVisibility.Internal => IsOperator,

            // An unrecognised value must not fall through to visible. A new enum member added
            // without updating this switch should hide, not leak.
            _ => false,
        };
    }
}
