using Sentinel.Application.Catalog;

namespace Sentinel.UnitTests.Catalog;

public sealed class ApplicationUrlPolicyTests
{
    [Theory]
    [InlineData("https://apps.example.com/vault")]
    [InlineData("https://apps.example.com:8443/vault?tab=1")]
    [InlineData("https://apps.example.com/path/with%20escape")]
    public void Https_destinations_are_accepted(string url)
    {
        Assert.Equal(ApplicationUrlRejection.None, ApplicationUrlPolicy.Validate(url, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("vbscript:msgbox(1)")]
    public void Script_bearing_schemes_are_rejected(string url)
    {
        // This is the check that matters most: the launch endpoint issues a redirect the
        // browser follows, which is exactly where a javascript: URL would execute.
        Assert.Equal(
            ApplicationUrlRejection.DisallowedScheme,
            ApplicationUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("apps.example.com/vault")]
    public void Anything_that_is_not_an_absolute_url_is_rejected(string url)
    {
        Assert.Equal(ApplicationUrlRejection.NotAbsolute, ApplicationUrlPolicy.Validate(url, out _));
    }

    [Theory]
    [InlineData("//evil.example/vault")]
    [InlineData("\\\\evil.example\\share")]
    public void Protocol_relative_and_unc_style_values_are_rejected(string url)
    {
        // The exact rejection code is platform-dependent — Windows parses these as UNC file
        // paths, so they land on DisallowedScheme rather than NotAbsolute. What has to hold
        // everywhere is that they never become a redirect target.
        Assert.NotEqual(ApplicationUrlRejection.None, ApplicationUrlPolicy.Validate(url, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Plain_http_to_a_public_host_is_rejected()
    {
        // Redirecting a signed-in member over plain http hands the request to anyone on the path.
        Assert.Equal(
            ApplicationUrlRejection.InsecureScheme,
            ApplicationUrlPolicy.Validate("http://apps.example.com/vault", out _));
    }

    [Theory]
    [InlineData("http://localhost:5000/app")]
    [InlineData("http://127.0.0.1:8080/app")]
    [InlineData("http://[::1]:8080/app")]
    public void Plain_http_to_loopback_is_allowed_for_local_development(string url)
    {
        Assert.Equal(ApplicationUrlRejection.None, ApplicationUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void A_url_carrying_credentials_is_rejected()
    {
        // user:password@ would end up in browser history and in the Referer of the next hop.
        Assert.Equal(
            ApplicationUrlRejection.EmbeddedCredentials,
            ApplicationUrlPolicy.Validate("https://admin:secret@apps.example.com/vault", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_destination_is_rejected(string? url)
    {
        Assert.Equal(ApplicationUrlRejection.Empty, ApplicationUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void A_destination_beyond_the_column_length_is_rejected()
    {
        var url = "https://apps.example.com/" + new string('a', ApplicationUrlPolicy.MaxLength);

        Assert.Equal(ApplicationUrlRejection.TooLong, ApplicationUrlPolicy.Validate(url, out _));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_make_a_valid_url_invalid()
    {
        Assert.True(ApplicationUrlPolicy.IsAllowed("  https://apps.example.com/vault  "));
    }
}
