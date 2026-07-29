using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Notifications;

namespace Sentinel.Web.ViewComponents;

/// <summary>
/// The unread count in the header.
/// <para>
/// A view component rather than a base-controller property or a layout-level service call:
/// every authenticated page shows this, and threading it through each controller's view model
/// would mean every new page has to remember to populate it.
/// </para>
/// </summary>
public sealed class UnreadNotificationsViewComponent : ViewComponent
{
    private readonly INotificationService _notifications;

    public UnreadNotificationsViewComponent(INotificationService notifications) =>
        _notifications = notifications;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return View(0);
        }

        // A single indexed COUNT on (UserId, ReadAt).
        var unread = await _notifications.GetUnreadCountAsync(userId, HttpContext.RequestAborted);

        return View(unread);
    }
}
