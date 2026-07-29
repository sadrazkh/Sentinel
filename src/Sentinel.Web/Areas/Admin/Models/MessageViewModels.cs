using System.ComponentModel.DataAnnotations;
using Sentinel.Domain.Notifications;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class SendMessageViewModel
{
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(Notification.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.messages.subject")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(Notification.BodyMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.messages.body")]
    public string Body { get; set; } = string.Empty;

    [Display(Name = "admin.messages.deliverToTelegram")]
    public bool DeliverToTelegram { get; set; } = true;
}

public sealed class BroadcastMessageViewModel
{
    /// <summary>
    /// Typed back by the operator before a broadcast goes out. A broadcast reaches every active
    /// member and cannot be recalled, so it gets a deliberate friction step rather than a
    /// dialog that a reflex click dismisses.
    /// </summary>
    public const string RequiredConfirmation = "SEND";

    [Required(ErrorMessage = "validation.required")]
    [StringLength(Notification.TitleMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.messages.subject")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(Notification.BodyMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.messages.body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Optional in-portal destination. Rejected unless it is a local path.</summary>
    [StringLength(512, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.messages.link")]
    public string? LinkPath { get; set; }

    [Display(Name = "admin.messages.deliverToTelegram")]
    public bool DeliverToTelegram { get; set; } = true;

    [Display(Name = "admin.messages.confirmation")]
    public string? Confirmation { get; set; }
}
