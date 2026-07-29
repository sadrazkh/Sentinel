using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;

namespace Sentinel.Infrastructure.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly ISentinelDbContext _db;
    private readonly IDbContextFactory<Persistence.SentinelDbContext> _dbFactory;
    private readonly IClientContext _clientContext;
    private readonly TimeProvider _timeProvider;

    public NotificationService(
        ISentinelDbContext db,
        IDbContextFactory<Persistence.SentinelDbContext> dbFactory,
        IClientContext clientContext,
        TimeProvider timeProvider)
    {
        _db = db;
        _dbFactory = dbFactory;
        _clientContext = clientContext;
        _timeProvider = timeProvider;
    }

    public Task CreateAsync(
        Guid userId,
        NewNotification notification,
        CancellationToken cancellationToken = default)
    {
        _db.Notifications.Add(Build(userId, notification));
        return Task.CompletedTask;
    }

    public async Task CreateAndSaveAsync(
        Guid userId,
        NewNotification notification,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.Notifications.Add(Build(userId, notification));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> BroadcastAsync(
        NewNotification notification,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Disabled and suspended accounts are skipped: they cannot act on anything the message
        // would tell them, and a broadcast is not the way to reach somebody who is locked out.
        var recipients = await db.Users
            .AsNoTracking()
            .Where(u => u.Status == UserAccountStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (recipients.Count == 0)
        {
            return 0;
        }

        // One row per recipient rather than a shared row with a fan-out view: read state,
        // delivery state and retry count are all per person.
        db.Notifications.AddRange(recipients.Select(userId => Build(userId, notification)));
        await db.SaveChangesAsync(cancellationToken);

        return recipients.Count;
    }

    public async Task<PagedResult<NotificationListItem>> GetForUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = PagingDefaults.NormalizePage(page);
        pageSize = PagingDefaults.NormalizePageSize(pageSize);

        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationListItem(
                n.Id, n.Kind, n.Title, n.Body, n.LinkPath, n.CreatedAt, n.ReadAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<NotificationListItem>(items, page, pageSize, totalCount);
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

    public async Task<OperationResult> MarkReadAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        // Both predicates matter: the id alone would let any member mark somebody else's
        // notification read, and the differing response would confirm it existed.
        var updated = await _db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(n => n.ReadAt, _timeProvider.GetUtcNow()),
                cancellationToken);

        return updated > 0
            ? OperationResult.Success()
            : OperationResult.Failure(OperationErrors.NotFound);
    }

    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(n => n.ReadAt, _timeProvider.GetUtcNow()),
                cancellationToken);

    private Notification Build(Guid userId, NewNotification notification)
    {
        var now = _timeProvider.GetUtcNow();

        return new Notification
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            Kind = notification.Kind,
            Title = Truncate(notification.Title, Notification.TitleMaxLength),
            Body = Truncate(notification.Body, Notification.BodyMaxLength),

            // Only local paths survive. This value becomes a link the member clicks, so an
            // absolute URL here would be an open redirect delivered straight to them.
            LinkPath = NotificationLinkPolicy.Sanitize(notification.LinkPath),

            CreatedAt = now,
            CreatedByUserId = _clientContext.UserId,

            // Everything starts in the outbox; the delivery service decides what actually
            // reaches Telegram based on whether the member is linked and opted in.
            DeliveryState = notification.DeliverToTelegram
                ? NotificationDeliveryState.Pending
                : NotificationDeliveryState.PortalOnly,
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
