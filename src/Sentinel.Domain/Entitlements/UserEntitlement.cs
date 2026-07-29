using Sentinel.Domain.Catalog;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Entitlements;

/// <summary>
/// An explicit, per-user grant for one application, independent of the membership tier.
/// <para>
/// There is exactly one row per (user, application) pair — enforced by a unique index.
/// Revoking sets <see cref="RevokedAt"/>; re-granting clears it. The full history of who
/// granted or revoked what lives in the audit log, which keeps the access-decision query
/// to a single row lookup and removes any "which of these five rows wins?" ambiguity.
/// </para>
/// </summary>
public class UserEntitlement : IConcurrencyAware, ITimestamped
{
    public const int NotesMaxLength = 512;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public Guid ApplicationId { get; set; }

    public PortalApplication? Application { get; set; }

    /// <summary>Lets an admin park a grant without losing its notes and dates.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset StartsAt { get; set; }

    /// <summary><c>null</c> means the grant follows the membership rather than its own end date.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public Guid? GrantedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? Notes { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
