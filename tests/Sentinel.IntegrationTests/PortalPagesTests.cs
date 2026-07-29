using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sentinel.Domain.Memberships;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed partial class PortalPagesTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public PortalPagesTests(SentinelWebApplicationFactory factory) => _factory = factory;

    [GeneratedRegex("data-apps=\"([^\"]*)\"")]
    private static partial Regex AppsPayloadRegex();

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    [Theory]
    [InlineData("/Apps")]
    [InlineData("/Membership")]
    [InlineData("/Dashboard")]
    public async Task Portal_pages_render_for_a_member(string path)
    {
        await _factory.CreateMemberAsync($"pages-render-{path.Trim('/')}");

        using var client = await SignedInAsync($"pages-render-{path.Trim('/')}");
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Apps")]
    [InlineData("/Membership")]
    public async Task Portal_pages_are_closed_to_anonymous_visitors(string path)
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_member_without_a_membership_sees_the_empty_state_rather_than_an_error()
    {
        await _factory.CreateMemberAsync("pages-nomembership", withMembership: false);

        using var client = await SignedInAsync("pages-nomembership");
        var response = await client.GetAsync("/Membership");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("empty-state", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_membership_page_shows_the_tier_and_the_countdown()
    {
        await _factory.CreateMemberAsync(
            "pages-tier",
            tier: MembershipTier.Elite,
            membershipEndsAt: DateTimeOffset.UtcNow.AddDays(45));

        using var client = await SignedInAsync("pages-tier");

        // Ask for English explicitly: the portal's default culture is Persian, so asserting on
        // an English label without this would be asserting that localisation is broken.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Membership");
        request.Headers.Add("Accept-Language", "en-US");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Elite", body, StringComparison.Ordinal);
        Assert.Contains("Days remaining", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_membership_page_is_rendered_in_persian_by_default()
    {
        await _factory.CreateMemberAsync("pages-persian", tier: MembershipTier.Elite);

        using var client = await SignedInAsync("pages-persian");
        var body = await client.GetStringAsync("/Membership");

        Assert.Contains("ویژه", body, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_membership_close_to_expiry_raises_the_renewal_warning()
    {
        await _factory.CreateMemberAsync(
            "pages-renewal", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(2));

        using var client = await SignedInAsync("pages-renewal");
        var body = await client.GetStringAsync("/Dashboard");

        Assert.Contains("alert--warning", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healthy_membership_raises_no_warning()
    {
        await _factory.CreateMemberAsync(
            "pages-healthy", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(200));

        using var client = await SignedInAsync("pages-healthy");
        var body = await client.GetStringAsync("/Dashboard");

        Assert.DoesNotContain("alert--warning", body, StringComparison.Ordinal);
        Assert.DoesNotContain("alert--danger", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_membership_inside_its_grace_period_can_still_launch_applications()
    {
        // The configured grace period is three days; this membership ended yesterday.
        await _factory.CreateMemberAsync(
            "pages-grace", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-1));

        await _factory.CreateApplicationAsync("pages-grace-app");

        using var client = await SignedInAsync("pages-grace");
        var response = await client.GetAsync("/apps/pages-grace-app/open");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://apps.example.com/target", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_membership_past_its_grace_period_can_no_longer_launch()
    {
        await _factory.CreateMemberAsync(
            "pages-postgrace", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-10));

        await _factory.CreateApplicationAsync("pages-postgrace-app");

        using var client = await SignedInAsync("pages-postgrace");
        var response = await client.GetAsync("/apps/pages-postgrace-app/open");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_island_payload_is_valid_json_and_carries_no_destination_url()
    {
        await _factory.CreateMemberAsync("pages-payload");
        await _factory.CreateApplicationAsync("pages-payload-open");
        await _factory.CreateApplicationAsync("pages-payload-locked", requiresExplicitEntitlement: true);

        using var client = await SignedInAsync("pages-payload");
        var html = await client.GetStringAsync("/Apps");

        var match = AppsPayloadRegex().Match(html);
        Assert.True(match.Success, "The applications island payload was not found in the page.");

        // Razor encodes the attribute; the browser's dataset API decodes it. Do the same here.
        var json = WebUtility.HtmlDecode(match.Groups[1].Value);

        using var document = JsonDocument.Parse(json);
        var cards = document.RootElement.EnumerateArray().ToList();

        Assert.NotEmpty(cards);
        Assert.DoesNotContain("apps.example.com", json, StringComparison.OrdinalIgnoreCase);

        var locked = cards.Single(c => c.GetProperty("key").GetString() == "pages-payload-locked");
        Assert.False(locked.GetProperty("canLaunch").GetBoolean());

        // A locked card carries no link at all.
        Assert.Equal(JsonValueKind.Null, locked.GetProperty("openUrl").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(locked.GetProperty("reason").GetString()));

        var open = cards.Single(c => c.GetProperty("key").GetString() == "pages-payload-open");
        Assert.True(open.GetProperty("canLaunch").GetBoolean());
        Assert.Equal("/apps/pages-payload-open/open", open.GetProperty("openUrl").GetString());
    }

    [Fact]
    public async Task The_applications_page_loads_its_island_bundle()
    {
        await _factory.CreateMemberAsync("pages-bundle");

        // Created here rather than relied upon from a sibling test: xUnit gives no ordering
        // guarantee, and a test that only passes when it runs second is not a test.
        await _factory.CreateApplicationAsync("pages-bundle-app");

        using var client = await SignedInAsync("pages-bundle");
        var html = await client.GetStringAsync("/Apps");

        Assert.Contains("/js/dist/page-apps.js", html, StringComparison.Ordinal);

        // The fallback must be present for visitors without scripting.
        Assert.Contains("<noscript>", html, StringComparison.OrdinalIgnoreCase);
    }
}
