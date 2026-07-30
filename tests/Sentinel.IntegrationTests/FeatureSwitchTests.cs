using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Features;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Turning a feature on and off from the back office.
/// <para>
/// The claim being tested is not that a row is written — it is that the switch <em>closes and opens
/// the endpoints</em>. A screen that records an operator's intention without changing what members
/// can reach is worse than no screen, because it looks like it worked.
/// </para>
/// </summary>
public sealed class FeatureSwitchTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public FeatureSwitchTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync(string userName, string? role = null)
    {
        await _factory.CreateMemberAsync(userName);

        if (role is not null)
        {
            await _factory.AddToRoleAsync(userName, role);
        }

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);

        return client;
    }

    private Guid _operatorId;

    /// <summary>
    /// A real account to attribute the change to. Every switch is audited, and the audit row's
    /// actor is a foreign key — as it should be, since in production it always comes from a
    /// signed-in principal.
    /// </summary>
    private async Task<Guid> OperatorAsync()
    {
        if (_operatorId == Guid.Empty)
        {
            _operatorId = await _factory.CreateMemberAsync("switch-operator");
        }

        return _operatorId;
    }

    private async Task SetAsync(string feature, bool? enabled)
    {
        var actor = await OperatorAsync();

        await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IFeatureAdminService>()
                .SetAsync(feature, enabled, actor);

            Assert.True(result.Succeeded, result.ErrorKey);
        });
    }

    private Task<bool> IsOnAsync(string feature) =>
        _factory.WithScopeAsync(services =>
            Task.FromResult(services.GetRequiredService<IFeatureGate>().IsEnabled(feature)));

    // -------------------------------------------------------------------------- the switch ----

    [Fact]
    public async Task A_switch_overrides_what_the_deployment_configured()
    {
        // The wallet ships off. Nothing in configuration changes here — the override is a second
        // layer the gate consults first.
        Assert.False(await IsOnAsync(FeatureNames.Wallet));

        await SetAsync(FeatureNames.Wallet, true);

        try
        {
            Assert.True(await IsOnAsync(FeatureNames.Wallet));
        }
        finally
        {
            await SetAsync(FeatureNames.Wallet, null);
        }

        // Removing the override hands the feature back to the deployment rather than forcing it off.
        Assert.False(await IsOnAsync(FeatureNames.Wallet));
    }

    [Fact]
    public async Task Turning_a_feature_on_opens_its_endpoints()
    {
        // The whole point. With the wallet off a member's credit page is a 404; with it on the
        // same URL answers — no restart, no redeploy.
        using var client = await ClientAsync("switch-endpoint-member");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/wallet")).StatusCode);

        await SetAsync(FeatureNames.Wallet, true);

        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wallet")).StatusCode);
        }
        finally
        {
            await SetAsync(FeatureNames.Wallet, null);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/wallet")).StatusCode);
    }

    [Fact]
    public async Task A_feature_this_build_does_not_have_is_refused()
    {
        // Otherwise a form post would create rows for invented names, and the table would collect
        // switches that control nothing.
        var actor = await OperatorAsync();

        var result = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IFeatureAdminService>()
                .SetAsync("ThereIsNoSuchFeature", true, actor));

        Assert.False(result.Succeeded);
        Assert.Equal(FeatureErrors.UnknownFeature, result.ErrorKey);

        var rows = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .FeatureOverrides.AsNoTracking()
                .CountAsync(entry => entry.Name == "ThereIsNoSuchFeature"));

        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task The_list_says_where_each_value_came_from()
    {
        await SetAsync(FeatureNames.BetaProducts, false);

        try
        {
            var states = await _factory.WithScopeAsync(services =>
                services.GetRequiredService<IFeatureAdminService>().ListAsync());

            var beta = states.Single(state => state.Name == FeatureNames.BetaProducts);

            Assert.Equal(FeatureSource.Override, beta.Source);
            Assert.False(beta.IsEnabled);

            // Configuration still says true, and the screen shows both so an operator can see that
            // this is somebody's decision rather than how the portal ships.
            Assert.True(beta.ConfiguredValue);
            Assert.True(beta.DivergesFromConfiguration);

            var untouched = states.Single(state => state.Name == FeatureNames.ProductLibrary);

            Assert.Equal(FeatureSource.Configuration, untouched.Source);
            Assert.False(untouched.DivergesFromConfiguration);
        }
        finally
        {
            await SetAsync(FeatureNames.BetaProducts, null);
        }
    }

    // ----------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_switchboard()
    {
        using var client = await ClientAsync("switch-member");

        var response = await client.GetAsync("/Admin/Features");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ordinary_administrator_cannot_move_a_switch()
    {
        // System administration, not back-office write. A feature switch changes what every member
        // sees at once, and the financial ones let credit move.
        using var client = await ClientAsync("switch-plain-admin", RoleNames.Admin);

        var response = await client.GetAsync("/Admin/Features");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Moving_a_switch_without_an_anti_forgery_token_is_refused()
    {
        using var client = await ClientAsync("switch-csrf", RoleNames.SuperAdmin);

        var response = await client.PostAsync(
            $"/Admin/Features/{FeatureNames.Wallet}",
            new FormUrlEncodedContent([new("enabled", "true")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await IsOnAsync(FeatureNames.Wallet));
    }

    // ------------------------------------------------------------------------ the one button ----

    [Fact]
    public async Task Opening_the_vpn_flow_turns_on_all_three_switches()
    {
        // Each is useless without the others: credit nobody can spend, or an order button with
        // nothing behind it. The button exists so an operator does not have to know that.
        using var client = await ClientAsync("switch-vpn-admin", RoleNames.SuperAdmin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/Features");

        try
        {
            var response = await client.PostAsync(
                "/Admin/Features/open-vpn",
                new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            Assert.True(await IsOnAsync(FeatureNames.VpnSelfService));
            Assert.True(await IsOnAsync(FeatureNames.Wallet));
            Assert.True(await IsOnAsync(FeatureNames.Purchases));

            // And a member can now reach their credit, which is the observable consequence.
            using var member = await ClientAsync("switch-vpn-member");

            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/wallet")).StatusCode);
        }
        finally
        {
            await SetAsync(FeatureNames.VpnSelfService, null);
            await SetAsync(FeatureNames.Wallet, null);
            await SetAsync(FeatureNames.Purchases, null);
        }
    }

    [Fact]
    public async Task Every_switch_change_is_audited()
    {
        // Turning a feature on changes what every member sees. That is the sort of thing somebody
        // asks about a week later, so it has to be answerable.
        // The audit rows are keyed by the feature's own name, so they read as a history of that
        // switch rather than of the table.
        await SetAsync(FeatureNames.ProductDocumentation, false);

        try
        {
            var actions = await _factory.RecentAuditActionsAsync(FeatureNames.ProductDocumentation);

            Assert.Contains(FeatureAuditActions.Changed, actions);
        }
        finally
        {
            await SetAsync(FeatureNames.ProductDocumentation, null);
        }

        var afterReset = await _factory.RecentAuditActionsAsync(FeatureNames.ProductDocumentation);

        Assert.Contains(FeatureAuditActions.Reset, afterReset);
    }
}
