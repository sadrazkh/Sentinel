using Sentinel.Vpn.Delivery;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// The delivery token is a capability: holding the URL is the whole authorisation, because a VPN
/// client application cannot sign in. These tests pin the properties that makes that safe.
/// </summary>
public sealed class DeliveryTokenTests
{
    [Fact]
    public void A_minted_token_has_the_expected_shape()
    {
        var (token, _) = DeliveryToken.Create();

        Assert.Equal(DeliveryToken.EncodedLength, token.Length);
        Assert.True(DeliveryToken.IsWellFormed(token));
    }

    [Fact]
    public void A_token_is_url_safe_without_escaping()
    {
        // It goes in a path segment and gets pasted into client applications by hand, so anything
        // needing percent-encoding would be a support problem.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var (token, _) = DeliveryToken.Create();

            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
            Assert.Equal(Uri.EscapeDataString(token), token);
        }
    }

    [Fact]
    public void Tokens_do_not_repeat()
    {
        // 256 bits from a cryptographic source. A collision here would mean one member's URL served
        // another's configurations.
        var minted = Enumerable.Range(0, 5_000).Select(_ => DeliveryToken.Create().Token).ToHashSet();

        Assert.Equal(5_000, minted.Count);
    }

    [Fact]
    public void The_hash_is_deterministic_and_the_token_is_not_recoverable_from_it()
    {
        var (token, hash) = DeliveryToken.Create();

        Assert.Equal(hash, DeliveryToken.Hash(token));

        // A hex SHA-256.
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(Uri.IsHexDigit));

        // The stored value must not contain the token.
        Assert.DoesNotContain(token, hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_tokens_hash_differently()
    {
        var (_, first) = DeliveryToken.Create();
        var (_, second) = DeliveryToken.Create();

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../etc/passwd")]
    [InlineData("a b c")]
    [InlineData("token-with-+-and-/")]
    public void A_malformed_token_is_refused_before_any_lookup(string? candidate) =>
        // Refused on shape so a crafted value never reaches a database query or a hash computation.
        Assert.False(DeliveryToken.IsWellFormed(candidate));

    [Fact]
    public void A_token_of_the_right_length_but_wrong_alphabet_is_refused() =>
        Assert.False(DeliveryToken.IsWellFormed(new string('!', DeliveryToken.EncodedLength)));

    [Fact]
    public void A_token_one_character_short_or_long_is_refused()
    {
        Assert.False(DeliveryToken.IsWellFormed(new string('a', DeliveryToken.EncodedLength - 1)));
        Assert.False(DeliveryToken.IsWellFormed(new string('a', DeliveryToken.EncodedLength + 1)));
    }

    [Fact]
    public void The_fingerprint_is_short_enough_not_to_be_the_credential()
    {
        var (token, _) = DeliveryToken.Create();
        var fingerprint = DeliveryToken.Fingerprint(token);

        // Eight characters of base64url is 48 bits — useful for correlating two log lines, useless
        // for reconstructing the remaining 208.
        Assert.StartsWith(token[..8], fingerprint, StringComparison.Ordinal);
        Assert.True(fingerprint.Length < token.Length / 2);
    }

    [Fact]
    public void The_fingerprint_of_nothing_is_harmless()
    {
        Assert.Equal("?", DeliveryToken.Fingerprint(null));
        Assert.Equal("?", DeliveryToken.Fingerprint(string.Empty));
        Assert.Equal("?", DeliveryToken.Fingerprint("tiny"));
    }
}
