using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Security;

/// <summary>
/// One sign-in attempt, successful or not. Drives the member-facing login history and
/// gives operators the data to spot credential stuffing.
/// </summary>
public class LoginAttempt
{
    public const int IdentifierMaxLength = 256;
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;

    public Guid Id { get; set; }

    /// <summary>
    /// The identifier the client typed, normalised and length-capped. Kept for abuse
    /// analysis; it is not a credential and the password is never stored anywhere.
    /// </summary>
    public string AttemptedIdentifier { get; set; } = string.Empty;

    /// <summary><c>null</c> when no account matched the identifier.</summary>
    public Guid? UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public bool Succeeded { get; set; }

    public LoginFailureReason FailureReason { get; set; } = LoginFailureReason.None;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
