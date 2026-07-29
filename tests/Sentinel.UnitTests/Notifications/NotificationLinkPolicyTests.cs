using Sentinel.Application.Notifications;

namespace Sentinel.UnitTests.Notifications;

/// <summary>
/// A notification's link becomes something the member clicks — in the portal and, once
/// delivered, as a link inside a Telegram message. An absolute URL stored here would be an
/// open redirect handed straight to them, with the portal's own name on it.
/// </summary>
public sealed class NotificationLinkPolicyTests
{
    [Theory]
    [InlineData("/Apps")]
    [InlineData("/Membership")]
    [InlineData("/Apps?filter=locked")]
    [InlineData("/Admin/Users/Details/9f1c")]
    [InlineData("/Notifications#latest")]
    public void A_local_path_is_kept(string path)
    {
        Assert.Equal(path, NotificationLinkPolicy.Sanitize(path));
        Assert.True(NotificationLinkPolicy.IsAllowed(path));
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("Apps")]
    public void Anything_that_could_leave_the_site_is_dropped(string path)
    {
        Assert.Null(NotificationLinkPolicy.Sanitize(path));
    }

    [Theory]
    [InlineData("/Apps/../../etc/passwd")]
    [InlineData("/a\\b")]
    [InlineData("/..")]
    public void A_traversal_or_backslash_attempt_is_dropped(string path)
    {
        Assert.Null(NotificationLinkPolicy.Sanitize(path));
    }

    [Theory]
    [InlineData("/Apps\nX-Injected: 1")]
    [InlineData("/Apps\r\nSet-Cookie: a=b")]
    [InlineData("/Apps\0")]
    public void A_control_character_is_dropped(string path)
    {
        // A stored value with a newline in it would break out of whatever attribute or header
        // it is later written into.
        Assert.Null(NotificationLinkPolicy.Sanitize(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_link_is_simply_absent(string? path)
    {
        Assert.Null(NotificationLinkPolicy.Sanitize(path));
        Assert.False(NotificationLinkPolicy.IsAllowed(path));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_make_a_valid_path_invalid()
    {
        Assert.Equal("/Apps", NotificationLinkPolicy.Sanitize("  /Apps  "));
    }

    [Fact]
    public void A_path_longer_than_the_column_is_dropped()
    {
        Assert.Null(NotificationLinkPolicy.Sanitize("/" + new string('a', NotificationLinkPolicy.MaxLength)));
    }
}
