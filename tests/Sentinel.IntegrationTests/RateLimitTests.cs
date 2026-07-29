using System.Net;
using Microsoft.AspNetCore.Hosting;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The login limiter partitions by source address, which is the half of the problem Identity's
/// per-account lockout does not cover: one client spraying a single common password across
/// many accounts never trips a per-account counter.
/// </summary>
public sealed class RateLimitTests : IClassFixture<RateLimitTests.ThrottledFactory>
{
    private const int PermitLimit = 4;

    public sealed class ThrottledFactory : SentinelWebApplicationFactory
    {
        protected override void ConfigureTestSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Security:LoginRateLimit:PermitLimit", PermitLimit.ToString());
            builder.UseSetting("Security:LoginRateLimit:WindowSeconds", "60");
            builder.UseSetting("Security:LoginRateLimit:QueueLimit", "0");

            // Identity's own lockout would otherwise fire first and mask the limiter.
            builder.UseSetting("Security:Lockout:MaxFailedAttempts", "20");
        }
    }

    private readonly ThrottledFactory _factory;

    public RateLimitTests(ThrottledFactory factory) => _factory = factory;

    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_are_rejected_with_429()
    {
        using var client = _factory.CreateNonRedirectingClient();
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < PermitLimit + 2; attempt++)
        {
            var response = await client.PostLoginAsync(
                token, $"spray-target-{attempt}", "Sprayed-Password-1234");

            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(PermitLimit), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task The_limiter_only_covers_the_sign_in_endpoint()
    {
        using var client = _factory.CreateNonRedirectingClient();
        var token = await client.GetAntiForgeryTokenAsync("/Account/Login");

        for (var attempt = 0; attempt < PermitLimit + 2; attempt++)
        {
            await client.PostLoginAsync(token, $"spray-other-{attempt}", "Sprayed-Password-1234");
        }

        // Exhausting the sign-in budget must not take the rest of the site down with it.
        var page = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
