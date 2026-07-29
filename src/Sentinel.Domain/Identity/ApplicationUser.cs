using Microsoft.AspNetCore.Identity;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;
using Sentinel.Domain.Security;

namespace Sentinel.Domain.Identity;

/// <summary>
/// Portal user. Credentials, lockout and security stamp are owned by ASP.NET Core Identity;
/// everything below is portal-specific state.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>, ITimestamped
{
    public const int DisplayNameMaxLength = 128;
    public const int StatusNoteMaxLength = 512;
    public const int CultureMaxLength = 16;
    public const int TimeZoneMaxLength = 64;
    public const int NormalizedPhoneMaxLength = 16;
    public const int TelegramUsernameMaxLength = 64;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Canonical <c>+&lt;digits&gt;</c> form of <see cref="IdentityUser{TKey}.PhoneNumber"/>,
    /// produced by <c>PhoneNumberNormalizer</c>. Unique when present, which is what allows
    /// signing in by phone: the lookup is an indexed equality match and two accounts can never
    /// claim the same number in different spellings.
    /// </summary>
    public string? NormalizedPhoneNumber { get; set; }

    /// <summary>
    /// The linked Telegram account, or <c>null</c>. Unique when present: one Telegram account
    /// maps to one portal account, so a message can never be delivered to the wrong person and
    /// a shared Telegram cannot be used to reach two members' notifications.
    /// </summary>
    public long? TelegramUserId { get; set; }

    /// <summary>Display convenience only. Telegram usernames change; the id is the identity.</summary>
    public string? TelegramUsername { get; set; }

    public DateTimeOffset? TelegramLinkedAt { get; set; }

    /// <summary>
    /// Lets a member keep the link but silence the channel. Notifications are still written to
    /// the portal — turning delivery off must not lose the message.
    /// </summary>
    public bool TelegramNotificationsEnabled { get; set; } = true;

    public UserAccountStatus Status { get; set; } = UserAccountStatus.Active;

    /// <summary>
    /// When set together with <see cref="UserAccountStatus.Suspended"/>, the suspension
    /// lapses automatically at this instant. <c>null</c> means "until an admin lifts it".
    /// </summary>
    public DateTimeOffset? SuspendedUntil { get; set; }

    /// <summary>Admin-facing note explaining the current <see cref="Status"/>. Never shown to the member.</summary>
    public string? StatusNote { get; set; }

    /// <summary>BCP-47 tag used to render the portal ("fa" or "en").</summary>
    public string PreferredCulture { get; set; } = "fa";

    /// <summary>IANA time zone id used to render UTC timestamps for this user.</summary>
    public string TimeZoneId { get; set; } = "Asia/Tehran";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public Membership? Membership { get; set; }

    public ICollection<UserEntitlement> Entitlements { get; set; } = new List<UserEntitlement>();

    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();

    public ICollection<LoginAttempt> LoginAttempts { get; set; } = new List<LoginAttempt>();

    public ICollection<AuditLog> ActedAuditLogs { get; set; } = new List<AuditLog>();

    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
