using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Memberships;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Notifications;

/// <summary>
/// Warns members that a membership or a subscription is running out.
/// <para>
/// The timer is the easy part. Two things make this worth its own service:
/// </para>
/// <list type="bullet">
/// <item>A member must be told once per stage, not once per sweep. Each subject records the
/// stage it was last mentioned at, and only an advance produces a message — while a renewal
/// moves the stage back down and re-arms the next cycle.</item>
/// <item>Every replica runs this. The stage marker sits on a row with an optimistic
/// concurrency token, so two instances reaching the same subject at the same moment means one
/// of them loses the write and sends nothing.</item>
/// </list>
/// </summary>
public sealed class ExpiryNoticeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExpiryNoticeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiryNoticeService> _logger;

    public ExpiryNoticeService(
        IServiceScopeFactory scopeFactory,
        IOptions<ExpiryNoticeOptions> options,
        TimeProvider timeProvider,
        ILogger<ExpiryNoticeService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Expiry notices are disabled.");
            return;
        }

        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_options.StartupDelaySeconds), _timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        using var timer = new PeriodicTimer(interval, _timeProvider);

        _logger.LogInformation("Expiry notices started, sweeping every {Interval}.", interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the loop; the next tick tries again.
                _logger.LogError(ex, "An expiry-notice sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Exposed for tests, which drive one sweep rather than waiting on a timer.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IMembershipStatusResolver>();
        var localizer = scope.ServiceProvider.GetRequiredService<INotificationLocalizer>();

        var now = _timeProvider.GetUtcNow();

        var sent = await SweepMembershipsAsync(db, resolver, localizer, now, cancellationToken);
        sent += await SweepSubscriptionsAsync(db, localizer, now, cancellationToken);

        if (sent > 0)
        {
            _logger.LogInformation("Expiry sweep raised {Count} notification(s).", sent);
        }

        return sent;
    }

    private async Task<int> SweepMembershipsAsync(
        SentinelDbContext db,
        IMembershipStatusResolver resolver,
        INotificationLocalizer localizer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Only accounts that can act on the message. Somebody disabled or suspended cannot
        // renew, and telling them their membership is ending helps nobody.
        var memberships = await db.Memberships
            .Include(m => m.User)
            .Where(m => m.User!.Status == UserAccountStatus.Active)
            .OrderBy(m => m.EndsAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;

        foreach (var membership in memberships)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = resolver.Resolve(MembershipFacts.From(membership), now);
            var reason = ExpiryNoticeRules.EvaluateMembership(snapshot);
            var stage = ExpiryNoticeRules.StageFor(reason);

            if (!ExpiryNoticeRules.ShouldNotify(stage, membership.LastNoticeStage))
            {
                // Record a lower stage without saying anything. This is the renewal case, and
                // it is what allows the next expiry to warn again.
                if (stage < membership.LastNoticeStage)
                {
                    membership.LastNoticeStage = stage;
                    await SaveIgnoringConflictAsync(db, cancellationToken);
                }

                continue;
            }

            var culture = membership.User!.PreferredCulture;
            var days = snapshot.DaysRemaining ?? 0;

            membership.LastNoticeStage = stage;
            membership.LastNoticeAt = now;

            db.Notifications.Add(Build(
                membership.UserId, reason, localizer, culture, now, days));

            if (await SaveIgnoringConflictAsync(db, cancellationToken))
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<int> SweepSubscriptionsAsync(
        SentinelDbContext db,
        INotificationLocalizer localizer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sources = await db.SubscriptionSources
            .Include(s => s.User)
            .Where(s => s.IsEnabled && s.User!.Status == UserAccountStatus.Active)
            .OrderBy(s => s.ExpiresAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var used = source.UploadBytes is null && source.DownloadBytes is null
                ? null
                : (long?)((source.UploadBytes ?? 0) + (source.DownloadBytes ?? 0));

            var reason = ExpiryNoticeRules.EvaluateSubscription(
                source.ExpiresAt,
                source.TotalBytes,
                used,
                now,
                _options.SubscriptionWarningDays,
                _options.QuotaWarningPercent);

            var stage = ExpiryNoticeRules.StageFor(reason);

            if (!ExpiryNoticeRules.ShouldNotify(stage, source.LastNoticeStage))
            {
                if (stage < source.LastNoticeStage)
                {
                    source.LastNoticeStage = stage;
                    await SaveIgnoringConflictAsync(db, cancellationToken);
                }

                continue;
            }

            var days = source.ExpiresAt is { } expires
                ? Math.Max(0, (int)Math.Ceiling((expires - now).TotalDays))
                : 0;

            source.LastNoticeStage = stage;
            source.LastNoticeAt = now;

            db.Notifications.Add(Build(
                source.UserId, reason, localizer, source.User!.PreferredCulture, now,
                days, source.Title));

            if (await SaveIgnoringConflictAsync(db, cancellationToken))
            {
                sent++;
            }
        }

        return sent;
    }

    private static Notification Build(
        Guid userId,
        ExpiryNoticeReason reason,
        INotificationLocalizer localizer,
        string? culture,
        DateTimeOffset now,
        int days,
        string? subjectName = null)
    {
        // Written in the recipient's own language, not the server's ambient culture — a
        // background sweep has no request to inherit one from.
        var title = localizer.Get(ExpiryNoticeRules.TitleKey(reason), culture);
        var body = subjectName is null
            ? localizer.Get(ExpiryNoticeRules.BodyKey(reason), culture, days)
            : localizer.Get(ExpiryNoticeRules.BodyKey(reason), culture, subjectName, days);

        return new Notification
        {
            Id = SequentialGuid.New(now),
            UserId = userId,
            Kind = ExpiryNoticeRules.KindFor(reason),
            Title = title,
            Body = body,
            LinkPath = NotificationLinkPolicy.Sanitize(ExpiryNoticeRules.LinkPath(reason)),
            CreatedAt = now,

            // Queued for Telegram like any other notification; the delivery service decides
            // whether the member is actually reachable there.
            DeliveryState = NotificationDeliveryState.Pending,
        };
    }

    /// <summary>
    /// Commits, treating a concurrency conflict as "another replica got there first".
    /// <para>
    /// The stage marker lives on a row carrying an optimistic concurrency token, so losing the
    /// race means the message was already sent by somebody else — which is exactly the outcome
    /// wanted. Returns whether this instance was the one that wrote.
    /// </para>
    /// </summary>
    private async Task<bool> SaveIgnoringConflictAsync(
        SentinelDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Another instance had already handled this subject; skipping.");

            // The tracked graph is now unusable for this entity; drop the pending change so the
            // rest of the sweep can continue.
            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }
}
