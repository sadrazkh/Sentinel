using Sentinel.Application.Common;
using Sentinel.Domain.Notifications;

namespace Sentinel.Application.Notifications;

public sealed record NotificationListItem(
    Guid Id,
    NotificationKind Kind,
    string Title,
    string Body,
    string? LinkPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt)
{
    public bool IsRead => ReadAt is not null;
}

/// <summary>
/// A message to write. <see cref="LinkPath"/> must be a local path — it becomes a link in the
/// portal and a button in Telegram, and an absolute URL here would be an open redirect handed
/// straight to the member.
/// </summary>
public sealed record NewNotification(
    NotificationKind Kind,
    string Title,
    string Body,
    string? LinkPath = null,
    bool DeliverToTelegram = true);

public interface INotificationService
{
    /// <summary>
    /// Writes a notification for one member. Staged on the caller's unit of work so the
    /// message and whatever caused it commit together — a granted entitlement that is not
    /// announced, or an announcement for a grant that rolled back, are both wrong.
    /// </summary>
    Task CreateAsync(
        Guid userId,
        NewNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>Writes and commits immediately, for callers with nothing else pending.</summary>
    Task CreateAndSaveAsync(
        Guid userId,
        NewNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the same message for every active account. Returns how many were written.
    /// </summary>
    Task<int> BroadcastAsync(
        NewNotification notification,
        CancellationToken cancellationToken = default);

    Task<PagedResult<NotificationListItem>> GetForUserAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one notification read. Scoped by user id so a member cannot mark — or by
    /// implication confirm the existence of — somebody else's message.
    /// </summary>
    Task<OperationResult> MarkReadAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
}
