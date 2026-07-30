using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sentinel.Vpn.Panel;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// The panel client against a real socket. The behaviour that matters most is the classification
/// of outcomes: a write whose result is unknown must be distinguishable from one that was refused,
/// because one is safe to retry and the other is not.
/// </summary>
public sealed class ThreeXUiClientTests
{
    private static ThreeXUiClient CreateClient(int timeoutSeconds = 5) =>
        new(
            Options.Create(new ThreeXUiOptions
            {
                TimeoutSeconds = timeoutSeconds,

                // The fake panel is necessarily on loopback and plain http. Both are off by
                // default and refused in Production; enabling them here is what lets the real
                // client be tested rather than a substitute.
                AllowLoopbackPanelUrls = true,
                AllowInsecurePanelUrls = true,
            }),
            NullLogger<ThreeXUiClient>.Instance);

    private static PanelEndpoint EndpointFor(FakePanel panel) =>
        new(panel.BaseUrl, FakePanel.ValidToken);

    private static PanelClientRequest SampleRequest(string? email = null) => new(
        email ?? PanelClientEmail.Create(),
        [3, 5],
        TotalAllowanceBytes: 53_687_091_200,
        ExpiresAt: DateTimeOffset.FromUnixTimeMilliseconds(1_735_689_600_000),
        IpLimit: 2,
        Enabled: true);

    // ---------------------------------------------------------------- the panel's verbs ----

    [Theory]
    // Every route the portal calls, with the method the panel's own OpenAPI document declares.
    // Pinned as a table because getting one wrong is invisible in code review and produces a 404
    // the panel describes as an unknown path — which sends an operator to check a base path that
    // was never wrong. That is exactly what a POST to this GET-only status route did.
    [InlineData("GET", "panel/api/server/status")]
    [InlineData("GET", "panel/api/inbounds/list/slim")]
    public async Task The_portal_calls_each_route_with_the_method_the_panel_registers(
        string expectedMethod,
        string path)
    {
        await using var panel = FakePanel.Start();

        panel.Handlers[path] = _ => PanelReply.Ok(
            path.Contains("inbounds", StringComparison.Ordinal)
                ? Array.Empty<object>()
                : new { xray = new { state = "running" } });

        // getXrayVersion is called alongside status and is allowed to fail; the assertion below
        // looks only at the route under test.
        panel.Handlers["panel/api/server/getXrayVersion"] = _ => PanelReply.Ok("25.1.1");

        using var client = CreateClient();

        if (path.Contains("inbounds", StringComparison.Ordinal))
        {
            await client.ListInboundsAsync(EndpointFor(panel));
        }
        else
        {
            await client.GetStatusAsync(EndpointFor(panel));
        }

        var call = panel.Requests.Single(request => request.Path.EndsWith(path, StringComparison.Ordinal));

        Assert.Equal(expectedMethod, call.Method);
    }

