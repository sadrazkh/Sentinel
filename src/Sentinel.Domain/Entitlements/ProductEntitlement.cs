using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Products;

namespace Sentinel.Domain.Entitlements;

/// <summary>
/// Why a member holds an entitlement. Drives what the library calls their access — a perk an
/// operator granted reads as "gifted", something they paid for reads as "owned" — and those are
/// different enough that collapsing them would make the card misleading.
/// </summary>
public enum EntitlementSource
{
    /// <summary>Granted by an operator: a loyalty perk, compensation, a manual arrangement.</summary>
    AdminGrant = 0,

    /// <summary>Bought. Reserved for the purchase flow, which is not enabled yet.</summary>
    Purchase = 1,

    Trial = 2,

    /// <summary>Invited to a closed preview or beta.</summary>
    BetaInvite = 3,
}

/// <summary>
/// One member's explicit right to use one product.
/// <para>
/// Exactly one row per (member, product), enforced by a unique index: the access check is then
/// a single lookup, and two rows can never disagree about the answer. Revoking sets
/// <see cref="RevokedAt"/> rather than deleting, and re-granting clears it — so the history of
/// an arrangement stays in the audit log rather than being spread over duplicate rows.
/// </para>
/// </summary>
public class ProductEntitlement : IConcurrencyAware, ITimestamped
{
    public const int NotesMaxLength = 512;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public EntitlementSource Source { get; set; } = EntitlementSource.AdminGrant;

    /// <summary>Lets an operator park a grant without losing its dates or notes.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset StartsAt { get; set; }

    /// <summary><c>null</c> means the grant does not expire on its own.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public Guid? GrantedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
