using Sentinel.Application.Common;
using Sentinel.Application.Notifications;

namespace Sentinel.Web.Models.Notifications;

public sealed class NotificationsViewModel
{
    public required PagedResult<NotificationListItem> Notifications { get; init; }

    public required string TimeZoneId { get; init; }

    public int UnreadCount => Notifications.Items.Count(n => !n.IsRead);
}
