using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class AuthenticationTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public AuthenticationTests(SentinelWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_page_is_reachable_anonymously()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Correct_credentials_sign_the_user_in_and_land_on_the_dashboard()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token, SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Dashboard", response.Headers.Location?.ToString());

        var dashboard = await client.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task Signing_in_by_email_works_as_well_as_by_username()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token, SentinelWebApplicationFactory.AdminEmail, SentinelWebApplicationFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_user_and_a_wrong_password_produce_the_identical_response()
    {
        // The whole point: nothing in the reply may reveal whether the account exists.
        using var unknownClient = _factory.CreateNonRedirectingClient();
        var unknownToken = await unknownClient.GetAntiForgeryTokenAsync("/Account/Login");
        var unknownResponse = await unknownClient.PostLoginAsync(
            unknownToken, "no-such-account-here", "Whatever-Password-1234");
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync();

        using var wrongPasswordClient = _factory.CreateNonRedirectingClient();
        var wrongToken = await wrongPasswordClient.GetAntiForgeryTokenAsync("/Account/Login");
        var wrongResponse = await wrongPasswordClient.PostLoginAsync(
            wrongToken, SentinelWebApplicationFactory.AdminUserName, "Definitely-Not-It-1234");
        var wrongBody = await wrongResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, unknownResponse.StatusCode);
        Assert.Equal(unknownResponse.StatusCode, wrongResponse.StatusCode);

        // Neither response may issue an authentication cookie.
        Assert.False(unknownResponse.IssuedAnAuthCookie());
        Assert.False(wrongResponse.IssuedAnAuthCookie());

        // Both render an error, and it is byte-for-byte the same one. Anything that differed
        // between the two would be an oracle for "does this account exist?".
        var unknownError = ExtractErrorMarker(unknownBody);
        var wrongError = ExtractErrorMarker(wrongBody);

        Assert.NotNull(unknownError);
        Assert.NotNull(wrongError);
        Assert.Equal(unknownError, wrongError);
    }

    [Fact]
    public async Task Empty_credentials_are_rejected_by_server_side_validation()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(token, string.Empty, string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.IssuedAnAuthCookie());
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in_even_with_the_right_password()
    {
        const string userName = "disabled-user";
        const string password = "Disabled-Account-Test-4321";

        await CreateUserAsync(userName, password, UserAccountStatus.Disabled);

        using var client = _factory.CreateNonRedirectingClient();
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(token, userName, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.IssuedAnAuthCookie());
    }

    [Fact]
    public async Task An_open_ended_suspension_blocks_sign_in()
    {
        const string userName = "suspended-user";
        const string password = "Suspended-Account-Test-4321";

        await CreateUserAsync(userName, password, UserAccountStatus.Suspended);

        using var client = _factory.CreateNonRedirectingClient();
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(token, userName, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.IssuedAnAuthCookie());
    }

    [Fact]
    public async Task Every_sign_in_attempt_is_recorded_whether_it_succeeds_or_not()
    {
        const string userName = "audited-user";
        const string password = "Audited-Account-Test-4321";

        var userId = await CreateUserAsync(userName, password, UserAccountStatus.Active);

        using var client = _factory.CreateNonRedirectingClient();

        var failToken = await client.GetAntiForgeryTokenAsync("/Account/Login");
        await client.PostLoginAsync(failToken, userName, "Wrong-Password-9999");

        var okToken = await client.GetAntiForgeryTokenAsync("/Account/Login");
        await client.PostLoginAsync(okToken, userName, password);

        var attempts = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            return await db.LoginAttempts
                .AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderBy(a => a.OccurredAt)
                .ToListAsync();
        });

        Assert.Equal(2, attempts.Count);
        Assert.False(attempts[0].Succeeded);
        Assert.True(attempts[1].Succeeded);

        // The submitted password must appear nowhere in the record.
        Assert.All(attempts, attempt =>
            Assert.DoesNotContain(password, attempt.AttemptedIdentifier, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Guid> CreateUserAsync(string userName, string password, UserAccountStatus status)
    {
        return await _factory.WithScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

            var existing = await userManager.FindByNameAsync(userName);
            if (existing is not null)
            {
                return existing.Id;
            }

            var user = new ApplicationUser
            {
                Id = SequentialGuid.New(),
                UserName = userName,
                Email = $"{userName}@sentinel.invalid",
                EmailConfirmed = true,
                DisplayName = userName,
                Status = status,
            };

            var result = await userManager.CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

            await userManager.AddToRoleAsync(user, RoleNames.Member);
            return user.Id;
        });
    }

    /// <summary>
    /// Pulls the rendered error text out of the alert block, so the two failure responses can
    /// be compared without hard-coding a translated string.
    /// </summary>
    private static string? ExtractErrorMarker(string html)
    {
        const string marker = "alert alert--danger";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        return end < 0 ? null : html[start..end];
    }
}
