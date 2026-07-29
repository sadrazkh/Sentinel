using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class SecurityTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public SecurityTests(SentinelWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("Permissions-Policy"));
        Assert.True(response.Headers.Contains("Cross-Origin-Opener-Policy"));

        // Kestrel's banner is suppressed at start-up.
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task The_content_security_policy_is_strict_and_nonce_based()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Account/Login");
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("'nonce-", policy, StringComparison.Ordinal);

        // Vue templates are compiled at build time precisely so these never have to appear.
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_nonce_is_different_on_every_response()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var first = await client.GetAsync("/Account/Login");
        var second = await client.GetAsync("/Account/Login");

        var firstPolicy = first.Headers.GetValues("Content-Security-Policy").Single();
        var secondPolicy = second.Headers.GetValues("Content-Security-Policy").Single();

        // A reused nonce would be no better than allowing inline script outright.
        Assert.NotEqual(firstPolicy, secondPolicy);
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task A_forged_correlation_id_is_replaced_rather_than_echoed()
    {
        using var client = _factory.CreateNonRedirectingClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "bad value <script>");

        var response = await client.SendAsync(request);
        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();

        Assert.DoesNotContain("<", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", echoed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_authentication_cookie_is_http_only()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token, SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var authCookie = response.SetCookies()
            .LastOrDefault(value => value.StartsWith("sentinel.auth=", StringComparison.Ordinal)
                                    && !value.StartsWith("sentinel.auth=;", StringComparison.Ordinal));

        Assert.NotNull(authCookie);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_anti_forgery_cookie_is_http_only_and_same_site_strict()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Account/Login");

        var csrfCookie = response.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(value => value.StartsWith("sentinel.csrf", StringComparison.Ordinal));

        Assert.NotNull(csrfCookie);
        Assert.Contains("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", csrfCookie, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ CSRF ----------

    [Fact]
    public async Task A_post_without_an_anti_forgery_token_is_refused()
    {
        using var client = _factory.CreateNonRedirectingClient();

        // Prime the cookie but deliberately omit the matching form field.
        await client.GetAsync("/Account/Login");

        var response = await client.PostAsync("/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Identifier"] = SentinelWebApplicationFactory.AdminUserName,
                ["Password"] = SentinelWebApplicationFactory.AdminPassword,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_post_with_a_tampered_anti_forgery_token_is_refused()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var tampered = token[..^4] + "AAAA";

        var response = await client.PostLoginAsync(
            tampered, SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_language_switch_also_requires_an_anti_forgery_token()
    {
        using var client = _factory.CreateNonRedirectingClient();
        await client.GetAsync("/Account/Login");

        var response = await client.PostAsync("/language",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["culture"] = "en-US" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unsupported_culture_is_rejected_by_the_language_switch()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");

        var response = await client.PostAsync("/language",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["culture"] = "xx-XX",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------- open redirect ----------

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example/steal")]
    [InlineData("http:/evil.example")]
    [InlineData("https://evil.example\\@localhost/")]
    public async Task A_non_local_returnUrl_is_ignored_after_sign_in(string returnUrl)
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token,
            SentinelWebApplicationFactory.AdminUserName,
            SentinelWebApplicationFactory.AdminPassword,
            returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Equal("/Dashboard", location);
        Assert.DoesNotContain("evil.example", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_local_returnUrl_is_honoured()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token,
            SentinelWebApplicationFactory.AdminUserName,
            SentinelWebApplicationFactory.AdminPassword,
            "/Dashboard/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Dashboard/Index", response.Headers.Location?.ToString());
    }

    // -------------------------------------------------------- session lifetime --------

    [Fact]
    public async Task Signing_out_revokes_the_server_side_session()
    {
        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(
            SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var beforeLogout = await client.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);

        var token = await client.GetAntiForgeryTokenAsync("/Dashboard");
        var logout = await client.PostAsync("/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var afterLogout = await client.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    [Fact]
    public async Task A_captured_cookie_stops_working_the_moment_the_session_is_revoked()
    {
        // Sign-out must be more than deleting the client's copy of the cookie: a cookie
        // captured beforehand has to stop being accepted too.
        using var victim = _factory.CreateNonRedirectingClient();

        var loginToken = await victim.GetAntiForgeryTokenAsync("/Account/Login");
        var loginResponse = await victim.PostLoginAsync(
            loginToken,
            SentinelWebApplicationFactory.AdminUserName,
            SentinelWebApplicationFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        // The very cookie this client is now using — not a second, unrelated session.
        var stolenCookie = loginResponse.FindAuthCookie();
        Assert.NotNull(stolenCookie);

        using var attacker = _factory.CreateNonRedirectingClient();
        using var probe = new HttpRequestMessage(HttpMethod.Get, "/Dashboard");
        probe.Headers.Add("Cookie", stolenCookie);

        var withLiveSession = await attacker.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, withLiveSession.StatusCode);

        var token = await victim.GetAntiForgeryTokenAsync("/Dashboard");
        await victim.PostAsync("/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        using var replay = new HttpRequestMessage(HttpMethod.Get, "/Dashboard");
        replay.Headers.Add("Cookie", stolenCookie);

        var afterRevocation = await attacker.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Redirect, afterRevocation.StatusCode);
    }

    [Fact]
    public async Task Signing_in_replaces_any_cookie_the_browser_arrived_with()
    {
        // Session fixation: a pre-set cookie must not survive the privilege change, so the
        // sign-in response first expires whatever was there and only then issues a new one.
        using var client = _factory.CreateNonRedirectingClient();

        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");
        var response = await client.PostLoginAsync(
            token, SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var authCookieHeaders = response.SetCookies()
            .Where(value => value.StartsWith("sentinel.auth=", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(authCookieHeaders, value => value.StartsWith("sentinel.auth=;", StringComparison.Ordinal));
        Assert.NotNull(response.FindAuthCookie());
    }

    [Fact]
    public async Task Signing_in_creates_exactly_one_active_session_row()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var before = await CountActiveSessionsAsync();
        await client.SignInAsync(
            SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);
        var after = await CountActiveSessionsAsync();

        Assert.Equal(before + 1, after);
    }

    private Task<int> CountActiveSessionsAsync() =>
        _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ISentinelDbContext>();
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

            return await db.UserSessions
                .AsNoTracking()
                .CountAsync(s => s.RevokedAt == null && s.ExpiresAt > now);
        });
}
