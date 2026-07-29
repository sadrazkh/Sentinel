using Sentinel.Domain.Auditing;

namespace Sentinel.Application.Auditing;

/// <summary>
/// One audited operation, as described by the caller. Actor, IP, user agent and correlation
/// id are filled in by <c>IAuditService</c> from the ambient request context, so no call site
/// can forget them or get them wrong.
/// </summary>
public sealed record AuditEntry
{
    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public string? EntityId { get; init; }

    public AuditResult Result { get; init; } = AuditResult.Success;

    public AuditMetadata? Metadata { get; init; }

    /// <summary>Overrides the ambient actor. Used for events that happen before sign-in completes.</summary>
    public Guid? ActorUserIdOverride { get; init; }

    public string? ActorUserNameOverride { get; init; }

    public static AuditEntry For(string action, string entityType, object? entityId = null) => new()
    {
        Action = action,
        EntityType = entityType,
        EntityId = entityId?.ToString(),
    };
}
