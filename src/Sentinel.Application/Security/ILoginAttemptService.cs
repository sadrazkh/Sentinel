using Sentinel.Domain.Security;

namespace Sentinel.Application.Security;

public sealed record LoginAttemptView(
    DateTimeOffset OccurredAt,
    bool Succeeded,
    LoginFailureReason FailureReason,
    string? IpAddress,
    string? UserAgent);

public interface ILoginAttemptService
{
    /// <summary>
    /// Records an attempt. <paramref name="identifier"/> is the value the client typed; it is
    /// normalised and length-capped before storage and the password is never touched.
    /// </summary>
    Task RecordAsync(
        string identifier,
        Guid? userId,
        bool succeeded,
        LoginFailureReason failureReason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoginAttemptView>> GetRecentForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default);
}
