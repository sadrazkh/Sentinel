using Sentinel.Application.Security;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Accounts;

public sealed record AccountOverview(
    Guid UserId,
    string DisplayName,
    string UserName,
    string? Email,
    UserAccountStatus Status,
    DateTimeOffset? SuspendedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    string PreferredCulture,
    string TimeZoneId,
    int ActiveSessionCount,
    IReadOnlyList<LoginAttemptView> RecentLoginAttempts);

/// <summary>
/// A named query service rather than a generic repository: it owns one shaped read, projects
/// straight to a DTO and is easy to reason about when tuning the SQL behind it.
/// </summary>
public interface IAccountOverviewQuery
{
    Task<AccountOverview?> GetAsync(Guid userId, int recentLoginCount, CancellationToken cancellationToken = default);
}
