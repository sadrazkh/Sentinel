using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;

namespace Sentinel.Domain.Subscriptions;

public enum SubscriptionFetchStatus
{
    NeverFetched = 0,
    Succeeded = 1,
    /// <summary>The upstream server failed, timed out, or returned something unusable.</summary>
    Failed = 2,
    /// <summary>Refused by the connection policy — the target resolved to an address we will not reach.</summary>
    Blocked = 3,
}

/// <summary>
/// A subscription link belonging to one member.
/// <para>
/// The URL itself is the credential: anyone holding it can retrieve the member's configs. It is
/// stored because the portal has to re-fetch it, but it is never rendered to anybody except its
/// owner, never written to a log, and never placed in an audit entry.
/// </para>
/// <para>
/// The parsed configs are deliberately <em>not</em> persisted. They are cached in memory and
/// re-fetched when stale, so the member's proxy credentials exist in our database in exactly
/// one place rather than two. Only the metadata below — quota, expiry, counts, last status — is
/// stored, which is what the admin list needs in order to spot dead subscriptions without
/// fetching anything.
/// </para>
/// </summary>
public class SubscriptionSource : IConcurrencyAware, ITimestamped
{
    public const int TitleMaxLength = 120;
    public const int UrlMaxLength = 2048;
    public const int NotesMaxLength = 512;
    public const int ErrorMaxLength = 300;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Lets an operator park a source without deleting its history.</summary>
    public bool IsEnabled { get; set; } = true;

    public string? Notes { get; set; }

    /// <summary><c>null</c> when the member added it themselves.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // ---- last fetch -------------------------------------------------------------------

    public DateTimeOffset? LastFetchedAt { get; set; }

    public SubscriptionFetchStatus LastFetchStatus { get; set; } = SubscriptionFetchStatus.NeverFetched;

    /// <summary>A short reason for the operator. Never the response body.</summary>
    public string? LastFetchError { get; set; }

    public int? LastConfigCount { get; set; }

    // ---- reported by the provider -----------------------------------------------------

    public long? UploadBytes { get; set; }

    public long? DownloadBytes { get; set; }

    /// <summary><c>null</c> means the provider reports no quota.</summary>
    public long? TotalBytes { get; set; }

    /// <summary><c>null</c> means the provider reports no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// The stage at which this source was last mentioned to its owner. Same purpose as the one
    /// on a membership: a recurring sweep must not say the same thing twice, and a renewed
    /// subscription must start warning again.
    /// </summary>
    public ExpiryNoticeStage LastNoticeStage { get; set; } = ExpiryNoticeStage.None;

    public DateTimeOffset? LastNoticeAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public bool IsExpiredAt(DateTimeOffset instant) => ExpiresAt is { } expires && expires <= instant;

    public bool IsQuotaExhausted =>
        TotalBytes is > 0 && (UploadBytes ?? 0) + (DownloadBytes ?? 0) >= TotalBytes;

    /// <summary>
    /// What the admin list calls "dead": expired, out of data, or failing to fetch. These are
    /// the rows worth deleting so a member's page is not cluttered with subscriptions that no
    /// longer do anything.
    /// </summary>
    public bool IsDeadAt(DateTimeOffset instant) =>
        IsExpiredAt(instant)
        || IsQuotaExhausted
        || LastFetchStatus is SubscriptionFetchStatus.Failed or SubscriptionFetchStatus.Blocked;
}
