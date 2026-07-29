using System.Text;
using Sentinel.Application.Subscriptions;

namespace Sentinel.UnitTests.Subscriptions;

public sealed class SubscriptionParserTests
{
    /// <summary>
    /// The shape a real panel returns: one vless entry using REALITY over XHTTP, with a remark
    /// carrying Persian text, emoji and percent-encoding. Credentials are placeholders.
    /// </summary>
    private const string RealisticVless =
        "vless://11111111-2222-3333-4444-555555555555@irba-ger3.example.net:443" +
        "?encryption=none&security=reality&sni=www.example.com&fp=chrome" +
        "&pbk=AAAABBBBCCCC&sid=1a2b&type=xhttp&mode=auto&path=%2Fpath" +
        "#IRbaGER3%F0%9F%87%A9%F0%9F%87%AA-Ali_pi0ck-9.97GB%F0%9F%93%8A-29D%2C23H%E2%8F%B3";

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    // --------------------------------------------------------------------------- decoding ----

    [Fact]
    public void A_whole_body_base64_payload_is_decoded()
    {
        // This is how the sample subscription actually arrives.
        var configs = SubscriptionParser.ParseBody(Base64(RealisticVless));

        var config = Assert.Single(configs);
        Assert.Equal(ProxyProtocol.Vless, config.Protocol);
        Assert.Equal("irba-ger3.example.net", config.Host);
        Assert.Equal(443, config.Port);
    }

    [Fact]
    public void A_plain_text_payload_is_read_as_is()
    {
        var configs = SubscriptionParser.ParseBody(RealisticVless);

        Assert.Single(configs);
    }

    [Fact]
    public void Base64_without_padding_is_accepted()
    {
        // Panels routinely strip the trailing '='.
        var configs = SubscriptionParser.ParseBody(Base64(RealisticVless).TrimEnd('='));

        Assert.Single(configs);
    }

    [Fact]
    public void The_url_safe_base64_alphabet_is_accepted()
    {
        var encoded = Base64(RealisticVless).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.Single(SubscriptionParser.ParseBody(encoded));
    }

    [Fact]
    public void Text_that_merely_looks_like_base64_is_left_alone()
    {
        // A body of ordinary words must not be mangled into noise by an over-eager decode.
        Assert.Empty(SubscriptionParser.ParseBody("ThisIsJustSomeOrdinaryTextWithoutAnyConfigs"));
    }

    // ---------------------------------------------------------------------------- parsing ----

