using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Auditing;

/// <summary>
/// Append-only record of a security-relevant operation.
/// <para>
/// Never contains passwords, tokens, secrets or password hashes: <see cref="MetadataJson"/>
/// is produced through <c>AuditMetadata</c>, which only accepts an explicit allow-list of keys.
/// </para>
/// </summary>
public class AuditLog
{
    public const int ActorNameMaxLength = 256;
    public const int ActionMaxLength = 128;
    public const int EntityTypeMaxLength = 128;
    public const int EntityIdMaxLength = 128;
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;
    public const int CorrelationIdMaxLength = 64;
    public const int MetadataMaxLength = 4000;

    public Guid Id { get; set; }

    /// <summary><c>null</c> for anonymous events such as a failed login for an unknown user.</summary>
    public Guid? ActorUserId { get; set; }

    public ApplicationUser? ActorUser { get; set; }

    /// <summary>Denormalised so the entry stays readable if the account is later renamed.</summary>
    public string? ActorUserName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public AuditResult Result { get; set; } = AuditResult.Success;

    /// <summary>Ties the entry to the request's correlation id so logs and audit rows line up.</summary>
    public string? CorrelationId { get; set; }

    public string? MetadataJson { get; set; }
}
