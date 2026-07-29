using Sentinel.Domain.Security;

namespace Sentinel.Application.Security;

public sealed record ActiveSessionView(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    string? IpAddress,
    string? UserAgent,
    bool IsCurrent);

/// <summary>
/// Owns the lifetime of server-side sessions. Sign-out revokes the row here, which is what
/// makes a leaked authentication cookie stop working the moment the user logs out.
/// </summary>
public interface IUserSessionService
{
    Task<UserSession> CreateAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the session backing the current cookie is still usable. Called on every
    /// authenticated request, so results are cached briefly and evicted on revocation.
    /// </summary>
    Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid sessionId, SessionRevocationReason reason, CancellationToken cancellationToken = default);

    /// <summary>Revokes every live session for a user. Returns how many were revoked.</summary>
    Task<int> RevokeAllForUserAsync(
        Guid userId,
        SessionRevocationReason reason,
        Guid? exceptSessionId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveSessionView>> ListActiveAsync(
        Guid userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken = default);
}
