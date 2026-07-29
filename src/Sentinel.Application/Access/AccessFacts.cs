using Sentinel.Domain.Products;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Access;

/// <summary>The application-side inputs to an access decision.</summary>
public sealed record ApplicationFacts(
    Guid Id,
    string Key,
    bool IsEnabled,
    ProductReleaseStatus ReleaseStatus,
    bool RequiresExplicitEntitlement,
    MembershipTier? MinimumTier);

/// <summary>The grant-side inputs. <c>null</c> means no grant row exists for this pair.</summary>
public sealed record EntitlementFacts(
    bool IsEnabled,
    DateTimeOffset StartsAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public static EntitlementFacts From(ProductEntitlement entitlement) => new(
        entitlement.IsEnabled,
        entitlement.StartsAt,
        entitlement.ExpiresAt,
        entitlement.RevokedAt);
}

/// <summary>The account-side inputs.</summary>
public sealed record AccountFacts(UserAccountStatus Status, DateTimeOffset? SuspendedUntil);
