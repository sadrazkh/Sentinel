using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Who may reach the admin area, and who may only look. Support is read-only by design, and
/// this suite is what keeps that from quietly becoming read-write the next time a controller
/// gains an attribute.
/// </summary>
public sealed class AdminAuthorizationTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public AdminAuthorizationTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_page()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Admin/Users");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ordinary_member_is_refused()
    {
        await _factory.CreateMemberAsync("admin-auth-member");

        using var client = await SignedInAsync("admin-auth-member");
        var response = await client.GetAsync("/Admin/Users");

        // The cookie is valid, the role is not: an access-denied redirect, not a login prompt.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_read_the_user_list()
    {
        await _factory.CreateMemberAsync("admin-auth-support");
        await _factory.AddToRoleAsync("admin-auth-support", RoleNames.Support);

        using var client = await SignedInAsync("admin-auth-support");
        var response = await client.GetAsync("/Admin/Users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Support_cannot_reach_the_create_form()
    {
        await _factory.CreateMemberAsync("admin-auth-support-create");
        await _factory.AddToRoleAsync("admin-auth-support-create", RoleNames.Support);

        using var client = await SignedInAsync("admin-auth-support-create");
        var response = await client.GetAsync("/Admin/Users/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_cannot_change_an_account_status()
    {
        var targetId = await _factory.CreateMemberAsync("admin-auth-target");

        await _factory.CreateMemberAsync("admin-auth-support-write");
        await _factory.AddToRoleAsync("admin-auth-support-write", RoleNames.Support);

        using var client = await SignedInAsync("admin-auth-support-write");
        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{targetId}");

        var response = await client.PostAsync("/Admin/Users/ChangeStatus",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = targetId.ToString(),
                ["Status"] = nameof(UserAccountStatus.Disabled),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // And the target is genuinely untouched, not merely redirected away from.
        var status = await _factory.GetAccountStatusAsync(targetId);
        Assert.Equal(UserAccountStatus.Active, status);
    }

    [Fact]
    public async Task An_admin_cannot_change_roles_that_only_a_super_admin_may_change()
    {
        var targetId = await _factory.CreateMemberAsync("admin-auth-role-target");

        await _factory.CreateMemberAsync("admin-auth-plain-admin");
        await _factory.AddToRoleAsync("admin-auth-plain-admin", RoleNames.Admin);

        using var client = await SignedInAsync("admin-auth-plain-admin");
        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{targetId}");

        var response = await client.PostAsync("/Admin/Users/SetRoles",
            new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("__RequestVerificationToken", token),
                new("UserId", targetId.ToString()),
                new("Roles", RoleNames.SuperAdmin),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // The privilege escalation did not happen.
        var roles = await _factory.GetRolesAsync(targetId);
        Assert.DoesNotContain(RoleNames.SuperAdmin, roles);
    }

    [Fact]
    public async Task An_admin_can_read_and_write()
    {
        var targetId = await _factory.CreateMemberAsync("admin-auth-write-target");

        await _factory.CreateMemberAsync("admin-auth-writer");
        await _factory.AddToRoleAsync("admin-auth-writer", RoleNames.Admin);

        using var client = await SignedInAsync("admin-auth-writer");

        var list = await client.GetAsync("/Admin/Users");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Users/Details/{targetId}");

        var response = await client.PostAsync("/Admin/Users/ChangeStatus",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserId"] = targetId.ToString(),
                ["Status"] = nameof(UserAccountStatus.Disabled),
                ["Note"] = "Integration test",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(UserAccountStatus.Disabled, await _factory.GetAccountStatusAsync(targetId));
    }

    [Fact]
    public async Task An_admin_mutation_without_an_anti_forgery_token_is_refused()
    {
        var targetId = await _factory.CreateMemberAsync("admin-auth-csrf-target");

        await _factory.CreateMemberAsync("admin-auth-csrf-admin");
        await _factory.AddToRoleAsync("admin-auth-csrf-admin", RoleNames.Admin);

        using var client = await SignedInAsync("admin-auth-csrf-admin");
        await client.GetAsync($"/Admin/Users/Details/{targetId}");

        var response = await client.PostAsync("/Admin/Users/ChangeStatus",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["UserId"] = targetId.ToString(),
                ["Status"] = nameof(UserAccountStatus.Disabled),
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(UserAccountStatus.Active, await _factory.GetAccountStatusAsync(targetId));
    }

    [Fact]
    public async Task A_details_page_for_an_unknown_user_is_a_404()
    {
        await _factory.CreateMemberAsync("admin-auth-404");
        await _factory.AddToRoleAsync("admin-auth-404", RoleNames.Admin);

        using var client = await SignedInAsync("admin-auth-404");
        var response = await client.GetAsync($"/Admin/Users/Details/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
