using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Notifications;

namespace Sentinel.Infrastructure.Notifications;

public sealed class ExpiryNoticeOptions
{
    public const string SectionName = "ExpiryNotices";

    /// <summary>Turns the recurring notifier off entirely. Nothing else changes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the sweep runs. Hourly by default: expiry is measured in days, so anything
    /// finer only produces load, and anything much coarser risks telling somebody their
    /// membership ended a day after it did.
    /// </summary>
    [Range(5, 24 * 60)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Delay before the first sweep, so a restart does not compete with start-up work — and so
    /// a crash loop cannot turn into a burst of notifications.
    /// </summary>
    [Range(0, 60)]
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Subjects examined per sweep, per kind. A bound keeps one sweep's memory and runtime
    /// predictable; anything left over is picked up by the next one.
    /// </summary>
    [Range(10, 5000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>Days before a subscription's expiry at which the first warning is sent.</summary>
    [Range(1, 90)]
    public int SubscriptionWarningDays { get; set; } = 7;

    [Range(50, 99)]
    public int QuotaWarningPercent { get; set; } = ExpiryNoticeRules.DefaultQuotaWarningPercent;
}
