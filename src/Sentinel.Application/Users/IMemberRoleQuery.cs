namespace Sentinel.Application.Users;

/// <summary>
/// The role names one member holds.
/// <para>
/// A narrow question with its own contract, rather than exposing Identity's join tables on
/// <c>ISentinelDbContext</c> or handing <c>UserManager</c> to every caller that needs a role name.
/// The VPN module's audience rules need this and nothing else from Identity, and keeping it this
/// small is what stops that module from acquiring an Identity dependency.
/// </para>
/// </summary>
public interface IMemberRoleQuery
{
    /// <summary>
    /// The member's roles, or an empty set if the account does not exist. Never <c>null</c>, so a
    /// caller cannot accidentally treat "unknown" as "no restrictions".
    /// </summary>
    Task<IReadOnlySet<string>> GetRoleNamesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
