using Sentinel.Application.Subscriptions;

namespace Sentinel.UnitTests.Subscriptions;

/// <summary>
/// The first of two SSRF layers: screening the URL as written. It is not sufficient on its own
/// — a hostname can resolve anywhere, and can resolve differently between this check and the
/// connection — which is why <see cref="IpAddressPolicy"/> is applied again at connect time.
/// </summary>
public sealed class SubscriptionUrlPolicyTests
{
    [Theory]
    [InlineData("https://sub.example.info/api/Subs/GetOtherCdn/abc/def")]
    [InlineData("https://sub.example.info:8443/x")]
    [InlineData("http://sub.example.info/x")]
    [InlineData("https://sub.example.info:2053/x")]
    public void An_ordinary_subscription_url_is_accepted(string url)
    {
        Assert.Equal(SubscriptionUrlRejection.None, SubscriptionUrlPolicy.Validate(url, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example/x")]
    [InlineData("gopher://example/x")]
    [InlineData("dict://example:11211/stat")]
    [InlineData("javascript:alert(1)")]
    public void A_scheme_other_than_http_is_refused(string url)
    {
        // These schemes appear in SSRF write-ups precisely because a permissive fetcher will
        // follow them.
        Assert.Equal(
            SubscriptionUrlRejection.DisallowedScheme,
            SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData("http://localhost/x")]
    [InlineData("http://LOCALHOST/x")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://instance-data/latest/meta-data/")]
    public void A_hostname_that_never_serves_a_customer_subscription_is_refused(string url)
    {
        Assert.Equal(
            SubscriptionUrlRejection.DisallowedHost,
            SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://[fc00::1]/x")]
    public void A_literal_internal_address_is_refused_at_the_form(string url)
    {
        // Caught here so an operator sees a clear message while still looking at the form,
        // rather than a vaguer failure later.
        Assert.Equal(
            SubscriptionUrlRejection.DisallowedHost,
            SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void A_url_carrying_credentials_is_refused()
    {
        // user:password@ would be sent to whatever the host turns out to be.
        Assert.Equal(
            SubscriptionUrlRejection.EmbeddedCredentials,
            SubscriptionUrlPolicy.Validate("https://user:secret@sub.example.info/x", out _));
    }

    [Theory]
    [InlineData("https://sub.example.info:22/x")]
    [InlineData("https://sub.example.info:6379/x")]
    [InlineData("https://sub.example.info:11211/x")]
    [InlineData("https://sub.example.info:5432/x")]
    public void An_unusual_port_is_refused(string url)
    {
        // Restricting ports stops the portal being used to probe internal services even when
        // the hostname itself resolves publicly.
        Assert.Equal(
            SubscriptionUrlRejection.NonStandardPort,
            SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData("sub.example.info/x")]
    [InlineData("/relative/path")]
    [InlineData("not a url at all")]
    public void A_value_that_is_not_an_absolute_url_is_refused(string url)
    {
        Assert.NotEqual(SubscriptionUrlRejection.None, SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_url_is_refused(string? url)
    {
        Assert.Equal(SubscriptionUrlRejection.Empty, SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void A_url_longer_than_the_column_is_refused()
    {
        var url = "https://sub.example.info/" + new string('a', SubscriptionUrlPolicy.MaxLength);

        Assert.Equal(SubscriptionUrlRejection.TooLong, SubscriptionUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_make_a_valid_url_invalid()
    {
        Assert.True(SubscriptionUrlPolicy.IsAllowed("  https://sub.example.info/x  "));
    }
}
