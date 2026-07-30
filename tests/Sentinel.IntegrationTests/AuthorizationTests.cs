using System.Net;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

public sealed class AuthorizationTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public AuthorizationTests(SentinelWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/Dashboard")]
    [InlineData("/Dashboard/Index")]
    public async Task Protected_pages_send_an_anonymous_visitor_to_the_login_page(string path)
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/Account/Login", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_root_path_serves_the_landing_page_to_an_anonymous_visitor()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A way in, and an explanation of what is being signed in to.
        Assert.Contains("/Account/Login", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_landing_page_does_not_name_a_single_product()
    {
        // The catalogue is behind the sign-in. Listing it on a public page would leak what is
        // being offered, and to whom, before anyone has authenticated.
        await _factory.CreateProductAsync("landing-secret-product");

        using var client = _factory.CreateNonRedirectingClient();
        var page = await client.GetStringAsync("/");

        Assert.DoesNotContain("landing-secret-product", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_root_path_sends_a_signed_in_visitor_to_their_dashboard()
    {
        // Landing on a marketing page when you already have an account is friction.
        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(
            SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Dashboard",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_authenticated_user_reaches_the_dashboard()
    {
        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(
            SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var response = await client.GetAsync("/Dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sign_out_endpoints_are_not_reachable_without_a_session()
    {
        // A controller-level [AllowAnonymous] would silently override the [Authorize] on
        // these actions; this is the regression guard for that.
        using var client = _factory.CreateNonRedirectingClient();
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");

        var logout = await client.PostAsync("/Account/Logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains(
            "/Account/Login",
            logout.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_stay_anonymous_despite_the_authenticated_fallback_policy(string path)
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_path_tells_an_anonymous_visitor_nothing_about_whether_it_exists()
    {
        // The authenticated fallback policy also covers requests that match no endpoint, so an
        // anonymous probe gets the same redirect for a real page and an imaginary one. That is
        // the desired outcome: the site map is not enumerable from the outside.
        using var client = _factory.CreateNonRedirectingClient();

        var unknown = await client.GetAsync("/no/such/page");
        var known = await client.GetAsync("/Dashboard");

        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
    }

    [Fact]
    public async Task An_unknown_path_renders_the_branded_error_page_rather_than_a_stack_trace()
    {
        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(
            SentinelWebApplicationFactory.AdminUserName, SentinelWebApplicationFactory.AdminPassword);

        var response = await client.GetAsync("/no/such/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);

        // The correlation id is the only technical detail on the page.
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }
}
