using Sentinel.Application.Access;
using Sentinel.Application.Common;
using Sentinel.Domain.Catalog;

namespace Sentinel.Application.Entitlements;

/// <summary>
/// One application as it appears on a user's entitlement editor: what the catalogue says, what
/// this user's grant says, and what the access rules currently conclude.
/// </summary>
public sealed record UserApplicationGrantRow(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationNameFa,
    string ApplicationNameEn,
    ApplicationPublishStatus PublishStatus,
    bool ApplicationIsEnabled,
    bool RequiresExplicitEntitlement,
    bool HasGrant,
    bool GrantIsEnabled,
    DateTimeOffset? GrantStartsAt,
    DateTimeOffset? GrantExpiresAt,
    DateTimeOffset? GrantRevokedAt,
    string? GrantNotes,
    Guid? ConcurrencyToken,
    AccessDecision Decision);

public sealed record GrantEntitlementRequest(
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt,
    string? Notes,
    Guid? ConcurrencyToken);

public interface IEntitlementAdminQuery
{
    /// <summary>
    /// Every application alongside this user's grant state. Drafts are included: an
    /// administrator needs to be able to pre-grant access to something not yet published.
    /// </summary>
    Task<IReadOnlyList<UserApplicationGrantRow>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public interface IEntitlementAdminService
{
    Task<OperationResult> GrantAsync(
        Guid userId,
        Guid applicationId,
        GrantEntitlementRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RevokeAsync(
        Guid userId,
        Guid applicationId,
        string? notes,
        Guid? concurrencyToken,
        CancellationToken cancellationToken = default);
}
