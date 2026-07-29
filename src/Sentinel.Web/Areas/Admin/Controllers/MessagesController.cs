using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Auditing;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Application.Users;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// Administrator messages: one member, or everybody.
/// <para>
/// Write-only for administrators — there is no endpoint that reads a member's notification
/// list back. Operators need to send announcements, not to browse somebody's inbox.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeWrite)]
public sealed class MessagesController : Controller
{
    private readonly INotificationService _notifications;
    private readonly IUserAdminQuery _users;
    private readonly IAuditService _audit;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public MessagesController(
        INotificationService notifications,
        IUserAdminQuery users,
        IAuditService audit,
        IStringLocalizer<SharedResource> localizer)
    {
        _notifications = notifications;
        _users = users;
        _audit = audit;
        _localizer = localizer;
    }

    [HttpGet]
    public IActionResult Broadcast() => View(new BroadcastMessageViewModel());

    [HttpPost]
    public async Task<IActionResult> Broadcast(
        BroadcastMessageViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Deliberately not a confirm-free action: a broadcast reaches every active member and
        // cannot be recalled, so the form asks for the word to be typed back.
        if (!string.Equals(model.Confirmation, BroadcastMessageViewModel.RequiredConfirmation,
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                nameof(model.Confirmation), _localizer["admin.messages.confirmRequired"].Value);

            return View(model);
        }

        var count = await _notifications.BroadcastAsync(
            new NewNotification(
                NotificationKind.AdminMessage,
                model.Title,
                model.Body,
                model.LinkPath,
                model.DeliverToTelegram),
            cancellationToken);

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.NotificationBroadcast, nameof(Notification)) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("recipients", count)
                    .Set("title", model.Title),
            },
            cancellationToken);

        TempData["StatusMessage"] = _localizer["admin.messages.broadcastSent", count].Value;
        return RedirectToAction(nameof(Broadcast));
    }

    [HttpPost]
    public async Task<IActionResult> SendToUser(
        SendMessageViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer[OperationErrors.IdentityRejected].Value;
            return RedirectToUser(model.UserId);
        }

        // Confirms the recipient exists before writing, so a mistyped id fails loudly rather
        // than creating an orphaned message.
        if (await _users.GetDetailAsync(model.UserId, cancellationToken) is null)
        {
            return NotFound();
        }

        await _notifications.CreateAndSaveAsync(
            model.UserId,
            new NewNotification(
                NotificationKind.AdminMessage,
                model.Title,
                model.Body,
                DeliverToTelegram: model.DeliverToTelegram),
            cancellationToken);

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.NotificationSent, nameof(ApplicationUser), model.UserId) with
            {
                Metadata = AuditMetadata.Create().Set("title", model.Title),
            },
            cancellationToken);

        TempData["StatusMessage"] = _localizer["admin.messages.sent"].Value;
        return RedirectToUser(model.UserId);
    }

    private IActionResult RedirectToUser(Guid userId) =>
        RedirectToAction("Details", "Users", new { id = userId });
}
