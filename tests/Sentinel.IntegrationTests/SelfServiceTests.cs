using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Profile, security and activity — everything a member may change about their own account.
/// The recurring question in these tests is whether a self-service endpoint can be talked into
/// touching somebody else.
/// </summary>
public sealed class SelfServiceTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public SelfServiceTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName, string? password = null)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, password ?? PortalTestData.MemberPassword);
        return client;
    }

    [Theory]
    [InlineData("/Profile")]
    [InlineData("/Security")]
    [InlineData("/Activity")]
    public async Task Self_service_pages_render_for_a_member(string path)
    {
        await _factory.CreateMemberAsync($"self-render-{path.Trim('/')}");

        using var client = await SignedInAsync($"self-render-{path.Trim('/')}");
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Profile")]
    [InlineData("/Security")]
    [InlineData("/Activity")]
    public async Task Self_service_pages_are_closed_to_anonymous_visitors(string path)
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------- profile ----

    [Fact]
    public async Task A_member_can_change_their_own_display_name_and_time_zone()
    {
        var userId = await _factory.CreateMemberAsync("self-profile-edit");

        using var client = await SignedInAsync("self-profile-edit");
        var response = await PostProfileAsync(client, "New Display Name", timeZone: "UTC");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var user = await _factory.FindUserAsync(userId);
        Assert.Equal("New Display Name", user!.DisplayName);
        Assert.Equal("UTC", user.TimeZoneId);
    }

    [Fact]
    public async Task A_phone_number_is_stored_in_one_canonical_form()
    {
        // Typed as a national number with a trunk zero; stored as E.164 so the unique index
        // and the sign-in lookup both work regardless of how it was typed.
        var userId = await _factory.CreateMemberAsync("self-profile-phone");

        using var client = await SignedInAsync("self-profile-phone");
        await PostProfileAsync(client, "Phone Owner", phone: "0912 111 2233");

        var user = await _factory.FindUserAsync(userId);
        Assert.Equal("+989121112233", user!.NormalizedPhoneNumber);
    }

    [Fact]
    public async Task A_phone_number_already_held_by_another_account_is_refused()
    {
        await _factory.CreateMemberAsync("self-phone-first");
        var secondId = await _factory.CreateMemberAsync("self-phone-second");

        using var first = await SignedInAsync("self-phone-first");
        await PostProfileAsync(first, "First", phone: "09125550001");

        using var second = await SignedInAsync("self-phone-second");
        var response = await PostProfileAsync(second, "Second", phone: "+989125550001");

        // Refused with the form redisplayed rather than a unique-index violation surfacing
        // as a 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await _factory.FindUserAsync(secondId);
        Assert.Null(user!.NormalizedPhoneNumber);
    }

    [Fact]
    public async Task An_unrecognised_time_zone_is_refused()
    {
        var userId = await _factory.CreateMemberAsync("self-profile-badzone");

        using var client = await SignedInAsync("self-profile-badzone");
        var response = await PostProfileAsync(client, "Zone Tester", timeZone: "Mars/Olympus_Mons");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await _factory.FindUserAsync(userId);
        Assert.NotEqual("Mars/Olympus_Mons", user!.TimeZoneId);
    }

    [Fact]
    public async Task The_profile_form_cannot_be_used_to_change_account_status_or_roles()
    {
        // Over-posting: the extra fields have no matching property on the view model, so they
        // are simply ignored. This is the regression guard for that staying true.
        var userId = await _factory.CreateMemberAsync("self-overpost");

        using var client = await SignedInAsync("self-overpost");
        var token = await client.GetAntiForgeryTokenAsync("/Profile");

        var response = await client.PostAsync("/Profile", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["DisplayName"] = "Over Poster",
                ["PreferredCulture"] = "fa",
                ["TimeZoneId"] = "Asia/Tehran",
                ["Status"] = "Disabled",
                ["Roles"] = "SuperAdmin",
                ["IsEnabled"] = "false",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var user = await _factory.FindUserAsync(userId);
        Assert.Equal(Sentinel.Domain.Identity.UserAccountStatus.Active, user!.Status);
    }

    // ------------------------------------------------------------------------ password ----

    [Fact]
    public async Task A_member_can_change_their_password_and_sign_in_with_the_new_one()
    {
        const string newPassword = "Self-Service-Changed-13579";

        await _factory.CreateMemberAsync("self-password-ok");

        using var client = await SignedInAsync("self-password-ok");
        var response = await PostPasswordAsync(client, PortalTestData.MemberPassword, newPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var fresh = _factory.CreateNonRedirectingClient();
        await fresh.SignInAsync("self-password-ok", newPassword);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused()
    {
        // Requiring the current password is what stops a borrowed, unlocked browser from
        // becoming a permanent account takeover.
        await _factory.CreateMemberAsync("self-password-wrong");

        using var client = await SignedInAsync("self-password-wrong");
        var response = await PostPasswordAsync(client, "Not-The-Password-999", "Another-Password-24680");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The original password still works.
        using var fresh = _factory.CreateNonRedirectingClient();
        await fresh.SignInAsync("self-password-wrong", PortalTestData.MemberPassword);
    }

    [Fact]
    public async Task A_password_that_fails_the_policy_is_refused()
    {
        await _factory.CreateMemberAsync("self-password-weak");

        using var client = await SignedInAsync("self-password-weak");
        var response = await PostPasswordAsync(client, PortalTestData.MemberPassword, "short");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var fresh = _factory.CreateNonRedirectingClient();
        await fresh.SignInAsync("self-password-weak", PortalTestData.MemberPassword);
    }

    [Fact]
    public async Task Changing_the_password_ends_other_sessions_but_keeps_this_one()
    {
        const string newPassword = "Sessions-After-Change-97531";

        await _factory.CreateMemberAsync("self-password-sessions");

        using var otherDevice = await SignedInAsync("self-password-sessions");
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/Dashboard")).StatusCode);

        using var thisDevice = await SignedInAsync("self-password-sessions");
        var response = await PostPasswordAsync(thisDevice, PortalTestData.MemberPassword, newPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // The browser that made the change stays signed in — sending it back to the login page
        // teaches nothing and only encourages weaker passwords next time.
        Assert.Equal(HttpStatusCode.OK, (await thisDevice.GetAsync("/Dashboard")).StatusCode);

        // Every other device is out.
        Assert.Equal(HttpStatusCode.Redirect, (await otherDevice.GetAsync("/Dashboard")).StatusCode);
    }

    // ------------------------------------------------------------------------ sessions ----

    [Fact]
    public async Task A_member_cannot_revoke_a_session_belonging_to_someone_else()
    {
        // The id comes from the form, so ownership is verified rather than assumed.
        await _factory.CreateMemberAsync("self-session-victim");
        await _factory.CreateMemberAsync("self-session-attacker");

        using var victim = await SignedInAsync("self-session-victim");
        var victimSessionId = await _factory.LatestSessionIdAsync("self-session-victim");

        using var attacker = await SignedInAsync("self-session-attacker");
        var token = await attacker.GetAntiForgeryTokenAsync("/Security");

        var response = await attacker.PostAsync("/Security/RevokeSession", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["sessionId"] = victimSessionId.ToString(),
            }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The victim is still signed in.
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/Dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_member_can_end_one_of_their_own_other_sessions()
    {
        await _factory.CreateMemberAsync("self-session-owner");

        using var otherDevice = await SignedInAsync("self-session-owner");
        var otherSessionId = await _factory.LatestSessionIdAsync("self-session-owner");

        using var thisDevice = await SignedInAsync("self-session-owner");
        var token = await thisDevice.GetAntiForgeryTokenAsync("/Security");

        var response = await thisDevice.PostAsync("/Security/RevokeSession", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["sessionId"] = otherSessionId.ToString(),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await otherDevice.GetAsync("/Dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await thisDevice.GetAsync("/Dashboard")).StatusCode);
    }

    // ------------------------------------------------------------------------ activity ----

    [Fact]
    public async Task The_activity_page_shows_only_the_signed_in_members_history()
    {
        await _factory.CreateMemberAsync("self-activity-mine");
        await _factory.CreateMemberAsync("self-activity-theirs");

        // Produce a distinctive failed attempt for the other account.
        using var noise = _factory.CreateNonRedirectingClient();
        var noiseToken = await noise.GetAntiForgeryTokenAsync("/Account/Login");
        await noise.PostLoginAsync(noiseToken, "self-activity-theirs", "Wrong-Password-8642");

        using var client = await SignedInAsync("self-activity-mine");
        var page = await client.GetStringAsync("/Activity");

        Assert.DoesNotContain("self-activity-theirs", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- helpers ----

    private static async Task<HttpResponseMessage> PostProfileAsync(
        HttpClient client,
        string displayName,
        string? phone = null,
        string timeZone = "Asia/Tehran",
        string culture = "fa")
    {
        var token = await client.GetAntiForgeryTokenAsync("/Profile");

        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["DisplayName"] = displayName,
            ["PreferredCulture"] = culture,
            ["TimeZoneId"] = timeZone,
        };

        if (phone is not null)
        {
            fields["PhoneNumber"] = phone;
        }

        return await client.PostAsync("/Profile", new FormUrlEncodedContent(fields));
    }

    private static async Task<HttpResponseMessage> PostPasswordAsync(
        HttpClient client,
        string current,
        string replacement)
    {
        var token = await client.GetAntiForgeryTokenAsync("/Security");

        return await client.PostAsync("/Security/ChangePassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["CurrentPassword"] = current,
                ["NewPassword"] = replacement,
                ["ConfirmPassword"] = replacement,
            }));
    }
}

internal static class SelfServiceTestQueries
{
    public static Task<Sentinel.Domain.Identity.ApplicationUser?> FindUserAsync(
        this SentinelWebApplicationFactory factory,
        Guid userId) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        });

    public static Task<Guid> LatestSessionIdAsync(
        this SentinelWebApplicationFactory factory,
        string userName) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();

            return await db.UserSessions
                .AsNoTracking()
                .Where(s => s.User!.UserName == userName && s.RevokedAt == null)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.Id)
                .FirstAsync();
        });
}