    [Fact]
    public void The_remark_is_percent_decoded_including_persian_and_emoji()
    {
        var config = Assert.Single(SubscriptionParser.ParseBody(RealisticVless));

        Assert.StartsWith("IRbaGER3", config.Remark, StringComparison.Ordinal);
        Assert.Contains("Ali_pi0ck", config.Remark, StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_details_are_extracted()
    {
        var config = Assert.Single(SubscriptionParser.ParseBody(RealisticVless));

        Assert.Equal("reality", config.Security);
        Assert.Equal("xhttp", config.Network);
        Assert.Equal("www.example.com", config.Sni);
    }

    [Fact]
    public void The_original_line_is_preserved_for_copying()
    {
        // The raw URI is the payload a member pastes into their own client.
        var config = Assert.Single(SubscriptionParser.ParseBody(RealisticVless));

        Assert.Equal(RealisticVless, config.RawUri);
    }

    [Theory]
    [InlineData("vless://x@h.example:443#a", ProxyProtocol.Vless)]
    [InlineData("trojan://pass@h.example:443#a", ProxyProtocol.Trojan)]
    [InlineData("ss://YWVzOnBhc3M@h.example:8388#a", ProxyProtocol.Shadowsocks)]
    [InlineData("hysteria2://pass@h.example:443#a", ProxyProtocol.Hysteria2)]
    [InlineData("hy2://pass@h.example:443#a", ProxyProtocol.Hysteria2)]
    [InlineData("tuic://uuid:pass@h.example:443#a", ProxyProtocol.Tuic)]
    public void Each_supported_protocol_is_recognised(string line, ProxyProtocol expected)
    {
        var config = Assert.Single(SubscriptionParser.ParseBody(line));

        Assert.Equal(expected, config.Protocol);
    }

    [Fact]
    public void A_vmess_entry_is_decoded_from_its_json_payload()
    {
        // vmess is the odd one: base64 JSON rather than a URI.
        var json = """
            {"v":"2","ps":"Tokyo node","add":"jp.example.net","port":"443",
             "id":"11111111-2222-3333-4444-555555555555","net":"ws","tls":"tls","host":"cdn.example.net"}
            """;

        var config = Assert.Single(SubscriptionParser.ParseBody("vmess://" + Base64(json)));

        Assert.Equal(ProxyProtocol.Vmess, config.Protocol);
        Assert.Equal("Tokyo node", config.Remark);
        Assert.Equal("jp.example.net", config.Host);
        Assert.Equal(443, config.Port);
        Assert.Equal("ws", config.Network);
    }

    [Fact]
    public void A_vmess_port_given_as_a_number_is_read_too()
    {
        var json = """{"ps":"n","add":"h.example","port":8443,"net":"tcp"}""";

        var config = Assert.Single(SubscriptionParser.ParseBody("vmess://" + Base64(json)));

        Assert.Equal(8443, config.Port);
    }

    [Fact]
    public void A_malformed_vmess_entry_still_yields_a_card_rather_than_dropping_everything()
    {
        var config = Assert.Single(SubscriptionParser.ParseBody("vmess://not-valid-base64!!!"));

        Assert.Equal(ProxyProtocol.Vmess, config.Protocol);
        Assert.Null(config.Host);
    }

    // ------------------------------------------------------------------------- robustness ----

    [Fact]
    public void Multiple_entries_are_all_parsed()
    {
        var body = string.Join('\n',
            "vless://a@one.example:443#One",
            "trojan://b@two.example:443#Two",
            "vmess://" + Base64("""{"ps":"Three","add":"three.example","port":443}"""));

        Assert.Equal(3, SubscriptionParser.ParseBody(Base64(body)).Count);
    }

    [Fact]
    public void Blank_lines_and_comments_are_skipped()
    {
        var body = "# a comment\n\nvless://a@one.example:443#One\n// another\n\n";

        Assert.Single(SubscriptionParser.ParseBody(body));
    }

    [Fact]
    public void An_unrecognised_scheme_is_skipped_rather_than_shown_as_a_mystery()
    {
        var body = "ftp://files.example/x\nvless://a@one.example:443#One\nnonsense";

        var config = Assert.Single(SubscriptionParser.ParseBody(body));
        Assert.Equal(ProxyProtocol.Vless, config.Protocol);
    }

    [Fact]
    public void A_line_that_is_not_a_uri_at_all_does_not_throw()
    {
        Assert.Empty(SubscriptionParser.ParseBody("just some words\nand more words"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_body_yields_nothing(string? body)
    {
        Assert.Empty(SubscriptionParser.ParseBody(body));
    }

    [Fact]
    public void The_number_of_entries_is_capped()
    {
        // One payload must not be able to fill a page indefinitely.
        var body = string.Join('\n',
            Enumerable.Range(0, SubscriptionParser.MaxConfigs + 50)
                .Select(i => $"vless://a@host{i}.example:443#Node{i}"));

        Assert.Equal(SubscriptionParser.MaxConfigs, SubscriptionParser.ParseBody(body).Count);
    }

    [Fact]
    public void Control_characters_in_a_remark_are_stripped()
    {
        // A remark comes from a third-party server and is rendered on a page and in a
        // Telegram message.
        var config = Assert.Single(
            SubscriptionParser.ParseBody("vless://a@h.example:443#bad%0Aname%0Dhere"));

        Assert.DoesNotContain('\n', config.Remark);
        Assert.DoesNotContain('\r', config.Remark);
    }

    [Fact]
    public void An_overlong_remark_is_truncated()
    {
        var longRemark = new string('x', 500);
        var config = Assert.Single(
            SubscriptionParser.ParseBody($"vless://a@h.example:443#{longRemark}"));

        Assert.True(config.Remark.Length <= 120);
    }

    [Fact]
    public void An_entry_without_a_remark_falls_back_to_its_host_for_display()
    {
        var config = Assert.Single(SubscriptionParser.ParseBody("vless://a@h.example:443"));

        Assert.Equal("h.example", config.DisplayName);
    }
}
