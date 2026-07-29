using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Subscriptions;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Subscriptions;

namespace Sentinel.Infrastructure.Subscriptions;

public sealed class SubscriptionAdminService : ISubscriptionAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IMemoryCache _cache;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;

    public SubscriptionAdminService(
        ISentinelDbContext db,
        ISubscriptionService subscriptions,
        IMemoryCache cache,
        IAuditService audit,
        TimeProvider timeProvider)
    {
        _db = db;
        _subscriptions = subscriptions;
        _cache = cache;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<SubscriptionAdminRow>> SearchAsync(
        string? search,
        bool onlyDead,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = PagingDefaults.NormalizePage(page);
        pageSize = PagingDefaults.NormalizePageSize(pageSize);

        var now = _timeProvider.GetUtcNow();
        var query = _db.SubscriptionSources.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            // Matched against the title and the owner, never against the URL: an operator
            // should not be able to confirm a subscription link by guessing at it.
            query = query.Where(s =>
                EF.Functions.Like(s.Title, $"%{term}%")
                || EF.Functions.Like(s.User!.UserName!, $"%{term}%")
                || EF.Functions.Like(s.User!.DisplayName, $"%{term}%"));
        }

        if (onlyDead)
        {
            // Evaluated in SQL so the filter pages correctly, rather than fetching everything
            // and discarding rows in memory.
            query = query.Where(s =>
                (s.ExpiresAt != null && s.ExpiresAt <= now)
                || (s.TotalBytes != null && s.TotalBytes > 0
                    && (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0) >= s.TotalBytes)
                || s.LastFetchStatus == SubscriptionFetchStatus.Failed
                || s.LastFetchStatus == SubscriptionFetchStatus.Blocked);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(s => s.ExpiresAt == null)
            .ThenBy(s => s.ExpiresAt)
            .ThenByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SubscriptionAdminRow(
                s.Id,
                s.UserId,
                s.User!.DisplayName,
                s.User.UserName!,
                s.Title,
                s.IsEnabled,
                s.LastFetchStatus,
                s.LastFetchError,
                s.LastFetchedAt,
                s.LastConfigCount,
                s.TotalBytes,
                (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0),
                s.ExpiresAt,
                s.CreatedAt,
                s.ConcurrencyToken))
            .ToListAsync(cancellationToken);

        return new PagedResult<SubscriptionAdminRow>(items, page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<SubscriptionAdminRow>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await _db.SubscriptionSources
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new SubscriptionAdminRow(
                s.Id,
                s.UserId,
                s.User!.DisplayName,
                s.User.UserName!,
                s.Title,
                s.IsEnabled,
                s.LastFetchStatus,
                s.LastFetchError,
                s.LastFetchedAt,
                s.LastConfigCount,
                s.TotalBytes,
                (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0),
                s.ExpiresAt,
                s.CreatedAt,
                s.ConcurrencyToken))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Adds a source on a member's behalf. The same validation as the self-service path — an
    /// operator's URL is no more trusted than anybody else's, because the SSRF target would be
    /// the same server either way.
    /// </summary>
    public Task<OperationResult<Guid>> AddForUserAsync(
        Guid userId,
        SaveSubscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        _subscriptions.AddAsync(userId, request, cancellationToken);

    public async Task<OperationResult> DeleteAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var source = await _db.SubscriptionSources
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

        if (source is null)
        {
            return OperationResult.Failure(OperationErrors.NotFound);
        }

        _db.SubscriptionSources.Remove(source);
        _cache.Remove($"subscription:{subscriptionId:N}");

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.SubscriptionRemoved, nameof(SubscriptionSource), subscriptionId) with
            {
                Metadata = AuditMetadata.Create().Set("ownerUserId", source.UserId),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<int> DeleteDeadAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var dead = await _db.SubscriptionSources
            .Where(s =>
                (s.ExpiresAt != null && s.ExpiresAt <= now)
                || (s.TotalBytes != null && s.TotalBytes > 0
                    && (s.UploadBytes ?? 0) + (s.DownloadBytes ?? 0) >= s.TotalBytes)
                || s.LastFetchStatus == SubscriptionFetchStatus.Failed
                || s.LastFetchStatus == SubscriptionFetchStatus.Blocked)
            .ToListAsync(cancellationToken);

        if (dead.Count == 0)
        {
            return 0;
        }

        _db.SubscriptionSources.RemoveRange(dead);

        foreach (var source in dead)
        {
            _cache.Remove($"subscription:{source.Id:N}");
        }

        await _audit.RecordAsync(
            AuditEntry.For(AuditActions.SubscriptionPurged, nameof(SubscriptionSource)) with
            {
                Metadata = AuditMetadata.Create().Set("removed", dead.Count),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return dead.Count;
    }
}
