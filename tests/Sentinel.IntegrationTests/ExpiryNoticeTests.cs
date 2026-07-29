using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;
using Sentinel.Infrastructure.Notifications;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The recurring notifier, driven a sweep at a time.
/// <para>
/// Every test here runs the sweep more than once, because the failure this job is most likely
/// to have is not "never warns" — it is "warns every hour for a week".
/// </para>
/// </summary>
public sealed class ExpiryNoticeTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public ExpiryNoticeTests(SentinelWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// Runs one sweep against the live service. The hosted service is registered as a singleton
    /// background worker; this reaches the same instance and calls its sweep directly rather
    /// than waiting on a timer.
    /// </summary>
    private Task<int> SweepAsync() =>
        _factory.WithScopeAsync(async services =>
        {
            var job = services.GetServices<IHostedService>().OfType<ExpiryNoticeService>().Single();
            return await job.SweepAsync(CancellationToken.None);
        });

    private Task<List<Notification>> NoticesAsync(Guid userId, NotificationKind kind) =>
        _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.Notifications
                .AsNoTracking()
                .Where(n => n.UserId == userId && n.Kind == kind)
                .OrderBy(n => n.CreatedAt)
                .ToListAsync();
        });

    // --------------------------------------------------------------------- memberships ----

    [Fact]
    public async Task A_membership_nearing_expiry_produces_exactly_one_warning()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-approaching", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(2));

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        // Three sweeps, one message. This is the behaviour the whole design exists for.
        var notices = await NoticesAsync(userId, NotificationKind.Membership);
        Assert.Single(notices);
    }

    [Fact]
    public async Task A_healthy_membership_produces_nothing()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-healthy", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(300));

        await SweepAsync();

        Assert.Empty(await NoticesAsync(userId, NotificationKind.Membership));
    }

    [Fact]
    public async Task An_expired_membership_produces_a_warning()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-expired", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-60));

        await SweepAsync();
        await SweepAsync();

        var notices = await NoticesAsync(userId, NotificationKind.Membership);
        Assert.Single(notices);
    }

    [Fact]
    public async Task A_membership_crossing_into_expiry_produces_a_second_message()
    {
        // First the countdown, later the end. Two distinct events deserve two messages — and
        // the stage marker is what keeps it at two rather than one per sweep.
        var userId = await _factory.CreateMemberAsync(
            "notice-progression", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(2));

        await SweepAsync();
        await SweepAsync();

        Assert.Single(await NoticesAsync(userId, NotificationKind.Membership));

        // Time passes: the membership is now well past its grace period.
        await _factory.SetMembershipEndAsync(userId, DateTimeOffset.UtcNow.AddDays(-60));

        await SweepAsync();
        await SweepAsync();

        Assert.Equal(2, (await NoticesAsync(userId, NotificationKind.Membership)).Count);
    }

    [Fact]
    public async Task Renewing_re_arms_the_warning_for_the_next_cycle()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-renewal", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-60));

        await SweepAsync();
        Assert.Single(await NoticesAsync(userId, NotificationKind.Membership));

        // Renewed for a year: nothing to say, and the marker drops back.
        await _factory.SetMembershipEndAsync(userId, DateTimeOffset.UtcNow.AddDays(365));
        await SweepAsync();
        Assert.Single(await NoticesAsync(userId, NotificationKind.Membership));

        // A year later it is running out again — and this must warn, not stay silent because
        // it was once at the Ended stage.
        await _factory.SetMembershipEndAsync(userId, DateTimeOffset.UtcNow.AddDays(2));
        await SweepAsync();

        Assert.Equal(2, (await NoticesAsync(userId, NotificationKind.Membership)).Count);
    }

    [Fact]
    public async Task A_disabled_account_is_not_warned()
    {
        // Somebody who cannot sign in cannot renew either.
        var userId = await _factory.CreateMemberAsync(
            "notice-disabled", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(1));

        await _factory.SetAccountStatusAsync(userId, UserAccountStatus.Disabled);
        await SweepAsync();

        Assert.Empty(await NoticesAsync(userId, NotificationKind.Membership));
    }

    [Fact]
    public async Task A_suspended_membership_is_not_nagged_about_renewal()
    {
        // Suspension is an administrator decision; "renew soon" would be misleading.
        var userId = await _factory.CreateMemberAsync(
            "notice-suspended-membership",
            membershipState: MembershipAdminState.Suspended,
            membershipEndsAt: DateTimeOffset.UtcNow.AddDays(1));

        await SweepAsync();

        Assert.Empty(await NoticesAsync(userId, NotificationKind.Membership));
    }

    // ------------------------------------------------------------------- subscriptions ----

    [Fact]
    public async Task An_expired_subscription_produces_exactly_one_warning()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-sub-expired", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(300));

        var subscriptionId = await _factory.AddSubscriptionAsync(
            userId, "https://sub.example.info/api/notice-expired", "Expiring sub");

        await _factory.SetSubscriptionExpiryAsync(subscriptionId, DateTimeOffset.UtcNow.AddDays(-2));

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        var notices = await NoticesAsync(userId, NotificationKind.Subscription);
        Assert.Single(notices);
        Assert.Equal("/Configs", notices[0].LinkPath);
    }

    [Fact]
    public async Task An_exhausted_quota_produces_a_warning()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-sub-quota", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(300));

        var subscriptionId = await _factory.AddSubscriptionAsync(
            userId, "https://sub.example.info/api/notice-quota", "Used up");

        await _factory.SetSubscriptionQuotaAsync(subscriptionId, total: 1000, used: 1000);

        await SweepAsync();
        await SweepAsync();

        Assert.Single(await NoticesAsync(userId, NotificationKind.Subscription));
    }

    [Fact]
    public async Task A_healthy_subscription_produces_nothing()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-sub-healthy", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(300));

        var subscriptionId = await _factory.AddSubscriptionAsync(
            userId, "https://sub.example.info/api/notice-healthy", "Fine");

        await _factory.SetSubscriptionExpiryAsync(subscriptionId, DateTimeOffset.UtcNow.AddDays(120));
        await _factory.SetSubscriptionQuotaAsync(subscriptionId, total: 1000, used: 10);

        await SweepAsync();

        Assert.Empty(await NoticesAsync(userId, NotificationKind.Subscription));
    }

    // ------------------------------------------------------------------------ delivery ----

    [Fact]
    public async Task A_notice_is_queued_for_telegram_and_written_in_the_members_language()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-language", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-60));

        await SweepAsync();

        var notice = Assert.Single(await NoticesAsync(userId, NotificationKind.Membership));

        // Queued rather than portal-only, so a linked member also hears about it.
        Assert.Equal(NotificationDeliveryState.Pending, notice.DeliveryState);

        // Members are created with the portal's default culture, which is Persian — so the
        // text must not be the English fallback or a raw resource key.
        Assert.DoesNotContain("notice.", notice.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", notice.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_member_sees_the_notice_on_their_notifications_page()
    {
        var userId = await _factory.CreateMemberAsync(
            "notice-visible", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-60));

        await SweepAsync();

        var notice = Assert.Single(await NoticesAsync(userId, NotificationKind.Membership));

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("notice-visible", PortalTestData.MemberPassword);

        var page = await client.GetStringAsync("/Notifications");

        Assert.Contains(notice.Title, page, StringComparison.Ordinal);
    }
}

internal static class ExpiryNoticeTestQueries
{
    public static Task SetMembershipEndAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        DateTimeOffset endsAt) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            await db.Memberships
                .Where(m => m.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.EndsAt, endsAt));
        });

    public static Task SetSubscriptionQuotaAsync(
        this SentinelWebApplicationFactory factory,
        Guid subscriptionId,
        long total,
        long used) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            await db.SubscriptionSources
                .Where(s => s.Id == subscriptionId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.TotalBytes, total)
                    .SetProperty(s => s.DownloadBytes, used)
                    .SetProperty(s => s.UploadBytes, 0L));
        });

    public static Task<Guid> AddSubscriptionAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        string url,
        string title) =>
        factory.WithScopeAsync(async services =>
        {
            var subscriptions = services
                .GetRequiredService<Sentinel.Application.Subscriptions.ISubscriptionService>();

            var result = await subscriptions.AddAsync(
                userId,
                new Sentinel.Application.Subscriptions.SaveSubscriptionRequest(
                    title, url, true, null, null));

            Assert.True(result.Succeeded, result.ErrorKey);
            return result.Value;
        });
}
