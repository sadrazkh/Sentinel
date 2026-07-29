using Sentinel.Application.Common;
using Sentinel.Domain.Security;

namespace Sentinel.Application.Accounts;

public sealed record ActivityEntry(
    DateTimeOffset OccurredAt,
    bool Succeeded,
    LoginFailureReason FailureReason,
    string? IpAddress,
    string? UserAgent);

public interface IActivityQuery
{
    /// <summary>
    /// A member's own sign-in history, newest first. The user id comes from the authenticated
    /// principal at the call site — there is no route parameter for it, so one member cannot
    /// page through another's history.
    /// </summary>
    Task<PagedResult<ActivityEntry>> GetSignInHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
