using Microsoft.AspNetCore.Identity;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;
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

    public string DisplayName { get; set; } = string.Empty;

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
}
