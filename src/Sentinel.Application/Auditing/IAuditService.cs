namespace Sentinel.Application.Auditing;

public interface IAuditService
{
    /// <summary>
    /// Stages an audit row on the current unit of work. The caller's
    /// <c>SaveChangesAsync</c> commits it together with the change being audited, so an
    /// audited operation and its record either both land or neither does.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an audit row immediately on its own unit of work. For events with nothing to
    /// commit alongside them, such as a failed sign-in.
    /// </summary>
    Task RecordAndSaveAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