    [Fact]
    public async Task A_healthy_panel_reports_xray_running()
    {
        // The whole probe path, end to end: status is read, its shape is parsed, and the server
        // comes back healthy. This is what a wrong verb silently prevented.
        await using var panel = FakePanel.Start();

        panel.Handlers["panel/api/server/status"] =
            _ => PanelReply.Ok(new { xray = new { state = "running" } });

        panel.Handlers["panel/api/server/getXrayVersion"] = _ => PanelReply.Ok("25.1.1");

        using var client = CreateClient();
        var result = await client.GetStatusAsync(EndpointFor(panel));

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Value!.XrayRunning);
    }

    // ------------------------------------------------------------------ authentication ----

    [Fact]
    public async Task The_api_token_is_sent_as_a_bearer_header()
    {
        // 3x-ui 3.x issues a static token under Settings → Security → API Token; there is no
        // login round-trip and no session cookie to maintain.
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/inbounds/list/slim"] = _ => PanelReply.Ok(Array.Empty<object>());

        using var client = CreateClient();
        var result = await client.ListInboundsAsync(EndpointFor(panel));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal($"Bearer {FakePanel.ValidToken}", panel.Requests.Single().Authorization);
    }

    [Fact]
    public async Task A_refused_token_is_reported_as_unauthorized_and_not_as_unknown()
    {
        // Distinct because the response differs: a wrong credential needs an operator, whereas an
        // unknown outcome needs reconciliation.
        await using var panel = FakePanel.Start();
        panel.RequireBearerToken();

        using var client = CreateClient();
        var result = await client.ListInboundsAsync(new PanelEndpoint(panel.BaseUrl, "wrong-token"));

        Assert.Equal(PanelOutcome.Unauthorized, result.Outcome);
        Assert.True(result.IsDefinitelyUnapplied);
    }

    // --------------------------------------------------------------- outcome classification ----

    [Fact]
    public async Task A_dropped_connection_on_a_write_is_an_unknown_outcome()
    {
        // The whole point of the enum. The panel may have applied the create before the socket
        // died, so a retry could produce a second client.
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Dropped();

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.UnknownOutcome, result.Outcome);
        Assert.False(result.IsDefinitelyUnapplied);
    }

    [Fact]
    public async Task A_timeout_on_a_write_is_an_unknown_outcome()
    {
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Slow(TimeSpan.FromSeconds(5));

        using var client = CreateClient(timeoutSeconds: 2);
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.UnknownOutcome, result.Outcome);
        Assert.False(result.IsDefinitelyUnapplied);
    }

    [Fact]
    public async Task An_unparseable_response_is_an_unknown_outcome()
    {
        // The panel answered, but we cannot tell what it did. That is not the same as a refusal.
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Garbage();

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.UnknownOutcome, result.Outcome);
    }

    [Fact]
    public async Task A_server_error_is_an_unknown_outcome()
    {
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Json(500, new { error = "boom" });

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.UnknownOutcome, result.Outcome);
    }

    [Fact]
    public async Task A_panel_that_answers_and_says_no_is_a_final_rejection()
    {
        // success:false means the panel processed the request and declined it. Retrying would get
        // the same answer, so this must not read as unknown.
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Refused("email already exists");

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.Rejected, result.Outcome);
        Assert.True(result.IsDefinitelyUnapplied);
        Assert.Contains("already exists", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_client_is_reported_as_not_found()
    {
        await using var panel = FakePanel.Start();

        using var client = CreateClient();
        var result = await client.GetClientAsync(EndpointFor(panel), PanelClientEmail.Create());

        Assert.Equal(PanelOutcome.NotFound, result.Outcome);
        Assert.True(result.IsDefinitelyUnapplied);
    }

    // ------------------------------------------------------------------------ addresses ----

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://10.0.0.5:2053")]
    [InlineData("http://192.168.1.1")]
    [InlineData("https://[::1]:2053")]
    public async Task An_internal_address_is_blocked_before_anything_is_sent(string baseUrl)
    {
        // Blocked, not unknown: nothing left the process, so the outcome is certain. An operator
        // aiming a panel at the metadata service would otherwise turn this client into a way to
        // read the host's own credentials.
        using var client = new ThreeXUiClient(
            Options.Create(new ThreeXUiOptions
            {
                AllowInsecurePanelUrls = true,

                // Loopback stays disabled here so [::1] is refused like any other internal address.
                AllowLoopbackPanelUrls = false,
            }),
            NullLogger<ThreeXUiClient>.Instance);

        var result = await client.ListInboundsAsync(new PanelEndpoint(baseUrl, FakePanel.ValidToken));

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
        Assert.True(result.IsDefinitelyUnapplied);
    }

    [Theory]
    [InlineData("panel.example.com")]
    [InlineData("ftp://panel.example.com")]
    [InlineData("https://user:secret@panel.example.com")]
    [InlineData("https://panel.example.com/?x=1")]
    [InlineData("")]
    public async Task A_malformed_panel_address_is_blocked(string baseUrl)
    {
        using var client = CreateClient();
        var result = await client.ListInboundsAsync(new PanelEndpoint(baseUrl, FakePanel.ValidToken));

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task Plain_http_is_refused_unless_a_deployment_opts_in()
    {
        // The token travels on every call, so http would put it on the wire in the clear.
        await using var panel = FakePanel.Start();

        using var strict = new ThreeXUiClient(
            Options.Create(new ThreeXUiOptions { AllowLoopbackPanelUrls = true }),
            NullLogger<ThreeXUiClient>.Instance);

        var result = await strict.ListInboundsAsync(EndpointFor(panel));

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
        Assert.Empty(panel.Requests);
    }

    [Fact]
    public async Task A_panel_mounted_under_a_base_path_is_reached_at_that_prefix()
    {
        // 3x-ui can be served under a webBasePath, and every API path is relative to it. Losing
        // the prefix makes such a panel simply unreachable.
        await using var panel = FakePanel.Start();
        panel.Handlers["secret-path/panel/api/inbounds/list/slim"] = _ => PanelReply.Ok(Array.Empty<object>());

        using var client = CreateClient();
        var result = await client.ListInboundsAsync(
            new PanelEndpoint($"{panel.BaseUrl}/secret-path", FakePanel.ValidToken));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("secret-path/panel/api/inbounds/list/slim", panel.Requests.Single().Path);
    }

    [Fact]
    public async Task A_trailing_slash_on_the_base_address_does_not_double_up()
    {
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/inbounds/list/slim"] = _ => PanelReply.Ok(Array.Empty<object>());

        using var client = CreateClient();
        var result = await client.ListInboundsAsync(
            new PanelEndpoint($"{panel.BaseUrl}/", FakePanel.ValidToken));

        Assert.True(result.IsSuccess, result.Message);
    }

    // -------------------------------------------------------------------- what we send ----

    [Fact]
    public async Task A_create_never_sends_a_uuid_password_or_protocol()
    {
        // The panel generates every per-protocol secret when they are omitted. That is how the
        // portal avoids ever holding a customer's credential — or being the source of a weak one.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();

        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Ok();
        panel.Handlers[$"panel/api/clients/get/{email}"] = _ => PanelReply.Ok(new
        {
            email,
            subId = "abcd1234",
            enable = true,
            totalGB = 53_687_091_200L,
            expiryTime = 1_735_689_600_000L,
            limitIp = 2,
            inboundIds = new[] { 3, 5 },
        });

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest(email));

        Assert.True(result.IsSuccess, result.Message);

        var body = panel.Requests.First(request => request.Path == "panel/api/clients/add").Body;

        foreach (var forbidden in new[] { "\"id\"", "uuid", "password", "\"auth\"", "protocol", "\"flow\"", "subId" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_create_sends_the_allowance_in_bytes_and_the_expiry_in_epoch_milliseconds()
    {
        // The panel's field is called totalGB but its unit is bytes, and timestamps are epoch
        // milliseconds. Getting either wrong hands a customer the wrong quota or the wrong expiry.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Ok();
        panel.Handlers[$"panel/api/clients/get/{email}"] = _ => PanelReply.Ok(new { email, enable = true });

        using var client = CreateClient();
        await client.CreateClientAsync(EndpointFor(panel), SampleRequest(email));

        var body = panel.Requests.First(request => request.Path == "panel/api/clients/add").Json();
        var payload = body.GetProperty("client");

        Assert.Equal(53_687_091_200L, payload.GetProperty("totalGB").GetInt64());
        Assert.Equal(1_735_689_600_000L, payload.GetProperty("expiryTime").GetInt64());
        Assert.Equal([3, 5], body.GetProperty("inboundIds").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public async Task An_absent_expiry_is_sent_as_the_panels_zero_rather_than_as_null()
    {
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Ok();
        panel.Handlers[$"panel/api/clients/get/{email}"] = _ => PanelReply.Ok(new { email, enable = true });

        using var client = CreateClient();
        await client.CreateClientAsync(
            EndpointFor(panel), SampleRequest(email) with { ExpiresAt = null });

        var payload = panel.Requests
            .First(request => request.Path == "panel/api/clients/add")
            .Json()
            .GetProperty("client");

        Assert.Equal(0L, payload.GetProperty("expiryTime").GetInt64());
    }

    [Theory]
    [InlineData("not-our-format")]
    [InlineData("../escape")]
    [InlineData("s-XYZ")]
    [InlineData("member@example.com")]
    [InlineData("")]
    public async Task A_client_identifier_the_portal_did_not_mint_is_refused_before_it_reaches_a_path(string email)
    {
        // This value lands in a URL path on every call, so the shape is checked on the way out
        // even though it comes from our own database.
        await using var panel = FakePanel.Start();

        using var client = CreateClient();
        var result = await client.GetClientAsync(EndpointFor(panel), email);

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
        Assert.Empty(panel.Requests);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, -1)]
    public async Task A_nonsensical_request_is_refused_before_it_reaches_the_panel(
        long allowanceBytes,
        int ipLimit)
    {
        await using var panel = FakePanel.Start();

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest() with
        {
            TotalAllowanceBytes = allowanceBytes,
            IpLimit = ipLimit,
        });

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
        Assert.Empty(panel.Requests);
    }

    [Fact]
    public async Task A_create_with_no_inbound_is_refused()
    {
        await using var panel = FakePanel.Start();

        using var client = CreateClient();
        var result = await client.CreateClientAsync(
            EndpointFor(panel), SampleRequest() with { InboundIds = [] });

        Assert.Equal(PanelOutcome.Blocked, result.Outcome);
        Assert.Empty(panel.Requests);
    }

    // ------------------------------------------------------------------- what we read ----

    [Fact]
    public async Task Traffic_counters_come_back_converted_from_the_panels_units()
    {
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers[$"panel/api/clients/traffic/{email}"] = _ => PanelReply.Ok(new
        {
            email,
            up = 1_048_576L,
            down = 2_097_152L,
            total = 10_737_418_240L,
            enable = true,
            expiryTime = 1_735_689_600_000L,
            lastOnline = 1_735_680_000_000L,
            inboundId = 1,
        });

        using var client = CreateClient();
        var result = await client.GetTrafficAsync(EndpointFor(panel), email);

        Assert.True(result.IsSuccess, result.Message);

        var traffic = result.Value!;

        Assert.Equal(3_145_728L, traffic.UsedBytes);
        Assert.Equal(10_734_272_512L, traffic.RemainingBytes);
        Assert.False(traffic.IsUnlimited);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_735_689_600_000), traffic.ExpiresAt);
    }

    [Fact]
    public async Task A_zero_allowance_reads_as_unlimited_rather_than_as_exhausted()
    {
        // The panel's convention. Reading it as "nothing left" would disable every unlimited
        // service the first time usage was synced.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers[$"panel/api/clients/traffic/{email}"] = _ => PanelReply.Ok(new
        {
            email, up = 500L, down = 500L, total = 0L, enable = true,
            expiryTime = 0L, lastOnline = 0L, inboundId = 1,
        });

        using var client = CreateClient();
        var traffic = (await client.GetTrafficAsync(EndpointFor(panel), email)).Value!;

        Assert.True(traffic.IsUnlimited);
        Assert.Null(traffic.RemainingBytes);
        Assert.Null(traffic.ExpiresAt);
    }

    [Fact]
    public async Task Attach_and_detach_post_the_inbound_ids()
    {
        // These two are what make moving a service between servers safe: attach at the
        // destination, verify, then detach at the source.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers[$"panel/api/clients/{email}/attach"] = _ => PanelReply.Ok();
        panel.Handlers[$"panel/api/clients/{email}/detach"] = _ => PanelReply.Ok();

        using var client = CreateClient();

        Assert.True((await client.AttachAsync(EndpointFor(panel), email, [7, 9])).IsSuccess);
        Assert.True((await client.DetachAsync(EndpointFor(panel), email, [5])).IsSuccess);

        var attach = panel.Requests.First(r => r.Path.EndsWith("/attach", StringComparison.Ordinal));
        var detach = panel.Requests.First(r => r.Path.EndsWith("/detach", StringComparison.Ordinal));

        Assert.Equal([7, 9], attach.Json().GetProperty("inboundIds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([5], detach.Json().GetProperty("inboundIds").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public async Task Deleting_a_client_that_is_already_gone_reports_success()
    {
        // The caller wanted it absent, and it is. Reporting failure would make a retried
        // decommission look broken.
        await using var panel = FakePanel.Start();

        using var client = CreateClient();
        var result = await client.DeleteClientAsync(
            EndpointFor(panel), PanelClientEmail.Create(), keepTraffic: false);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Keeping_traffic_on_delete_is_expressed_in_the_query_string()
    {
        // A migration needs this: the counters are the customer's remaining allowance.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers[$"panel/api/clients/del/{email}"] = _ => PanelReply.Ok();

        using var client = CreateClient();
        await client.DeleteClientAsync(EndpointFor(panel), email, keepTraffic: true);

        Assert.Contains("keepTraffic=1", panel.Requests.Single().Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_links_are_read_from_the_panel_rather_than_assembled_here()
    {
        // Building a vless:// URI ourselves would mean reimplementing the panel's own logic and
        // drifting from it on every panel upgrade.
        await using var panel = FakePanel.Start();

        var email = PanelClientEmail.Create();
        panel.Handlers[$"panel/api/clients/links/{email}"] = _ => PanelReply.Ok(new[]
        {
            "vless://uuid@host.example.com:443?type=tcp#one",
            "vless://uuid@host.example.com:8443?type=ws#two",
        });

        using var client = CreateClient();
        var result = await client.GetClientLinksAsync(EndpointFor(panel), email);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Value!.Count);
    }

    [Fact]
    public async Task The_status_check_reports_whether_xray_is_running()
    {
        await using var panel = FakePanel.Start();

        panel.Handlers["panel/api/server/status"] = _ =>
            PanelReply.Ok(new { xray = new { state = "running", version = "25.1.1" } });

        panel.Handlers["panel/api/server/getXrayVersion"] = _ => PanelReply.Ok("25.1.1");

        using var client = CreateClient();
        var result = await client.GetStatusAsync(EndpointFor(panel));

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Value!.XrayRunning);
        Assert.Equal("25.1.1", result.Value.XrayVersion);
    }

    [Fact]
    public async Task A_status_payload_in_an_unexpected_shape_reports_not_running_rather_than_throwing()
    {
        // The health sweep must survive a panel version that moved a field.
        await using var panel = FakePanel.Start();
        panel.Handlers["panel/api/server/status"] = _ => PanelReply.Ok(new { something = "else" });

        using var client = CreateClient();
        var result = await client.GetStatusAsync(EndpointFor(panel));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.XrayRunning);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_token_cannot_be_carried_elsewhere()
    {
        await using var panel = FakePanel.Start();

        panel.Handlers["panel/api/inbounds/list/slim"] = _ =>
            new PanelReply(302, string.Empty) { };

        using var client = CreateClient();
        var result = await client.ListInboundsAsync(EndpointFor(panel));

        // Not a success, and not treated as a rejection either: an unusable answer.
        Assert.False(result.IsSuccess);
        Assert.Single(panel.Requests);
    }

    [Fact]
    public async Task A_panel_message_is_truncated_before_it_reaches_a_log_or_an_audit_row()
    {
        await using var panel = FakePanel.Start();

        panel.Handlers["panel/api/clients/add"] = _ => PanelReply.Refused(new string('x', 5_000));

        using var client = CreateClient();
        var result = await client.CreateClientAsync(EndpointFor(panel), SampleRequest());

        Assert.Equal(PanelOutcome.Rejected, result.Outcome);
        Assert.True(result.Message!.Length <= 200);
    }
}

public sealed class PanelClientEmailTests
{
    [Fact]
    public void A_minted_identifier_is_valid_and_opaque()
    {
        var email = PanelClientEmail.Create();

        Assert.True(PanelClientEmail.IsValid(email));
        Assert.StartsWith("s-", email, StringComparison.Ordinal);
        Assert.Equal(2 + PanelClientEmail.TokenLength, email.Length);
    }

    [Fact]
    public void Two_minted_identifiers_differ()
    {
        var minted = Enumerable.Range(0, 500).Select(_ => PanelClientEmail.Create()).ToHashSet();

        Assert.Equal(500, minted.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s-")]
    [InlineData("s-SHORT")]
    [InlineData("s-0123456789abcdefg")]
    [InlineData("s-0123456789ABCDEF")]
    [InlineData("x-0123456789abcdef")]
    [InlineData("../0123456789abcdef")]
    [InlineData("s-0123456789abcde/")]
    [InlineData("member@example.com")]
    public void Anything_else_is_refused(string? candidate) =>
        Assert.False(PanelClientEmail.IsValid(candidate));

    [Fact]
    public void The_hint_shows_only_the_tail()
    {
        var hint = IPanelCredentialProtector.HintFor("secret-token-abcd");

        Assert.Equal("····abcd", hint);
        Assert.DoesNotContain("secret", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_token_yields_no_tail_at_all()
    {
        Assert.Equal("····", IPanelCredentialProtector.HintFor("ab"));
        Assert.Equal("····", IPanelCredentialProtector.HintFor(string.Empty));
    }
}
