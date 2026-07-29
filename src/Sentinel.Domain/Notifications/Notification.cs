using Sentinel.Domain.Identity;

namespace Sentinel.Domain.Notifications;

public enum NotificationKind
{
    /// <summary>Written by an administrator, to one member or to everybody.</summary>
    AdminMessage = 1,

    /// <summary>Membership created, renewed, suspended or about to expire.</summary>
    Membership = 2,

    /// <summary>An individual application grant was given or taken away.</summary>
    Entitlement = 3,

    /// <summary>Account status, password, sessions — anything the member should notice.</summary>
    Security = 4,

    /// <summary>Subscription quota or expiry.</summary>
    Subscription = 5,

    /// <summary>Anything else the portal wants to say.</summary>
    System = 6,
}

public enum NotificationDeliveryState
{
    /// <summary>Visible in the portal; not queued for Telegram.</summary>
    PortalOnly = 0,

    /// <summary>Waiting for the delivery service to pick it up.</summary>
    Pending = 1,

    Delivered = 2,

    /// <summary>Gave up after the retry budget. The portal copy is still readable.</summary>
    Failed = 3,
}

/// <summary>
/// One message for one member.
/// <para>
/// The portal copy is the record; Telegram is a delivery channel on top of it. Writing the row
/// first and delivering from it afterwards — an outbox — means creating a notification never
/// blocks on Telegram's API, a member never loses a message because the bot was rate-limited,
/// and a failed send can be retried without re-running whatever produced it.
/// </para>
/// </summary>
public class Notification
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 2000;
    public const int FailureReasonMaxLength = 300;

    /// <summary>How many delivery attempts before the message is left as portal-only.</summary>
    public const int MaxDeliveryAttempts = 5;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public NotificationKind Kind { get; set; } = NotificationKind.System;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Optional in-portal destination, always a local path.</summary>
    public string? LinkPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>null</c> for messages the system generated rather than a person.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public NotificationDeliveryState DeliveryState { get; set; } = NotificationDeliveryState.PortalOnly;

    public int DeliveryAttempts { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>Diagnostic only, never shown to the member.</summary>
    public string? LastFailureReason { get; set; }

    public bool IsRead => ReadAt is not null;
}
