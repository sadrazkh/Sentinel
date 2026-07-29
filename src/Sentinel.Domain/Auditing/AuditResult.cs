namespace Sentinel.Domain.Auditing;

public enum AuditResult
{
    Success = 1,

    /// <summary>The operation was attempted and failed (bad input, conflict, downstream error).</summary>
    Failure = 2,

    /// <summary>The operation was refused by an authorization or access rule.</summary>
    Denied = 3,
}
