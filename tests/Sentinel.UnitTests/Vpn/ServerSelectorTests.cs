using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Provisioning;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// Where a customer's service lands. Pure, so every placement rule is testable — and it needs to be,
/// because a wrong choice here means a provisioning failure the customer meets rather than the
/// operator.
/// </summary>
public sealed class ServerSelectorTests
{
    private static ServerCandidate Server(
        string key,
        string country = "DE",
        VpnServerStatus status = VpnServerStatus.Active,
        VpnServerHealth health = VpnServerHealth.Healthy,
        int max = 100,
        int reserved = 0,
        int priority = 100,
        int inbounds = 1) =>
        new(Guid.NewGuid(), key, country, status, health, max, reserved, priority, inbounds);

    // -------------------------------------------------------------------------- happy path ----

    [Fact]
    public void The_only_usable_server_is_chosen()
    {
        var only = Server("de-1");

        var result = ServerSelector.Select([only], "DE");

        Assert.True(result.IsSuccess);
        Assert.Equal(only.Key, result.Server!.Key);
    }

    [Fact]
    public void A_null_country_accepts_any_location()
    {
        // A plan with no country asks for anywhere the portal can deliver.
        var result = ServerSelector.Select([Server("nl-1", country: "NL")], countryCode: null);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void The_country_match_is_case_insensitive()
    {
        // Plans and servers both store upper case, but a hand-entered row should not be excluded on
        // a capital letter.
        var result = ServerSelector.Select([Server("de-1", country: "de")], "DE");

        Assert.True(result.IsSuccess);
    }

    // ---------------------------------------------------------------------------- ordering ----

    [Fact]
    public void The_operators_priority_comes_first()
    {
        var result = ServerSelector.Select(
            [
                Server("de-busy", priority: 10, reserved: 90),
                Server("de-idle", priority: 50, reserved: 0),
            ],
            "DE");

        // Priority is an explicit preference and outranks load: an operator setting it low means
        // "prefer this one", including when it is fuller.
        Assert.Equal("de-busy", result.Server!.Key);
    }

    [Fact]
    public void At_equal_priority_the_emptiest_server_wins()
    {
        var result = ServerSelector.Select(
            [
                Server("de-full", reserved: 95),
                Server("de-half", reserved: 50),
                Server("de-empty", reserved: 5),
            ],
            "DE");

        Assert.Equal("de-empty", result.Server!.Key);
    }

    [Fact]
    public void Load_is_compared_as_a_fraction_not_a_count()
    {
        // 40 of 50 is fuller than 60 of 200, even though the raw count is lower.
        var result = ServerSelector.Select(
            [
                Server("de-small", max: 50, reserved: 40),
                Server("de-large", max: 200, reserved: 60),
            ],
            "DE");

        Assert.Equal("de-large", result.Server!.Key);
    }

    [Fact]
    public void The_choice_is_deterministic_between_identical_servers()
    {
        // Without a final tie-break the pick would follow whatever order the database returned, and
        // a test would pass or fail by luck.
        var candidates = new[] { Server("de-b"), Server("de-a"), Server("de-c") };

        var first = ServerSelector.Select(candidates, "DE");
        var reversed = ServerSelector.Select(candidates.Reverse().ToList(), "DE");

        Assert.Equal("de-a", first.Server!.Key);
        Assert.Equal(first.Server.Key, reversed.Server!.Key);
    }

    // ---------------------------------------------------------------------------- refusals ----

    [Fact]
    public void No_server_at_all_is_reported_as_no_server_in_country() =>
        Assert.Equal(
            SelectionOutcome.NoServerInCountry,
            ServerSelector.Select([], "DE").Outcome);

    [Fact]
    public void A_server_in_another_country_does_not_count() =>
        Assert.Equal(
            SelectionOutcome.NoServerInCountry,
            ServerSelector.Select([Server("nl-1", country: "NL")], "DE").Outcome);

    [Theory]
    [InlineData(VpnServerStatus.Unverified)]
    [InlineData(VpnServerStatus.Disabled)]
    [InlineData(VpnServerStatus.Unreachable)]
    public void A_server_that_is_not_active_is_never_selected(VpnServerStatus status) =>
        Assert.Equal(
            SelectionOutcome.NoHealthyServer,
            ServerSelector.Select([Server("de-1", status: status)], "DE").Outcome);

    [Fact]
    public void A_draining_server_takes_no_new_service()
    {
        // The point of draining: existing services keep working, new ones go elsewhere.
        Assert.Equal(
            SelectionOutcome.NoHealthyServer,
            ServerSelector.Select([Server("de-1", status: VpnServerStatus.Draining)], "DE").Outcome);
    }

    [Fact]
    public void An_unreachable_health_reading_excludes_a_server() =>
        Assert.Equal(
            SelectionOutcome.NoHealthyServer,
            ServerSelector.Select(
                [Server("de-1", health: VpnServerHealth.Unreachable)], "DE").Outcome);

    [Fact]
    public void A_degraded_server_is_still_selectable_as_a_last_resort()
    {
        // Degraded means the panel answers but Xray is not running — recoverable, and often the only
        // server in a country. Excluding it would refuse service where a retry would succeed.
        var result = ServerSelector.Select(
            [Server("de-1", health: VpnServerHealth.Degraded)], "DE");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void A_full_server_is_reported_as_no_capacity() =>
        Assert.Equal(
            SelectionOutcome.NoCapacity,
            ServerSelector.Select([Server("de-1", max: 10, reserved: 10)], "DE").Outcome);

    [Fact]
    public void An_over_subscribed_server_is_also_full()
    {
        // Reserved above the ceiling should not compute as negative room.
        Assert.Equal(
            SelectionOutcome.NoCapacity,
            ServerSelector.Select([Server("de-1", max: 10, reserved: 15)], "DE").Outcome);
    }

    [Fact]
    public void A_server_with_no_ceiling_is_treated_as_full()
    {
        // Zero is how an operator says "not ready", so it must not read as unlimited.
        Assert.Equal(
            SelectionOutcome.NoCapacity,
            ServerSelector.Select([Server("de-1", max: 0)], "DE").Outcome);
    }

    [Fact]
    public void A_server_with_no_allowlisted_inbound_cannot_be_used() =>
        Assert.Equal(
            SelectionOutcome.NoUsableInbound,
            ServerSelector.Select([Server("de-1", inbounds: 0)], "DE").Outcome);

    [Fact]
    public void The_refusals_are_reported_at_the_stage_they_happen()
    {
        // Each outcome needs a different response — add a server, finish configuring one, or
        // investigate — so collapsing them into one "unavailable" would lose the actionable part.
        Assert.Equal(
            SelectionOutcome.NoHealthyServer,
            ServerSelector.Select(
                [
                    Server("de-disabled", status: VpnServerStatus.Disabled, inbounds: 0, max: 0),
                ],
                "DE").Outcome);

        Assert.Equal(
            SelectionOutcome.NoCapacity,
            ServerSelector.Select([Server("de-full", max: 5, reserved: 5, inbounds: 0)], "DE").Outcome);
    }

    [Fact]
    public void A_usable_server_is_found_among_unusable_ones()
    {
        var result = ServerSelector.Select(
            [
                Server("de-disabled", status: VpnServerStatus.Disabled),
                Server("de-full", max: 10, reserved: 10),
                Server("de-noinbound", inbounds: 0),
                Server("de-good"),
                Server("nl-good", country: "NL"),
            ],
            "DE");

        Assert.True(result.IsSuccess);
        Assert.Equal("de-good", result.Server!.Key);
    }
}
