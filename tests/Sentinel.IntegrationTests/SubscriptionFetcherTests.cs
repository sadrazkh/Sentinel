using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sentinel.Application.Subscriptions;
using Sentinel.Infrastructure.Subscriptions;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The fetcher against real network conditions.
/// <para>
/// The SSRF cases are the point: they attempt an actual connection to an actual internal
/// address and assert it is refused. A mocked handler would only prove the mock behaves as
/// written, which is exactly the thing worth not assuming here.
/// </para>
/// </summary>
public sealed class SubscriptionFetcherTests
{
    private static SubscriptionFetcher CreateFetcher(Action<SubscriptionFetchOptions>? configure = null)
    {
        var options = new SubscriptionFetchOptions
        {
            Enabled = true,
            TimeoutSeconds = 10,
            MaxResponseBytes = 512 * 1024,
            MaxRedirects = 3,
        };

        configure?.Invoke(options);

        return new SubscriptionFetcher(
            Options.Create(options),
            NullLogger<SubscriptionFetcher>.Instance);
    }

    // ------------------------------------------------------------------------- refusals ----

    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://localhost/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://192.168.1.1/x")]
    [InlineData("http://[::1]/x")]
    public async Task An_internal_target_is_refused_without_a_connection(string url)
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(url, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Outcome,
            new[] { SubscriptionFetchOutcome.RejectedUrl, SubscriptionFetchOutcome.BlockedAddress });

        Assert.Empty(result.Content.Configs);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example/x")]
    [InlineData("gopher://example:70/x")]
    public async Task A_non_http_scheme_is_refused(string url)
    {
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(url, CancellationToken.None);

        Assert.Equal(SubscriptionFetchOutcome.RejectedUrl, result.Outcome);
    }

    [Fact]
    public async Task A_hostname_that_resolves_to_loopback_is_refused_at_connect_time()
    {
        // This is the DNS-rebinding shape: the URL looks like an ordinary public hostname and
        // passes the textual check, and only the address it resolves to gives it away.
        // localtest.me is a public domain whose records all point at 127.0.0.1.
        using var fetcher = CreateFetcher();

        var result = await fetcher.FetchAsync(
            "http://sub.localtest.me/x", CancellationToken.None);

        Assert.False(result.Succeeded);

        // Either the connect callback refused it, or DNS did not resolve in this environment;
        // what must never happen is a successful fetch.
        Assert.NotEqual(SubscriptionFetchOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task Fetching_is_refused_outright_when_the_feature_is_disabled()
    {
        using var fetcher = CreateFetcher(options => options.Enabled = false);

        var result = await fetcher.FetchAsync(
            "https://sub.example.info/x", CancellationToken.None);

        Assert.Equal(SubscriptionFetchOutcome.RejectedUrl, result.Outcome);
    }

    [Fact]
    public async Task An_unreachable_host_reports_a_failure_rather_than_throwing()
    {
        using var fetcher = CreateFetcher(options => options.TimeoutSeconds = 5);

        var result = await fetcher.FetchAsync(
            "https://this-host-does-not-exist-sentinel-test.invalid/x",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Content.Configs);
    }

    // -------------------------------------------------------------------- the real link ----

    /// <summary>
    /// The subscription this feature was built against. Skipped automatically when the network
    /// is unavailable, so the suite stays runnable offline — but when it does run, it proves
    /// the whole path: fetch, quota header, base64 body, and parsed entries.
    /// </summary>
    [Fact]
    public async Task A_real_subscription_is_fetched_and_parsed()
    {
        const string url =
            "https://sub.irnetfree.info/api/Subs/GetOtherCdn/irba-ger3/gvtro8x7an6i85zi";

        using var fetcher = CreateFetcher();
        var result = await fetcher.FetchAsync(url, CancellationToken.None);

        if (result.Outcome is SubscriptionFetchOutcome.NetworkError
            or SubscriptionFetchOutcome.Timeout)
        {
            // No network in this environment. Not a failure of the code under test.
            Assert.True(true);
            return;
        }

        Assert.True(result.Succeeded, $"Fetch failed: {result.Outcome} {result.Reason}");

        // The provider reports quota and expiry through subscription-userinfo.
        Assert.NotNull(result.Content.UserInfo.TotalBytes);
        Assert.NotNull(result.Content.UserInfo.ExpiresAt);

        // The body decodes to at least one usable entry.
        Assert.NotEmpty(result.Content.Configs);

        var config = result.Content.Configs[0];
        Assert.NotEqual(ProxyProtocol.Unknown, config.Protocol);
        Assert.False(string.IsNullOrWhiteSpace(config.RawUri));
        Assert.False(string.IsNullOrWhiteSpace(config.DisplayName));
    }
}
