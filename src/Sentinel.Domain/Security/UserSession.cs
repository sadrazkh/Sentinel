using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Security;

/// <summary>
/// Server-side record of one signed-in browser.
/// <para>
/// The authentication cookie carries this row's id as a claim, and every request validates
/// that the row is still live. That is what makes sign-out real: deleting the client cookie
/// alone would leave a stolen cookie usable until it expired, whereas revoking the row kills
/// it immediately — and it allows signing out one device without disturbing the others.
/// </para>
/// </summary>
public class UserSession
{
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;

    /// <summary>Claim type that carries <see cref="Id"/> in the authentication cookie.</summary>
    public const string ClaimType = "sentinel:sid";

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public SessionRevocationReason RevocationReason { get; set; } = SessionRevocationReason.None;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public bool IsActiveAt(DateTimeOffset instant) => RevokedAt is null && ExpiresAt > instant;
}
