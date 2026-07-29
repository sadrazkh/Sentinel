using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Subscriptions;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class SubscriptionPageTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public SubscriptionPageTests(SentinelWebApplicationFactory factory) => _factory = factory;

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

    private static async Task<HttpResponseMessage> AddAsync(HttpClient client, string url, string title)
    {
        var token = await client.GetAntiForgeryTokenAsync("/Configs");

        return await client.PostAsync("/Configs/Add", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Title"] = title,
                ["Url"] = url,
            }));
    }

    // ------------------------------------------------------------------------- the page ----

    [Fact]
    public async Task The_configs_page_renders_for_a_member()
    {
        await _factory.CreateMemberAsync("sub-render");

        using var client = await SignedInAsync("sub-render");
        var response = await client.GetAsync("/Configs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_configs_page_is_closed_to_anonymous_visitors()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Configs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    // ---------------------------------------------------------------------- adding a link ----

    [Fact]
    public async Task A_member_can_add_a_subscription()
    {
        var userId = await _factory.CreateMemberAsync("sub-add");

        using var client = await SignedInAsync("sub-add");
        var response = await AddAsync(client, "https://sub.example.info/api/one", "Main");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var sources = await _factory.ListSubscriptionsAsync(userId);
        Assert.Single(sources);
        Assert.Equal("Main", sources[0].Title);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://localhost/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("https://user:secret@sub.example.info/x")]
    public async Task An_ssrf_target_is_refused_and_never_stored(string url)
    {
        // The portal must not be turned into a proxy for its own private network — and the
        // rejection has to happen before the row exists, not after a fetch is attempted.
        var userId = await _factory.CreateMemberAsync("sub-ssrf");

        using var client = await SignedInAsync("sub-ssrf");
        await AddAsync(client, url, "Malicious");

        var sources = await _factory.ListSubscriptionsAsync(userId);
        Assert.DoesNotContain(sources, s => s.Title == "Malicious");
    }

    [Fact]
    public async Task The_same_link_cannot_be_added_twice()
    {
        var userId = await _factory.CreateMemberAsync("sub-duplicate");

        using var client = await SignedInAsync("sub-duplicate");
        await AddAsync(client, "https://sub.example.info/api/dup", "First");
        await AddAsync(client, "https://sub.example.info/api/dup", "Second");

        Assert.Single(await _factory.ListSubscriptionsAsync(userId));
    }

    // ---------------------------------------------------------------------------- IDOR ----

    [Fact]
    public async Task A_member_cannot_see_another_members_subscriptions()
    {
        var owner = await _factory.CreateMemberAsync("sub-owner");
        await _factory.CreateMemberAsync("sub-outsider");

        using var ownerClient = await SignedInAsync("sub-owner");
        await AddAsync(ownerClient, "https://sub.example.info/api/private", "Owner only");

        using var outsider = await SignedInAsync("sub-outsider");
        var page = await outsider.GetStringAsync("/Configs");

        Assert.DoesNotContain("Owner only", page, StringComparison.Ordinal);
        Assert.NotEmpty(await _factory.ListSubscriptionsAsync(owner));
    }

    [Fact]
    public async Task A_member_cannot_delete_another_members_subscription()
    {
        var owner = await _factory.CreateMemberAsync("sub-del-owner");
        await _factory.CreateMemberAsync("sub-del-attacker");

        using var ownerClient = await SignedInAsync("sub-del-owner");
        await AddAsync(ownerClient, "https://sub.example.info/api/victim", "Victim");

        var target = (await _factory.ListSubscriptionsAsync(owner)).Single();

        using var attacker = await SignedInAsync("sub-del-attacker");
        var token = await attacker.GetAntiForgeryTokenAsync("/Configs");

        await attacker.PostAsync("/Configs/Remove", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = target.Id.ToString(),
            }));

        // Still there.
        Assert.Single(await _factory.ListSubscriptionsAsync(owner));
    }

    [Fact]
    public async Task A_member_can_delete_their_own_subscription()
    {
        var userId = await _factory.CreateMemberAsync("sub-del-own");

        using var client = await SignedInAsync("sub-del-own");
        await AddAsync(client, "https://sub.example.info/api/mine", "Mine");

        var target = (await _factory.ListSubscriptionsAsync(userId)).Single();

        var token = await client.GetAntiForgeryTokenAsync("/Configs");
        await client.PostAsync("/Configs/Remove", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = target.Id.ToString(),
            }));

        Assert.Empty(await _factory.ListSubscriptionsAsync(userId));
    }

    [Fact]
    public async Task The_subscription_url_is_never_rendered_on_the_page()
    {
        // The link is the credential that retrieves the configs; the page has no reason to
        // show it back, and doing so would put it in browser history and screenshots.
        const string url = "https://sub.example.info/api/secret-token-value";

        await _factory.CreateMemberAsync("sub-url-hidden");

        using var client = await SignedInAsync("sub-url-hidden");
        await AddAsync(client, url, "Hidden");

        var page = await client.GetStringAsync("/Configs");

        Assert.Contains("Hidden", page, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", page, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------- admin ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_subscription_admin()
    {
        await _factory.CreateMemberAsync("sub-admin-member");

        using var client = await SignedInAsync("sub-admin-member");
        var response = await client.GetAsync("/Admin/Subscriptions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_administrator_sees_every_members_subscription()
    {
        await _factory.CreateMemberAsync("sub-listed-member");

        using var member = await SignedInAsync("sub-listed-member");
        await AddAsync(member, "https://sub.example.info/api/listed", "Listed subscription");

        using var admin = await AdminClientAsync("sub-list-admin");
        var page = await admin.GetStringAsync("/Admin/Subscriptions");

        Assert.Contains("Listed subscription", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admin_list_never_renders_a_subscription_url()
    {
        const string url = "https://sub.example.info/api/admin-should-not-see-this";

        await _factory.CreateMemberAsync("sub-admin-privacy");

        using var member = await SignedInAsync("sub-admin-privacy");
        await AddAsync(member, url, "Private link");

        using var admin = await AdminClientAsync("sub-privacy-admin");
        var page = await admin.GetStringAsync("/Admin/Subscriptions");

        Assert.Contains("Private link", page, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-should-not-see-this", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_subscription_is_flagged_and_can_be_purged()
    {
        var userId = await _factory.CreateMemberAsync("sub-expired");

        using var member = await SignedInAsync("sub-expired");
        await AddAsync(member, "https://sub.example.info/api/expired", "Expired one");

        var source = (await _factory.ListSubscriptionsAsync(userId)).Single();
        await _factory.SetSubscriptionExpiryAsync(source.Id, DateTimeOffset.UtcNow.AddDays(-5));

        using var admin = await AdminClientAsync("sub-purge-admin");

        var deadPage = await admin.GetStringAsync("/Admin/Subscriptions?onlyDead=true");
        Assert.Contains("Expired one", deadPage, StringComparison.Ordinal);

        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Subscriptions?onlyDead=true");
        await admin.PostAsync("/Admin/Subscriptions/PurgeDead", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["confirmation"] = "PURGE",
            }));

        Assert.Empty(await _factory.ListSubscriptionsAsync(userId));
    }

    [Fact]
    public async Task A_purge_without_the_typed_confirmation_deletes_nothing()
    {
        var userId = await _factory.CreateMemberAsync("sub-purge-guard");

        using var member = await SignedInAsync("sub-purge-guard");
        await AddAsync(member, "https://sub.example.info/api/guarded", "Guarded");

        var source = (await _factory.ListSubscriptionsAsync(userId)).Single();
        await _factory.SetSubscriptionExpiryAsync(source.Id, DateTimeOffset.UtcNow.AddDays(-1));

        using var admin = await AdminClientAsync("sub-purge-guard-admin");
        var token = await admin.GetAntiForgeryTokenAsync("/Admin/Subscriptions?onlyDead=true");

        await admin.PostAsync("/Admin/Subscriptions/PurgeDead", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["confirmation"] = "please",
            }));

        Assert.Single(await _factory.ListSubscriptionsAsync(userId));
    }

    [Fact]
    public async Task Support_can_view_but_not_delete()
    {
        await _factory.CreateMemberAsync("sub-support");
        await _factory.AddToRoleAsync("sub-support", RoleNames.Support);

        var userId = await _factory.CreateMemberAsync("sub-support-target");

        using var member = await SignedInAsync("sub-support-target");
        await AddAsync(member, "https://sub.example.info/api/support", "Support view");

        var source = (await _factory.ListSubscriptionsAsync(userId)).Single();

        using var support = await SignedInAsync("sub-support");
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/Admin/Subscriptions")).StatusCode);

        var token = await support.GetAntiForgeryTokenAsync("/Admin/Subscriptions");
        var response = await support.PostAsync("/Admin/Subscriptions/Delete", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["id"] = source.Id.ToString(),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.Single(await _factory.ListSubscriptionsAsync(userId));
    }
}

internal static class SubscriptionTestQueries
{
    public static Task<List<SubscriptionSource>> ListSubscriptionsAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.SubscriptionSources
                .AsNoTracking()
                .Where(s => s.UserId == userId)
                .ToListAsync();
        });

    public static Task SetSubscriptionExpiryAsync(
        this SentinelWebApplicationFactory factory,
        Guid subscriptionId,
        DateTimeOffset expiresAt) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            await db.SubscriptionSources
                .Where(s => s.Id == subscriptionId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAt, expiresAt));
        });
}
