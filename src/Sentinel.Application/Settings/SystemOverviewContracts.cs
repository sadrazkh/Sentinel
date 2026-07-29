namespace Sentinel.Application.Settings;

public sealed record SystemCounters(
    int TotalUsers,
    int ActiveUsers,
    int SuspendedUsers,
    int DisabledUsers,
    int ActiveMemberships,
    int ExpiringSoon,
    int TotalApplications,
    int PublishedApplications,
    int ActiveEntitlements,
    int ActiveSessions,
    int FailedSignInsLast24Hours,
    long AuditEntries);

public interface ISystemOverviewQuery
{
    Task<SystemCounters> GetCountersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One effective setting, as the operator sees it on the settings page.
/// <para>
/// <see cref="Value"/> is only ever a non-secret: connection strings, seed passwords and key
/// material are never surfaced. A settings screen that renders whatever is in configuration is
/// a credential-disclosure page waiting for its first over-privileged viewer.
/// </para>
/// </summary>
public sealed record SettingRow(string Key, string Value, string DescriptionKey, bool IsSensitiveArea);
