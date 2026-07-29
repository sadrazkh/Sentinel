using System.ComponentModel.DataAnnotations;

namespace Sentinel.Infrastructure.Notifications;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>
    /// Master switch. With this off the whole integration is dormant: no polling, no delivery,
    /// and the portal simply does not offer to link an account. Notifications are still
    /// written and still readable in the portal.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Supplied only through <c>Telegram__BotToken</c> or a secret store. A bot token is a full
    /// credential — anyone holding it can read and send everything the bot can — so it is never
    /// written to a configuration file, never logged, and never rendered on a settings page.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Used to build the <c>https://t.me/&lt;bot&gt;?start=…</c> deep link. Not a secret.</summary>
    [StringLength(64)]
    public string BotUsername { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL of the portal, used to turn a notification's local path into a link
    /// Telegram can render. Validated as absolute https at start-up.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Long polling. Simple and works anywhere, including a laptop with no public address.
    /// A webhook would be cheaper at scale but needs an inbound route and its own secret path.
    /// </summary>
    public bool UsePolling { get; set; } = true;

    /// <summary>How often the outbox is swept for undelivered notifications.</summary>
    [Range(5, 3600)]
    public int DeliveryIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Messages sent per sweep. Telegram throttles aggressively for bulk sends, and a broadcast
    /// to every member would otherwise trip it and get the bot temporarily blocked.
    /// </summary>
    [Range(1, 100)]
    public int DeliveryBatchSize { get; set; } = 20;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BotToken)
        && !string.IsNullOrWhiteSpace(BotUsername);
}
