using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Security;
using Sentinel.Domain.Common;
using Sentinel.Domain.Security;

namespace Sentinel.Infrastructure.Security;

public sealed class UserSessionService : IUserSessionService
{
    /// <summary>
    /// LastSeenAt is only written when it is at least this stale, so tracking activity does
    /// not turn every page view into a database write.
    /// </summary>
    private static readonly TimeSpan TouchThreshold = TimeSpan.FromMinutes(5);

    private readonly ISentinelDbContext _db;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public UserSessionService(ISentinelDbContext db, IClientContext clientContext, TimeProvider timeProvider)
    {
        _db = db;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public async Task<UserSession> CreateAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var session = new UserSession
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.Add(lifetime),
            IpAddress = Truncate(_clientContext.IpAddress, UserSession.IpAddressMaxLength),
            UserAgent = Truncate(_clientContext.UserAgent, UserSession.UserAgentMaxLength),
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>
    /// Deliberately uncached: this runs on every authenticated request so that revoking a
    /// session takes effect immediately and across every replica. It is a primary-key lookup
    /// of two columns, which is far cheaper than the correctness a cache would cost here.
    /// </summary>
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        return await _db.UserSessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now, cancellationToken);
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - TouchThreshold;

        await _db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null && s.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSeenAt, now), cancellationToken);
    }

    public async Task RevokeAsync(
        Guid sessionId,
        SessionRevocationReason reason,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        await _db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.RevokedAt, now)
                    .SetProperty(s => s.RevocationReason, reason),
                cancellationToken);
    }

    public async Task<int> RevokeAllForUserAsync(
        Guid userId,
        SessionRevocationReason reason,
        Guid? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        return await _db.UserSessions
            .Where(s => s.UserId == userId
                        && s.RevokedAt == null
                        && (exceptSessionId == null || s.Id != exceptSessionId))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.RevokedAt, now)
                    .SetProperty(s => s.RevocationReason, reason),
                cancellationToken);
    }

    public async Task<IReadOnlyList<ActiveSessionView>> ListActiveAsync(
        Guid userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        return await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new ActiveSessionView(
                s.Id,
                s.CreatedAt,
                s.LastSeenAt,
                s.ExpiresAt,
                s.IpAddress,
                s.UserAgent,
                currentSessionId != null && s.Id == currentSessionId))
            .ToListAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
