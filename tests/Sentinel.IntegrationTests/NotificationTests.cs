using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class NotificationTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public NotificationTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    private async Task<HttpClient> AdminClientAsync(string userName)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, RoleNames.Admin);
        return await SignedInAsync(userName);
    }

    // ------------------------------------------------------------------------- the page ----

    [Fact]
    public async Task The_notifications_page_renders_for_a_member()
    {
        await _factory.CreateMemberAsync("notif-render");

        using var client = await SignedInAsync("notif-render");
        var response = await client.GetAsync("/Notifications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_notifications_page_is_closed_to_anonymous_visitors()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Notifications");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task A_member_sees_only_their_own_notifications()
    {
        var mine = await _factory.CreateMemberAsync("notif-mine");
        var theirs = await _factory.CreateMemberAsync("notif-theirs");

        await _factory.CreateNotificationAsync(mine, "Visible to me", "body");
        await _factory.CreateNotificationAsync(theirs, "Belongs to somebody else", "body");

        using var client = await SignedInAsync("notif-mine");
        var page = await client.GetStringAsync("/Notifications");

        Assert.Contains("Visible to me", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Belongs to somebody else", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------- IDOR ----

    [Fact]
    public async Task A_member_cannot_mark_another_members_notification_as_read()
    {
        var victim = await _factory.CreateMemberAsync("notif-victim");
        await _factory.CreateMemberAsync("notif-attacker");

        var notificationId = await _factory.CreateNotificationAsync(victim, "Private", "body");

        using var attacker = await SignedInAsync("notif-attacker");
        var token = await attacker.GetAntiForgeryTokenAsync("/Notifications");

        var response = await attacker.PostAsync("/Notifications/MarkRead", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = notificationId.ToString(),
            }));

        // The redirect is identical to the success path on purpose: a different response would
        // confirm that the guessed id belongs to a real notification.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var notification = await _factory.FindNotificationAsync(notificationId);
        Assert.Null(notification!.ReadAt);
    }

    [Fact]
    public async Task A_member_can_mark_their_own_notification_read()
    {
        var userId = await _factory.CreateMemberAsync("notif-markread");
        var notificationId = await _factory.CreateNotificationAsync(userId, "Mine", "body");

        using var client = await SignedInAsync("notif-markread");
        var token = await client.GetAntiForgeryTokenAsync("/Notifications");

        await client.PostAsync("/Notifications/MarkRead", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = notificationId.ToString(),
            }));

        var notification = await _factory.FindNotificationAsync(notificationId);
        Assert.NotNull(notification!.ReadAt);
    }

    // ------------------------------------------------------------------ admin messaging ----

    [Fact]
    public async Task An_administrator_can_message_one_member()
    {
        using var admin = await AdminClientAsync("notif-admin-send");
        var recipient = await _factory.CreateMemberAsync("notif-recipient");

        var token = await admin.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{recipient}");

        var response = await admin.PostAsync("/Admin/Messages/SendToUser", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = recipient.ToString(),
                ["Title"] = "Scheduled maintenance",
                ["Body"] = "The portal will be unavailable briefly tonight.",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var member = await SignedInAsync("notif-recipient");
        var page = await member.GetStringAsync("/Notifications");

        Assert.Contains("Scheduled maintenance", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_member_cannot_send_messages()
    {
        await _factory.CreateMemberAsync("notif-not-admin");
        var target = await _factory.CreateMemberAsync("notif-not-admin-target");

        using var client = await SignedInAsync("notif-not-admin");
        var token = await client.GetAntiForgeryTokenAsync("/Notifications");

        var response = await client.PostAsync("/Admin/Messages/SendToUser", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = target.ToString(),
                ["Title"] = "Not allowed",
                ["Body"] = "Should never arrive.",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, await _factory.CountNotificationsAsync(target));
    }

    [Fact]
    public async Task A_broadcast_without_the_typed_confirmation_sends_nothing()
    {
        // A broadcast reaches every active member and cannot be recalled, so it gets a
        // deliberate friction step rather than a dialog a reflex click dismisses.
        using var admin = await AdminClientAsync("notif-broadcast-guard");
        var before = await _factory.CountAllNotificationsAsync();

        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Messages/Broadcast");

        var response = await admin.PostAsync("/Admin/Messages/Broadcast", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Title"] = "Unconfirmed",
                ["Body"] = "Should not go out.",
                ["Confirmation"] = "yes please",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await _factory.CountAllNotificationsAsync());
    }

    [Fact]
    public async Task A_confirmed_broadcast_reaches_active_members()
    {
        using var admin = await AdminClientAsync("notif-broadcast-admin");
        await _factory.CreateMemberAsync("notif-broadcast-target");

        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Messages/Broadcast");

        var response = await admin.PostAsync("/Admin/Messages/Broadcast", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Title"] = "Service announcement",
                ["Body"] = "Everything is fine.",
                ["Confirmation"] = "SEND",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var member = await SignedInAsync("notif-broadcast-target");
        var page = await member.GetStringAsync("/Notifications");

        Assert.Contains("Service announcement", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broadcast_skips_disabled_accounts()
    {
        var disabled = await _factory.CreateMemberAsync("notif-broadcast-disabled");
        await _factory.SetAccountStatusAsync(disabled, UserAccountStatus.Disabled);

        using var admin = await AdminClientAsync("notif-broadcast-skip-admin");
        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Messages/Broadcast");

        await admin.PostAsync("/Admin/Messages/Broadcast", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Title"] = "Only for the active",
                ["Body"] = "Body.",
                ["Confirmation"] = "SEND",
            }));

        // Somebody who cannot sign in cannot act on an announcement either.
        Assert.Equal(0, await _factory.CountNotificationsAsync(disabled));
    }

    [Fact]
    public async Task An_external_link_on_a_broadcast_is_stripped_rather_than_stored()
    {
        using var admin = await AdminClientAsync("notif-link-admin");
        await _factory.CreateMemberAsync("notif-link-target");

        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Messages/Broadcast");

        await admin.PostAsync("/Admin/Messages/Broadcast", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Title"] = "Link test",
                ["Body"] = "Body.",
                ["LinkPath"] = "https://evil.example/phish",
                ["Confirmation"] = "SEND",
            }));

        var stored = await _factory.FindNotificationByTitleAsync("Link test");

        Assert.NotNull(stored);
        Assert.Null(stored!.LinkPath);
    }

    // --------------------------------------------------------------- event notifications ----

    [Fact]
    public async Task Granting_access_notifies_the_member()
    {
        using var admin = await AdminClientAsync("notif-grant-admin");

        var memberId = await _factory.CreateMemberAsync("notif-grant-member");
        var appId = await _factory.CreateApplicationAsync("notif-grant-app");

        var token = await admin.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{memberId}");

        await admin.PostAsync("/Admin/Users/GrantEntitlement", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = memberId.ToString(),
                ["ProductId"] = appId.ToString(),
            }));

        var notifications = await _factory.ListNotificationsAsync(memberId);

        Assert.Contains(notifications, n => n.Kind == NotificationKind.Entitlement);
    }
}

internal static class NotificationTestQueries
{
    public static Task<Guid> CreateNotificationAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId,
        string title,
        string body) =>
        factory.WithScopeAsync(async services =>
        {
            var notifications = services.GetRequiredService<INotificationService>();
            var db = services.GetRequiredService<ISentinelDbContext>();

            await notifications.CreateAsync(
                userId,
                new NewNotification(NotificationKind.System, title, body, DeliverToTelegram: false));

            await db.SaveChangesAsync();

            return await db.Notifications
                .Where(n => n.UserId == userId && n.Title == title)
                .Select(n => n.Id)
                .FirstAsync();
        });

    public static Task<Notification?> FindNotificationAsync(
        this SentinelWebApplicationFactory factory,
        Guid id) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        });

    public static Task<Notification?> FindNotificationByTitleAsync(
        this SentinelWebApplicationFactory factory,
        string title) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Title == title);
        });

    public static Task<List<Notification>> ListNotificationsAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).ToListAsync();
        });

    public static Task<int> CountNotificationsAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Notifications.CountAsync(n => n.UserId == userId);
        });

    public static Task<int> CountAllNotificationsAsync(this SentinelWebApplicationFactory factory) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Notifications.CountAsync();
        });
}
