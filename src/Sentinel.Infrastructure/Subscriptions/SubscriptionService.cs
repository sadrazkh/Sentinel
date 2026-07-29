using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Subscriptions;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Subscriptions;

namespace Sentinel.Infrastructure.Subscriptions;

public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISentinelDbContext _db;
    private readonly ISubscriptionFetcher _fetcher;
    private readonly IMemoryCache _cache;
    private readonly IAuditService _audit;
    private readonly IClientContext _clientContext;
    private readonly SubscriptionFetchOptions _options;
    private readonly TimeProvider _timeProvider;

    public SubscriptionService(
        ISentinelDbContext db,
        ISubscriptionFetcher fetcher,
        IMemoryCache cache,
        IAuditService audit,
        IClientContext clientContext,
        IOptions<SubscriptionFetchOptions> options,
        TimeProvider timeProvider)
    {
        _db = db;
        _fetcher = fetcher;
        _cache = cache;
        _audit = audit;
        _clientContext = clientContext;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<SubscriptionView>> GetForUserAsync(
        Guid userId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var sources = await _db.SubscriptionSources
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var views = new List<SubscriptionView>(sources.Count);

        foreach (var source in sources)
        {
            var configs = source.IsEnabled
                ? await GetConfigsAsync(source, forceRefresh, cancellationToken)
                : [];

            views.Add(ToView(source, configs));
        }

        // The loop above may have updated fetch metadata on several sources.
        await _db.SaveChangesAsync(cancellationToken);

        return views;
    }

    /// <summary>
    /// Returns the parsed configs, fetching only when the cached copy has expired.
    /// <para>
    /// Cached in memory rather than stored: these carry the member's proxy credentials, and the
    /// subscription URL we already keep is enough to obtain them again. Holding a second copy
    /// in the database would widen what a stolen backup exposes for no real benefit.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ProxyConfig>> GetConfigsAsync(
        SubscriptionSource source,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"subscription:{source.Id:N}";

        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<ProxyConfig>? cached))
        {
            return cached ?? [];
        }

        var result = await _fetcher.FetchAsync(source.Url, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        source.LastFetchedAt = now;

        if (!result.Succeeded)
        {
            source.LastFetchStatus = result.Outcome == SubscriptionFetchOutcome.BlockedAddress
                ? SubscriptionFetchStatus.Blocked
                : SubscriptionFetchStatus.Failed;

            source.LastFetchError = Truncate(result.Reason, SubscriptionSource.ErrorMaxLength);

            // A failed fetch is cached briefly too, so an unreachable host cannot be turned
            // into one outbound request per page view.
            _cache.Set(cacheKey, (IReadOnlyList<ProxyConfig>)[], TimeSpan.FromMinutes(2));

            return [];
        }

        source.LastFetchStatus = SubscriptionFetchStatus.Succeeded;
        source.LastFetchError = null;
        source.LastConfigCount = result.Content.Configs.Count;

        var info = result.Content.UserInfo;
        source.UploadBytes = info.UploadBytes;
        source.DownloadBytes = info.DownloadBytes;
        source.TotalBytes = info.TotalBytes;
        source.ExpiresAt = info.ExpiresAt;

        _cache.Set(cacheKey, result.Content.Configs, TimeSpan.FromMinutes(_options.CacheMinutes));

        return result.Content.Configs;
    }

    public async Task<OperationResult<Guid>> AddAsync(
        Guid userId,
        SaveSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return OperationResult<Guid>.Failure(SubscriptionErrors.Disabled);
        }

        var rejection = SubscriptionUrlPolicy.Validate(request.Url, out var uri);

        if (rejection != SubscriptionUrlRejection.None)
        {
            // "Blocked" and "invalid" are separated so the message can say something useful
            // without describing the internal network.
            return OperationResult<Guid>.Failure(
                rejection is SubscriptionUrlRejection.DisallowedHost
                    or SubscriptionUrlRejection.DisallowedScheme
                    or SubscriptionUrlRejection.NonStandardPort
                    ? SubscriptionErrors.BlockedUrl
                    : SubscriptionErrors.InvalidUrl);
        }

        var url = uri!.ToString();

        var existing = await _db.SubscriptionSources
            .CountAsync(s => s.UserId == userId, cancellationToken);

        if (existing >= _options.MaxSourcesPerUser)
        {
            return OperationResult<Guid>.Failure(SubscriptionErrors.TooMany);
        }

        if (await _db.SubscriptionSources.AnyAsync(
                s => s.UserId == userId && s.Url == url, cancellationToken))
        {
            return OperationResult<Guid>.Failure(SubscriptionErrors.DuplicateUrl);
        }

        var now = _timeProvider.GetUtcNow();

        var source = new SubscriptionSource
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            Title = Clean(request.Title, SubscriptionSource.TitleMaxLength) ?? "Subscription",
            Url = url,
            IsEnabled = request.IsEnabled,
            Notes = Clean(request.Notes, SubscriptionSource.NotesMaxLength),
            CreatedByUserId = _clientContext.UserId,
        };

        _db.SubscriptionSources.Add(source);

        // The URL is the credential that retrieves the member's configs, so the audit entry
        // records that a source was added and by whom — never the link itself.
        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.SubscriptionAdded, nameof(SubscriptionSource), source.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("ownerUserId", userId)
                    .Set("host", uri.Host),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(source.Id);
    }

    public async Task<OperationResult> RemoveAsync(
        Guid userId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        // Scoped by owner as well as id: without the owner predicate any member could delete
        // another's subscription by guessing an identifier.
        var source = await _db.SubscriptionSources
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.UserId == userId, cancellationToken);

        if (source is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        _db.SubscriptionSources.Remove(source);
        _cache.Remove($"subscription:{subscriptionId:N}");

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.SubscriptionRemoved, nameof(SubscriptionSource), subscriptionId) with
            {
                Metadata = AuditMetadata.Create().Set("ownerUserId", userId),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RefreshAsync(
        Guid userId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var source = await _db.SubscriptionSources
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.UserId == userId, cancellationToken);

        if (source is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        await GetConfigsAsync(source, forceRefresh: true, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    private static SubscriptionView ToView(SubscriptionSource source, IReadOnlyList<ProxyConfig> configs) =>
        new(
            source.Id,
            source.Title,
            source.IsEnabled,
            source.LastFetchStatus,
            source.LastFetchError,
            source.LastFetchedAt,
            source.UploadBytes,
            source.DownloadBytes,
            source.TotalBytes,
            source.ExpiresAt,
            configs);

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), maxLength);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
