using System.Net;
using Sentinel.Domain.Products;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// End-to-end checks on the launch endpoint. Nothing here reaches into the rule evaluator:
/// each test drives the real HTTP surface, because what matters is that the running
/// application refuses, not that a function returns false.
/// </summary>
public sealed class ApplicationAccessTests : IClassFixture<SentinelWebApplicationFactory>
{
    private const string TargetUrl = "https://apps.example.com/target";

    private readonly SentinelWebApplicationFactory _factory;

    public ApplicationAccessTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    // ------------------------------------------------------------------- the happy path ----

    [Fact]
    public async Task An_active_member_is_redirected_to_the_application()
    {
        await _factory.CreateMemberAsync("launch-ok");
        await _factory.CreateApplicationAsync("launch-ok-app");

        using var client = await SignedInAsync("launch-ok");
        var response = await client.GetAsync("/apps/launch-ok-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(TargetUrl, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_grant_opens_an_application_that_requires_one()
    {
        var userId = await _factory.CreateMemberAsync("launch-granted");
        var appId = await _factory.CreateApplicationAsync(
            "launch-granted-app", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, appId);

        using var client = await SignedInAsync("launch-granted");
        var response = await client.GetAsync("/apps/launch-granted-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(TargetUrl, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_grant_keeps_an_application_open_after_the_membership_expires()
    {
        var userId = await _factory.CreateMemberAsync(
            "launch-grant-outlives",
            membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-90));

        var appId = await _factory.CreateApplicationAsync("launch-grant-outlives-app");
        await _factory.GrantAsync(userId, appId);

        using var client = await SignedInAsync("launch-grant-outlives");
        var response = await client.GetAsync("/apps/launch-grant-outlives-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    // ------------------------------------------------------------------------ refusals ----

    [Fact]
    public async Task An_expired_membership_is_refused_and_the_destination_is_not_disclosed()
    {
        await _factory.CreateMemberAsync(
            "launch-expired", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-90));

        await _factory.CreateApplicationAsync("launch-expired-app");

        using var client = await SignedInAsync("launch-expired");
        var response = await client.GetAsync("/apps/launch-expired-app/open");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The refusal page must not carry the address a successful launch would have used.
        Assert.DoesNotContain(TargetUrl, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apps.example.com", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_application_requiring_a_grant_is_refused_without_one()
    {
        await _factory.CreateMemberAsync("launch-nogrant");
        await _factory.CreateApplicationAsync("launch-nogrant-app", requiresExplicitEntitlement: true);

        using var client = await SignedInAsync("launch-nogrant");
        var response = await client.GetAsync("/apps/launch-nogrant-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_grant_closes_an_application_that_requires_one()
    {
        var userId = await _factory.CreateMemberAsync("launch-revoked");
        var appId = await _factory.CreateApplicationAsync(
            "launch-revoked-app", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, appId, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = await SignedInAsync("launch-revoked");
        var response = await client.GetAsync("/apps/launch-revoked-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_tier_below_the_minimum_is_refused()
    {
        await _factory.CreateMemberAsync("launch-lowtier", tier: MembershipTier.Basic);
        await _factory.CreateApplicationAsync("launch-lowtier-app", minimumTier: MembershipTier.Elite);

        using var client = await SignedInAsync("launch-lowtier");
        var response = await client.GetAsync("/apps/launch-lowtier-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(ProductReleaseStatus.Draft)]
    [InlineData(ProductReleaseStatus.ComingSoon)]
    [InlineData(ProductReleaseStatus.Deprecated)]
    public async Task An_application_that_is_not_published_cannot_be_launched(
        ProductReleaseStatus status)
    {
        var key = $"launch-status-{status}".ToLowerInvariant();

        await _factory.CreateMemberAsync($"launch-status-{status}");
        await _factory.CreateApplicationAsync(key, releaseStatus: status);

        using var client = await SignedInAsync($"launch-status-{status}");
        var response = await client.GetAsync($"/apps/{key}/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_application_is_closed_even_to_a_holder_of_a_grant()
    {
        var userId = await _factory.CreateMemberAsync("launch-disabled-app");
        var appId = await _factory.CreateApplicationAsync("launch-disabled-app-key", isEnabled: false);
        await _factory.GrantAsync(userId, appId);

        using var client = await SignedInAsync("launch-disabled-app");
        var response = await client.GetAsync("/apps/launch-disabled-app-key/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_application_key_is_a_404()
    {
        await _factory.CreateMemberAsync("launch-unknown");

        using var client = await SignedInAsync("launch-unknown");
        var response = await client.GetAsync("/apps/no-such-application/open");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_page_rather_than_the_application()
    {
        await _factory.CreateApplicationAsync("launch-anonymous-app");

        using var client = _factory.CreateNonRedirectingClient();
        var response = await client.GetAsync("/apps/launch-anonymous-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------- IDOR ----

    [Fact]
    public async Task One_members_grant_does_not_open_the_application_for_another()
    {
        // The classic IDOR shape: the grant exists, just not for the caller. Access is keyed
        // off the authenticated principal, never off anything in the request.
        var granted = await _factory.CreateMemberAsync("idor-granted");
        await _factory.CreateMemberAsync("idor-outsider");

        var appId = await _factory.CreateApplicationAsync(
            "idor-app", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(granted, appId);

        using var outsider = await SignedInAsync("idor-outsider");
        var response = await outsider.GetAsync("/apps/idor-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var holder = await SignedInAsync("idor-granted");
        var allowed = await holder.GetAsync("/apps/idor-app/open");

        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
    }

    [Fact]
    public async Task The_catalogue_shows_each_member_only_their_own_access()
    {
        var granted = await _factory.CreateMemberAsync("catalog-granted");
        await _factory.CreateMemberAsync("catalog-outsider");

        var appId = await _factory.CreateApplicationAsync(
            "catalog-restricted", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(granted, appId);

        using var holderClient = await SignedInAsync("catalog-granted");
        var holderPage = await holderClient.GetStringAsync("/Apps");

        using var outsiderClient = await SignedInAsync("catalog-outsider");
        var outsiderPage = await outsiderClient.GetStringAsync("/Apps");

        // Both see the application listed, but only the holder gets a launch link for it.
        Assert.Contains("catalog-restricted", holderPage, StringComparison.Ordinal);
        Assert.Contains("/apps/catalog-restricted/open", holderPage, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/catalog-restricted/open", outsiderPage, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- account state ----

    [Fact]
    public async Task A_suspended_account_cannot_launch_anything()
    {
        // The account is suspended after sign-in, so an already-issued cookie is in play —
        // exactly the case where a check made only at login time would fail to catch up.
        var userId = await _factory.CreateMemberAsync("launch-suspend-later");
        await _factory.CreateApplicationAsync("launch-suspend-later-app");

        using var client = await SignedInAsync("launch-suspend-later");

        var before = await client.GetAsync("/apps/launch-suspend-later-app/open");
        Assert.Equal(HttpStatusCode.Redirect, before.StatusCode);
        Assert.Equal(TargetUrl, before.Headers.Location?.ToString());

        await _factory.SetAccountStatusAsync(userId, UserAccountStatus.Suspended);

        var after = await client.GetAsync("/apps/launch-suspend-later-app/open");

        // The cookie is rejected outright and the member is bounced to the login page.
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains(
            "/Account/Login",
            after.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------- catalogue ----

    [Fact]
    public async Task Draft_applications_are_never_listed()
    {
        await _factory.CreateMemberAsync("catalog-draft-viewer");
        await _factory.CreateApplicationAsync(
            "catalog-draft-app", releaseStatus: ProductReleaseStatus.Draft);

        using var client = await SignedInAsync("catalog-draft-viewer");
        var page = await client.GetStringAsync("/Apps");

        Assert.DoesNotContain("catalog-draft-app", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_never_ships_the_destination_url_to_the_browser()
    {
        // The card links to the portal's launch endpoint; the real address stays server-side,
        // which is what stops a locked card from being bypassed with the browser's inspector.
        await _factory.CreateMemberAsync("catalog-url-check");
        await _factory.CreateApplicationAsync("catalog-url-app");

        using var client = await SignedInAsync("catalog-url-check");
        var page = await client.GetStringAsync("/Apps");

        Assert.Contains("/apps/catalog-url-app/open", page, StringComparison.Ordinal);
        Assert.DoesNotContain("apps.example.com", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_coming_soon_application_is_listed_but_not_launchable()
    {
        await _factory.CreateMemberAsync("catalog-soon-viewer");
        await _factory.CreateApplicationAsync(
            "catalog-soon-app", releaseStatus: ProductReleaseStatus.ComingSoon);

        using var client = await SignedInAsync("catalog-soon-viewer");
        var page = await client.GetStringAsync("/Apps");

        Assert.Contains("catalog-soon-app", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/catalog-soon-app/open", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ audit ----

    [Fact]
    public async Task Both_a_successful_and_a_refused_launch_are_audited()
    {
        var userId = await _factory.CreateMemberAsync("audit-launch");
        var openAppId = await _factory.CreateApplicationAsync("audit-open-app");
        var closedAppId = await _factory.CreateApplicationAsync(
            "audit-closed-app", requiresExplicitEntitlement: true);

        using var client = await SignedInAsync("audit-launch");
        await client.GetAsync("/apps/audit-open-app/open");
        await client.GetAsync("/apps/audit-closed-app/open");

        var allowedActions = await _factory.RecentAuditActionsAsync(openAppId.ToString());
        var deniedActions = await _factory.RecentAuditActionsAsync(closedAppId.ToString());

        Assert.Contains("application.launched", allowedActions);
        Assert.Contains("application.launch.denied", deniedActions);
        Assert.DoesNotContain("application.launched", deniedActions);

        Assert.NotEqual(Guid.Empty, userId);
    }
}
