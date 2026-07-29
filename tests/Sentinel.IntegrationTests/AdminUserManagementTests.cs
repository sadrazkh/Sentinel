using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed partial class AdminUserManagementTests : IClassFixture<SentinelWebApplicationFactory>
{
    /// <summary>Obviously synthetic; used only by this suite.</summary>
    private const string NewUserPassword = "Created-By-Admin-Test-5150";

    private readonly SentinelWebApplicationFactory _factory;

    public AdminUserManagementTests(SentinelWebApplicationFactory factory) => _factory = factory;

    [GeneratedRegex("/Admin/Users/Details/([0-9a-fA-F-]{36})")]
    private static partial Regex DetailsLinkRegex();

    /// <summary>Signs in as an administrator created for the calling test.</summary>
    private async Task<HttpClient> AdminClientAsync(string userName)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, RoleNames.Admin);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    // ---------------------------------------------------------------------- listing ----

    [Fact]
    public async Task The_list_pages_in_the_database_and_never_repeats_a_row()
    {
        using var client = await AdminClientAsync("admin-paging");

        for (var i = 0; i < 12; i++)
        {
            await _factory.CreateMemberAsync($"paging-subject-{i:00}");
        }

        var firstPage = await client.GetStringAsync("/Admin/Users?pageSize=5&page=1&search=paging-subject");
        var secondPage = await client.GetStringAsync("/Admin/Users?pageSize=5&page=2&search=paging-subject");

        var firstIds = DetailsLinkRegex().Matches(firstPage).Select(m => m.Groups[1].Value).ToList();
        var secondIds = DetailsLinkRegex().Matches(secondPage).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(5, firstIds.Count);
        Assert.Equal(5, secondIds.Count);

        // A total order in the query is what stops rows from sliding between pages.
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task An_oversized_page_size_is_clamped_rather_than_honoured()
    {
        using var client = await AdminClientAsync("admin-pagesize");

        // Left unbounded, ?pageSize=100000 is a request for the entire table.
        var response = await client.GetAsync("/Admin/Users?pageSize=100000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var rendered = DetailsLinkRegex().Matches(body).Count;

        Assert.True(rendered <= 100, $"Expected at most 100 rows, rendered {rendered}.");
    }

    [Fact]
    public async Task Search_matches_on_display_name_and_username()
    {
        using var client = await AdminClientAsync("admin-search");

        await _factory.CreateMemberAsync("searchable-zebra");
        await _factory.CreateMemberAsync("searchable-quokka");

        var body = await client.GetStringAsync("/Admin/Users?search=zebra");

        Assert.Contains("searchable-zebra", body, StringComparison.Ordinal);
        Assert.DoesNotContain("searchable-quokka", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_term_containing_wildcards_is_treated_as_literal_text()
    {
        using var client = await AdminClientAsync("admin-search-wildcard");

        await _factory.CreateMemberAsync("wildcard-subject");

        // Left unescaped, "%" would match every user in the table.
        var body = await client.GetStringAsync("/Admin/Users?search=%25");

        Assert.DoesNotContain("wildcard-subject", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_query_string_falls_back_to_defaults_instead_of_erroring()
    {
        using var client = await AdminClientAsync("admin-badquery");

        var response = await client.GetAsync("/Admin/Users?page=-5&pageSize=0&sortBy=99&status=nonsense");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----------------------------------------------------------------------- create ----

    [Fact]
    public async Task An_administrator_can_create_a_user_who_can_then_sign_in()
    {
        using var client = await AdminClientAsync("admin-create");

        var result = await CreateUserAsync(client, "created-member", "created@example.com");
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);

        using var member = _factory.CreateNonRedirectingClient();
        await member.SignInAsync("created-member", NewUserPassword);

        var dashboard = await member.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_username_is_reported_rather_than_throwing()
    {
        using var client = await AdminClientAsync("admin-duplicate");

        await CreateUserAsync(client, "duplicate-member", "duplicate-one@example.com");
        var second = await CreateUserAsync(client, "duplicate-member", "duplicate-two@example.com");

        // Re-rendered form, not a redirect and not a 500.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("alert--danger", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_below_the_policy_is_refused()
    {
        using var client = await AdminClientAsync("admin-weakpassword");

        var response = await CreateUserAsync(
            client, "weak-password-member", "weak@example.com", password: "short1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var exists = await _factory.UserExistsAsync("weak-password-member");
        Assert.False(exists);
    }

    [Fact]
    public async Task A_phone_number_already_in_use_is_refused()
    {
        using var client = await AdminClientAsync("admin-duplicatephone");

        await CreateUserAsync(client, "phone-owner", "phone-owner@example.com", phone: "09121110001");

        // Same number, written differently. Normalisation is what makes this collide.
        var second = await CreateUserAsync(
            client, "phone-thief", "phone-thief@example.com", phone: "+98 912 111 0001");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(await _factory.UserExistsAsync("phone-thief"));
    }

    [Fact]
    public async Task A_created_user_can_sign_in_with_their_phone_number()
    {
        using var client = await AdminClientAsync("admin-phonelogin");

        await CreateUserAsync(client, "phone-login-member", "phone-login@example.com", phone: "09129998877");

        using var member = _factory.CreateNonRedirectingClient();
        var token = await member.GetAntiForgeryTokenAsync("/Account/Login");

        // Typed in yet another format, with Persian digits.
        var response = await member.PostLoginAsync(token, "۰۹۱۲۹۹۹۸۸۷۷", NewUserPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.IssuedAnAuthCookie());
    }

    // ----------------------------------------------------------------------- status ----

    [Fact]
    public async Task Disabling_an_account_ends_its_live_sessions_immediately()
    {
        using var admin = await AdminClientAsync("admin-revoke");
        var targetId = await _factory.CreateMemberAsync("revoke-subject");

        using var target = _factory.CreateNonRedirectingClient();
        await target.SignInAsync("revoke-subject", PortalTestData.MemberPassword);

        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/Dashboard")).StatusCode);

        await ChangeStatusAsync(admin, targetId, UserAccountStatus.Disabled);

        // Blocking future sign-ins is not enough; the cookie already issued must stop working.
        var afterDisable = await target.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, afterDisable.StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_disable_their_own_account()
    {
        using var client = await AdminClientAsync("admin-selfdisable");
        var selfId = await _factory.GetUserIdAsync("admin-selfdisable");

        var response = await ChangeStatusAsync(client, selfId, UserAccountStatus.Disabled);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserAccountStatus.Active, await _factory.GetAccountStatusAsync(selfId));
    }

    [Fact]
    public async Task The_last_active_super_admin_cannot_be_disabled()
    {
        // Locking out every administrator is unrecoverable from inside the application.
        using var client = await AdminClientAsync("admin-lastsuper");

        var superAdminId = await _factory.GetUserIdAsync(SentinelWebApplicationFactory.AdminUserName);

        var response = await ChangeStatusAsync(client, superAdminId, UserAccountStatus.Disabled);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserAccountStatus.Active, await _factory.GetAccountStatusAsync(superAdminId));
    }

    [Fact]
    public async Task A_status_change_is_audited()
    {
        using var admin = await AdminClientAsync("admin-auditstatus");
        var targetId = await _factory.CreateMemberAsync("audit-status-subject");

        await ChangeStatusAsync(admin, targetId, UserAccountStatus.Suspended);

        var actions = await _factory.RecentAuditActionsAsync(targetId.ToString());
        Assert.Contains("user.status.changed", actions);
    }

    // ------------------------------------------------------------------- membership ----

    [Fact]
    public async Task An_administrator_can_create_and_then_update_a_membership()
    {
        using var admin = await AdminClientAsync("admin-membership");
        var targetId = await _factory.CreateMemberAsync("membership-subject", withMembership: false);

        var created = await SaveMembershipAsync(
            admin, targetId, MembershipTier.Elite, DateTime.UtcNow.Date.AddDays(30), token: null);

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var membership = await _factory.GetMembershipAsync(targetId);
        Assert.NotNull(membership);
        Assert.Equal(MembershipTier.Elite, membership!.Tier);

        var updated = await SaveMembershipAsync(
            admin, targetId, MembershipTier.Basic, DateTime.UtcNow.Date.AddDays(60),
            token: membership.ConcurrencyToken);

        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal(MembershipTier.Basic, (await _factory.GetMembershipAsync(targetId))!.Tier);
    }

    [Fact]
    public async Task A_stale_membership_form_is_refused_instead_of_overwriting()
    {
        using var admin = await AdminClientAsync("admin-concurrency");
        var targetId = await _factory.CreateMemberAsync("concurrency-subject");

        var original = await _factory.GetMembershipAsync(targetId);
        Assert.NotNull(original);
        var staleToken = original!.ConcurrencyToken;

        // Somebody else saves first.
        await SaveMembershipAsync(
            admin, targetId, MembershipTier.Elite, DateTime.UtcNow.Date.AddDays(10), staleToken);

        // The second operator still holds the form rendered from the original row.
        var conflicted = await SaveMembershipAsync(
            admin, targetId, MembershipTier.Basic, DateTime.UtcNow.Date.AddDays(99), staleToken);

        Assert.Equal(HttpStatusCode.OK, conflicted.StatusCode);

        var body = await conflicted.Content.ReadAsStringAsync();
        Assert.Contains("alert--danger", body, StringComparison.Ordinal);

        // The first operator's change survived.
        Assert.Equal(MembershipTier.Elite, (await _factory.GetMembershipAsync(targetId))!.Tier);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_refused()
    {
        using var admin = await AdminClientAsync("admin-daterange");
        var targetId = await _factory.CreateMemberAsync("daterange-subject", withMembership: false);

        var response = await SaveMembershipAsync(
            admin,
            targetId,
            MembershipTier.Pro,
            endsAt: DateTime.UtcNow.Date.AddDays(-10),
            token: null,
            startsAt: DateTime.UtcNow.Date);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await _factory.GetMembershipAsync(targetId));
    }

    [Fact]
    public async Task Saving_a_membership_immediately_changes_what_the_member_can_open()
    {
        using var admin = await AdminClientAsync("admin-effect");

        var targetId = await _factory.CreateMemberAsync(
            "effect-subject", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-90));

        await _factory.CreateApplicationAsync("effect-app");

        using var member = _factory.CreateNonRedirectingClient();
        await member.SignInAsync("effect-subject", PortalTestData.MemberPassword);

        var beforeRenewal = await member.GetAsync("/apps/effect-app/open");
        Assert.Equal(HttpStatusCode.Forbidden, beforeRenewal.StatusCode);

        var membership = await _factory.GetMembershipAsync(targetId);
        var save = await SaveMembershipAsync(
            admin, targetId, MembershipTier.Pro, DateTime.UtcNow.Date.AddDays(30),
            membership!.ConcurrencyToken);

        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var renewed = await _factory.GetMembershipAsync(targetId);
        Assert.NotNull(renewed);
        Assert.True(
            renewed!.EndsAt > DateTimeOffset.UtcNow,
            $"Expected a future end date, got {renewed.EndsAt:O}.");

        // No re-login needed: access is evaluated per request, not baked into the cookie.
        var afterRenewal = await member.GetAsync("/apps/effect-app/open");

        Assert.Equal(HttpStatusCode.Redirect, afterRenewal.StatusCode);
        Assert.Equal("https://apps.example.com/target", afterRenewal.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_date_entered_under_the_persian_culture_is_stored_as_the_gregorian_date_typed()
    {
        // MVC's form value provider parses with the request culture. Under fa-IR that means
        // the Persian calendar, which read the ISO string an <input type="date"> submits as a
        // Persian date and stored a year six centuries out — plausible-looking in the form and
        // catastrophically wrong in the database.
        using var admin = await AdminClientAsync("admin-persian-dates");
        var targetId = await _factory.CreateMemberAsync("persian-date-subject", withMembership: false);

        var startsOn = new DateTime(2026, 3, 21, 0, 0, 0, DateTimeKind.Utc);
        var endsOn = new DateTime(2027, 3, 20, 0, 0, 0, DateTimeKind.Utc);

        var response = await SaveMembershipAsync(
            admin, targetId, MembershipTier.Pro, endsOn, token: null, startsAt: startsOn);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var stored = await _factory.GetMembershipAsync(targetId);
        Assert.NotNull(stored);

        Assert.Equal(2026, stored!.StartsAt.Year);
        Assert.Equal(3, stored.StartsAt.Month);
        Assert.Equal(21, stored.StartsAt.Day);

        Assert.Equal(2027, stored.EndsAt!.Value.Year);
        Assert.Equal(3, stored.EndsAt.Value.Month);
        Assert.Equal(20, stored.EndsAt.Value.Day);
    }

    [Fact]
    public async Task The_preview_endpoint_reports_the_status_the_resolver_would_produce()
    {
        using var admin = await AdminClientAsync("admin-preview");
        var targetId = await _factory.CreateMemberAsync("preview-subject");

        var token = await admin.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{targetId}");

        var response = await admin.PostAsync("/Admin/Users/PreviewMembership",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = targetId.ToString(),
                ["Tier"] = nameof(MembershipTier.Pro),
                ["AdminState"] = nameof(MembershipAdminState.Active),
                ["StartsAt"] = DateTime.UtcNow.Date.AddDays(-10).ToString("yyyy-MM-dd"),
                // Ended a month ago: past the grace period, so no access.
                ["EndsAt"] = DateTime.UtcNow.Date.AddDays(-30).ToString("yyyy-MM-dd"),
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var expired = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(expired.RootElement.GetProperty("grantsAccess").GetBoolean());

        // And the opposite case, so the endpoint is not simply always answering "no".
        var liveResponse = await admin.PostAsync("/Admin/Users/PreviewMembership",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = targetId.ToString(),
                ["Tier"] = nameof(MembershipTier.Pro),
                ["AdminState"] = nameof(MembershipAdminState.Active),
                ["StartsAt"] = DateTime.UtcNow.Date.AddDays(-10).ToString("yyyy-MM-dd"),
                ["EndsAt"] = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd"),
            }));

        using var live = JsonDocument.Parse(await liveResponse.Content.ReadAsStringAsync());
        Assert.True(live.RootElement.GetProperty("grantsAccess").GetBoolean());
        Assert.True(live.RootElement.GetProperty("daysRemaining").GetInt32() > 25);
    }

    // ------------------------------------------------------------------------ helpers ----

    private static Task<HttpResponseMessage> CreateUserAsync(
        HttpClient client,
        string userName,
        string email,
        string? phone = null,
        string password = NewUserPassword) =>
        SubmitAsync(client, "/Admin/Users/Create", "/Admin/Users/Create", fields =>
        {
            fields["UserName"] = userName;
            fields["DisplayName"] = userName;
            fields["Email"] = email;
            fields["Password"] = password;
            fields["ConfirmPassword"] = password;
            fields["Roles"] = RoleNames.Member;
            fields["PreferredCulture"] = "fa";
            fields["TimeZoneId"] = "Asia/Tehran";

            if (phone is not null)
            {
                fields["PhoneNumber"] = phone;
            }
        });

    private static Task<HttpResponseMessage> ChangeStatusAsync(
        HttpClient client,
        Guid userId,
        UserAccountStatus status) =>
        SubmitAsync(client, $"/Admin/Users/Details/{userId}", "/Admin/Users/ChangeStatus", fields =>
        {
            fields["UserId"] = userId.ToString();
            fields["Status"] = status.ToString();
            fields["Note"] = "Changed by an integration test.";
        });

    private static Task<HttpResponseMessage> SaveMembershipAsync(
        HttpClient client,
        Guid userId,
        MembershipTier tier,
        DateTime endsAt,
        Guid? token,
        DateTime? startsAt = null) =>
        SubmitAsync(client, $"/Admin/Users/Details/{userId}", "/Admin/Users/SaveMembership", fields =>
        {
            fields["UserId"] = userId.ToString();
            fields["Tier"] = tier.ToString();
            fields["AdminState"] = nameof(MembershipAdminState.Active);
            fields["StartsAt"] = (startsAt ?? DateTime.UtcNow.Date.AddDays(-30)).ToString("yyyy-MM-dd");
            fields["EndsAt"] = endsAt.ToString("yyyy-MM-dd");

            if (token is { } concurrencyToken)
            {
                fields["ConcurrencyToken"] = concurrencyToken.ToString();
            }
        });

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string formPage,
        string action,
        Action<Dictionary<string, string>> build)
    {
        var token = await client.GetAntiForgeryTokenAsync(formPage);

        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        build(fields);

        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }
}
