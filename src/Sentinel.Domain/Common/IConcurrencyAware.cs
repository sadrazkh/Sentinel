namespace Sentinel.Domain.Common;

/// <summary>
/// Opt-in optimistic concurrency for entities whose edits are safety relevant
/// (memberships, entitlements, application settings).
/// <para>
/// A plain <see cref="Guid"/> token is used rather than a provider-specific mechanism
/// (PostgreSQL <c>xmin</c>, SQL Server <c>rowversion</c>) so the model stays portable.
/// The token is rotated centrally in <c>SentinelDbContext.SaveChangesAsync</c>.
/// </para>
/// </summary>
public interface IConcurrencyAware
{
    Guid ConcurrencyToken { get; set; }
}
