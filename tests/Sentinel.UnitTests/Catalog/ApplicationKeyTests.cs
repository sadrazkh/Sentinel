using Sentinel.Application.Catalog;

namespace Sentinel.UnitTests.Catalog;

public sealed class ApplicationKeyTests
{
    [Theory]
    [InlineData("vault")]
    [InlineData("doc-vault")]
    [InlineData("app2")]
    [InlineData("a1-b2-c3")]
    public void An_ordinary_slug_is_accepted(string key)
    {
        Assert.True(ApplicationKey.IsValid(key));
    }

    [Theory]
    [InlineData("Vault")]
    [InlineData("doc vault")]
    [InlineData("doc_vault")]
    [InlineData("-vault")]
    [InlineData("vault-")]
    [InlineData("doc--vault")]
    [InlineData("والت")]
    public void Anything_that_would_need_escaping_in_a_url_is_rejected(string key)
    {
        // The key goes straight into /apps/{key}/open, so it is constrained rather than escaped
        // at each use — the one use that forgot would be the bug.
        Assert.False(ApplicationKey.IsValid(key));
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("a/b")]
    [InlineData("a?b=1")]
    [InlineData("a#b")]
    [InlineData("a%2fb")]
    public void A_path_or_query_injection_attempt_is_rejected(string key)
    {
        Assert.False(ApplicationKey.IsValid(key));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("")]
    [InlineData(null)]
    public void A_key_that_is_too_short_is_rejected(string? key)
    {
        Assert.False(ApplicationKey.IsValid(key));
    }

    [Fact]
    public void A_key_longer_than_the_column_is_rejected()
    {
        Assert.False(ApplicationKey.IsValid(new string('a', ApplicationKey.MaxLength + 1)));
    }
}
