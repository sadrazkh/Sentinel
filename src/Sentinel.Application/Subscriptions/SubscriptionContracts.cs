using Sentinel.Application.Common;
using Sentinel.Domain.Subscriptions;

namespace Sentinel.Application.Subscriptions;

/// <summary>
/// One subscription as its owner sees it: the stored metadata plus whatever the last fetch
/// produced. The source URL is deliberately absent — it is the credential, and the page has no
/// reason to render it.
/// </summary>
public sealed record SubscriptionView(
    Guid Id,
    string Title,
    bool IsEnabled,
    SubscriptionFetchStatus LastFetchStatus,
    string? LastFetchError,
    DateTimeOffset? LastFetchedAt,
    long? UploadBytes,
    long? DownloadBytes,
    long? TotalBytes,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<ProxyConfig> Configs)
{
    public long? UsedBytes => UploadBytes is null && DownloadBytes is null
        ? null
        : (UploadBytes ?? 0) + (DownloadBytes ?? 0);

    public long? RemainingBytes => TotalBytes is null || UsedBytes is null
        ? null
        : Math.Max(0, TotalBytes.Value - UsedBytes.Value);

    public int? UsedPercent => TotalBytes is null or 0 || UsedBytes is null
        ? null
        : (int)Math.Clamp(UsedBytes.Value * 100 / TotalBytes.Value, 0, 100);

    public bool IsExpiredAt(DateTimeOffset instant) => ExpiresAt is { } expires && expires <= instant;

    public bool IsQuotaExhausted => RemainingBytes is 0 && TotalBytes is > 0;

    public int? DaysRemaining => ExpiresAt is not { } expires
        ? null
        : Math.Max(0, (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalDays));
}

/// <summary>Admin view: adds ownership and the fields an operator needs to spot a dead source.</summary>
public sealed record SubscriptionAdminRow(
    Guid Id,
    Guid UserId,
    string UserDisplayName,
    string UserName,
    string Title,
    bool IsEnabled,
    SubscriptionFetchStatus LastFetchStatus,
    string? LastFetchError,
    DateTimeOffset? LastFetchedAt,
    int? LastConfigCount,
    long? TotalBytes,
    long? UsedBytes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    Guid ConcurrencyToken)
{
    public bool IsExpiredAt(DateTimeOffset instant) => ExpiresAt is { } expires && expires <= instant;

    public bool IsQuotaExhausted =>
        TotalBytes is > 0 && UsedBytes is not null && UsedBytes >= TotalBytes;

    public bool IsDeadAt(DateTimeOffset instant) =>
        IsExpiredAt(instant)
        || IsQuotaExhausted
        || LastFetchStatus is SubscriptionFetchStatus.Failed or SubscriptionFetchStatus.Blocked;
}

public sealed record SaveSubscriptionRequest(
    string Title,
    string Url,
    bool IsEnabled,
    string? Notes,
    Guid? ConcurrencyToken);

public interface ISubscriptionService
{
    /// <summary>
    /// A member's own subscriptions with their configs, fetching any whose cached copy is
    /// stale. The user id comes from the authenticated principal at the call site.
    /// </summary>
    Task<IReadOnlyList<SubscriptionView>> GetForUserAsync(
        Guid userId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<OperationResult<Guid>> AddAsync(
        Guid userId,
        SaveSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a subscription. Scoped by owner as well as id, so one member cannot delete
    /// another's — and gets the same "not found" either way.
    /// </summary>
    Task<OperationResult> RemoveAsync(
        Guid userId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RefreshAsync(
        Guid userId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default);
}

public interface ISubscriptionAdminService
{
    Task<PagedResult<SubscriptionAdminRow>> SearchAsync(
        string? search,
        bool onlyDead,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubscriptionAdminRow>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<OperationResult<Guid>> AddForUserAsync(
        Guid userId,
        SaveSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every source that is expired, exhausted or failing. Returns the count.</summary>
    Task<int> DeleteDeadAsync(CancellationToken cancellationToken = default);
}

public static class SubscriptionErrors
{
    public const string Disabled = "subscription.error.disabled";
    public const string InvalidUrl = "subscription.error.invalidUrl";
    public const string BlockedUrl = "subscription.error.blockedUrl";
    public const string DuplicateUrl = "subscription.error.duplicate";
    public const string TooMany = "subscription.error.tooMany";
}
