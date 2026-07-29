using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Notifications;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Notifications;

/// <summary>
/// Drains the notification outbox to Telegram.
/// <para>
/// Delivery is separated from creation for three reasons: writing a notification must not block
/// on a third-party API, a broadcast to every member has to be paced so Telegram does not
/// throttle the bot, and a failed send has to be retryable without re-running whatever produced
/// the message. The portal copy is written first and is authoritative — if Telegram never
/// works, the member still has the message.
/// </para>
/// </summary>
public sealed class NotificationDeliveryService : BackgroundService
{
    private readonly IDbContextFactory<SentinelDbContext> _dbFactory;
    private readonly INotificationChannel _channel;
    private readonly TelegramOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationDeliveryService> _logger;

    public NotificationDeliveryService(
        IDbContextFactory<SentinelDbContext> dbFactory,
        INotificationChannel channel,
        IOptions<TelegramOptions> options,
        TimeProvider timeProvider,
        ILogger<NotificationDeliveryService> logger)
    {
        _dbFactory = dbFactory;
        _channel = channel;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_channel.IsConfigured)
        {
            _logger.LogInformation(
                "Telegram delivery is idle: no bot configured. Notifications stay in the portal.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.DeliveryIntervalSeconds);
        using var timer = new PeriodicTimer(interval, _timeProvider);

        _logger.LogInformation(
            "Telegram delivery started, sweeping every {Interval}.", interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DeliverBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not kill the loop; the next tick tries again.
                _logger.LogError(ex, "A notification delivery sweep failed.");
            }
        }
    }

    private async Task DeliverBatchAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Only messages whose recipient is actually reachable are picked up. The join keeps
        // that decision in SQL rather than fetching rows to discard them.
        var pending = await db.Notifications
            .Where(n => n.DeliveryState == NotificationDeliveryState.Pending
                        && n.DeliveryAttempts < Notification.MaxDeliveryAttempts
                        && n.User!.TelegramUserId != null
                        && n.User.TelegramNotificationsEnabled)
            .OrderBy(n => n.CreatedAt)
            .Take(_options.DeliveryBatchSize)
            .Select(n => new { Notification = n, TelegramUserId = n.User!.TelegramUserId!.Value })
            .ToListAsync(cancellationToken);

        // Anything pending for somebody unreachable — never linked, or opted out — is retired
        // rather than left to be re-examined on every sweep for ever.
        var retired = await db.Notifications
            .Where(n => n.DeliveryState == NotificationDeliveryState.Pending
                        && (n.User!.TelegramUserId == null || !n.User.TelegramNotificationsEnabled))
            .ExecuteUpdateAsync(
                set => set.SetProperty(n => n.DeliveryState, NotificationDeliveryState.PortalOnly),
                cancellationToken);

        if (retired > 0)
        {
            _logger.LogDebug("{Count} notification(s) left as portal-only.", retired);
        }

        if (pending.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var notification = item.Notification;
            notification.DeliveryAttempts++;

            var result = await _channel.SendAsync(
                item.TelegramUserId,
                notification.Title,
                notification.Body,
                BuildAbsoluteLink(notification.LinkPath),
                cancellationToken);

            if (result.Succeeded)
            {
                notification.DeliveryState = NotificationDeliveryState.Delivered;
                notification.DeliveredAt = now;
                notification.LastFailureReason = null;
                continue;
            }

            notification.LastFailureReason = Truncate(
                result.FailureReason, Notification.FailureReasonMaxLength);

            // A permanent refusal stops immediately; a transient one is retried until the
            // attempt budget runs out, after which the message stays readable in the portal.
            var giveUp = result.IsPermanent
                         || notification.DeliveryAttempts >= Notification.MaxDeliveryAttempts;

            if (giveUp)
            {
                notification.DeliveryState = NotificationDeliveryState.Failed;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Turns a notification's local path into an absolute URL for the Telegram message.
    /// The path was already constrained to a local one when the notification was written; this
    /// only prefixes the configured public origin, and produces nothing if that is unset.
    /// </summary>
    private string? BuildAbsoluteLink(string? linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return null;
        }

        return Uri.TryCreate(new Uri(_options.PublicBaseUrl), linkPath, out var absolute)
            ? absolute.ToString()
            : null;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
