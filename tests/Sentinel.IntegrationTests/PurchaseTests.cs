using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Billing;
using Sentinel.Domain.Billing;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Purchasing;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Buying a plan with wallet credit.
/// <para>
/// The properties under test: the price comes from the catalogue and never from the request, a
/// failed purchase charges nothing, a repeated one charges once, and — the boundary this whole
/// feature is built around — no member-facing path anywhere increases a balance.
/// </para>
/// </summary>
public sealed class PurchaseTests : IClassFixture<WalletEnabledFactory>
{
    private const string PanelToken = "integration-only-panel-token-33445";

    private readonly WalletEnabledFactory _factory;

    public PurchaseTests(WalletEnabledFactory factory) => _factory = factory;

    // ---------------------------------------------------------------------------- fixtures ----

    private Task<Guid> CreateServerAsync(string key, string country, int maxClients = 10) =>
        _factory.WithScopeAsync(async services =>
        {
            var created = await services.GetRequiredService<IVpnServerAdminService>()
                .SaveAsync(null, new VpnServerSaveRequest(
                    key, $"سرور {key}", $"Server {key}", country,
                    $"https://{key}.panel.example.com:2053", PanelToken,
                    VpnServerStatus.Active, maxClients, 100, null, null));

            Assert.True(created.Succeeded, created.ErrorKey);

            var db = services.GetRequiredService<IVpnDbContext>();

            var server = await db.VpnServers.FirstAsync(candidate => candidate.Id == created.Value);
            server.Status = VpnServerStatus.Active;
            server.Health = VpnServerHealth.Healthy;

            db.ServerInboundProfiles.Add(new ServerInboundProfile
            {
                Id = Guid.NewGuid(),
                ServerId = created.Value,
                InboundId = 1,
                Label = "vless:443",
                Protocol = "vless",
                IsEnabled = true,
            });

            await db.SaveChangesAsync();

            return created.Value;
        });

    private Task<Guid> CreatePlanAsync(
        Guid productId,
        string key,
        string country,
        long price = 2_500_000,
        bool purchasable = true) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IServicePlanAdminService>()
                .SaveAsync(null, new ServicePlanSaveRequest(
                    key, productId, $"پلن {key}", $"Plan {key}", null, null,
                    53_687_091_200L, 30, 2, price, "IRR",
                    true, purchasable, country, 100, false, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private static int _countrySequence;

    /// <summary>A country no other test in this class uses, so selection cannot stray.</summary>
    private static string NextCountry()
    {
        var n = System.Threading.Interlocked.Increment(ref _countrySequence);

        return $"{(char)('K' + (n / 26) % 12)}{(char)('A' + n % 26)}";
    }

    private Task<Guid> CreditAsync(Guid userId, long amount) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IWalletService>().CreditAsync(
                new AdjustWalletRequest(userId, amount, "test float", null), userId);

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private Task<long> BalanceAsync(Guid userId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .Wallets.AsNoTracking()
                .Where(wallet => wallet.UserId == userId)
                .Select(wallet => wallet.BalanceMinorUnits)
                .FirstAsync());

    private async Task<(Guid UserId, Guid ProductId, Guid PlanId, string ProductKey)> ShopAsync(
        string prefix,
        long price = 2_500_000,
        bool purchasable = true)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-member");
        var productKey = $"{prefix}-product";

        var productId = await _factory.CreateProductAsync(
            productKey, capabilities: ProductCapability.HasConfigurations);

        await _factory.GrantAsync(userId, productId);

        var country = NextCountry();
        await CreateServerAsync($"{prefix}-server", country);

        var planId = await CreatePlanAsync(productId, $"{prefix}-plan", country, price, purchasable);

        return (userId, productId, planId, productKey);
    }

    private Task<Sentinel.Application.Common.OperationResult<PurchaseResult>> BuyAsync(
        Guid userId,
        Guid planId,
        string? key = null) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IPlanPurchaseService>()
                .PurchaseAsync(userId, new PurchasePlanRequest(planId, key)));

    // ------------------------------------------------------------------------- happy path ----

    [Fact]
    public async Task Buying_a_plan_debits_the_price_and_creates_the_service()
    {
        var shop = await ShopAsync("buy-happy");

        await CreditAsync(shop.UserId, 5_000_000);

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.True(result.Succeeded, result.ErrorKey);
        Assert.Equal(2_500_000, result.Value!.ChargedMinorUnits);
        Assert.Equal(2_500_000, result.Value.RemainingBalanceMinorUnits);
        Assert.Equal(2_500_000, await BalanceAsync(shop.UserId));

        var service = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(candidate => candidate.Id == result.Value.ServiceId));

        Assert.Equal(shop.UserId, service.UserId);
        Assert.Equal(CustomerServiceStatus.Pending, service.Status);
    }

    [Fact]
    public async Task The_ledger_entry_points_at_what_it_paid_for()
    {
        // So a support question — "what is this charge?" — is answered from the row itself.
        var shop = await ShopAsync("buy-linked");

        await CreditAsync(shop.UserId, 5_000_000);

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        var entry = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .FirstAsync(row => row.Id == result.Value!.TransactionId));

        Assert.Equal(WalletTransactionKind.Purchase, entry.Kind);
        Assert.Equal(WalletEntryDirection.Debit, entry.Direction);
        Assert.Equal(result.Value!.ServiceId, entry.RelatedServiceId);

        // A member's own purchase has no operator behind it.
        Assert.Null(entry.PerformedByUserId);
    }

    // ------------------------------------------------------------------- nothing by halves ----

    [Fact]
    public async Task A_member_without_enough_credit_is_charged_nothing_and_gets_no_service()
    {
        var shop = await ShopAsync("buy-poor");

        await CreditAsync(shop.UserId, 1_000);

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.False(result.Succeeded);
        Assert.Equal(PurchaseErrors.InsufficientFunds, result.ErrorKey);
        Assert.Equal(1_000, await BalanceAsync(shop.UserId));

        var services = await _factory.WithScopeAsync(sp =>
            sp.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .CountAsync(service => service.UserId == shop.UserId));

        Assert.Equal(0, services);
    }

    [Fact]
    public async Task When_no_server_has_room_the_member_is_not_charged()
    {
        // The case the shared transaction exists for: the debit is recorded, the service cannot be
        // created, and nothing is committed. A charge for a service that was never made is exactly
        // what a separate wallet transaction would have produced.
        var shop = await ShopAsync("buy-nocapacity");

        await CreditAsync(shop.UserId, 5_000_000);

        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<IVpnDbContext>();

            foreach (var server in await db.VpnServers.ToListAsync())
            {
                server.ReservedClients = server.MaxClients;
            }

            await db.SaveChangesAsync();
        });

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.False(result.Succeeded);
        Assert.Equal(PurchaseErrors.NoCapacity, result.ErrorKey);

        // Untouched, and no ledger entry at all.
        Assert.Equal(5_000_000, await BalanceAsync(shop.UserId));

        var entries = await _factory.WithScopeAsync(sp =>
            sp.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .CountAsync(entry => entry.UserId == shop.UserId
                                     && entry.Kind == WalletTransactionKind.Purchase));

        Assert.Equal(0, entries);
    }

    // ------------------------------------------------------------------------ idempotency ----

    [Fact]
    public async Task The_same_idempotency_key_buys_once()
    {
        var shop = await ShopAsync("buy-idempotent");

        await CreditAsync(shop.UserId, 10_000_000);

        var first = await BuyAsync(shop.UserId, shop.PlanId, "order-771");
        var second = await BuyAsync(shop.UserId, shop.PlanId, "order-771");

        Assert.True(first.Succeeded, first.ErrorKey);
        Assert.True(second.Succeeded, second.ErrorKey);

        Assert.Equal(first.Value!.ServiceId, second.Value!.ServiceId);
        Assert.Equal(first.Value.TransactionId, second.Value.TransactionId);
        Assert.Equal(7_500_000, await BalanceAsync(shop.UserId));
    }

    [Fact]
    public async Task A_member_may_deliberately_buy_the_same_plan_twice()
    {
        // Different keys are different orders. Idempotency must not turn into "one of these ever".
        var shop = await ShopAsync("buy-twice");

        await CreditAsync(shop.UserId, 10_000_000);

        var first = await BuyAsync(shop.UserId, shop.PlanId, "order-a");
        var second = await BuyAsync(shop.UserId, shop.PlanId, "order-b");

        Assert.True(second.Succeeded, second.ErrorKey);
        Assert.NotEqual(first.Value!.ServiceId, second.Value!.ServiceId);
        Assert.Equal(5_000_000, await BalanceAsync(shop.UserId));
    }

    // ------------------------------------------------------------ the price is not the caller's ----

    [Fact]
    public async Task The_price_charged_is_the_catalogue_price_even_after_it_changes()
    {
        // Re-read inside the transaction. A page rendered before a price change must not buy at the
        // old price, and a page rendered after must not buy at the new one until it is really set.
        var shop = await ShopAsync("buy-reprice", price: 1_000_000);

        await CreditAsync(shop.UserId, 10_000_000);

        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<IVpnDbContext>();

            var plan = await db.ServicePlans.FirstAsync(candidate => candidate.Id == shop.PlanId);
            plan.PriceMinorUnits = 3_000_000;

            await db.SaveChangesAsync();
        });

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.Equal(3_000_000, result.Value!.ChargedMinorUnits);
        Assert.Equal(7_000_000, await BalanceAsync(shop.UserId));
    }

    [Fact]
    public async Task A_plan_withdrawn_from_sale_cannot_be_bought()
    {
        var shop = await ShopAsync("buy-notforsale", purchasable: false);

        await CreditAsync(shop.UserId, 10_000_000);

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.False(result.Succeeded);
        Assert.Equal(PurchaseErrors.NotPurchasable, result.ErrorKey);
        Assert.Equal(10_000_000, await BalanceAsync(shop.UserId));
    }

    [Fact]
    public async Task A_member_outside_the_audience_cannot_buy_it()
    {
        // Re-evaluated at purchase, not taken from the page that drew the button.
        var shop = await ShopAsync("buy-audience");

        await CreditAsync(shop.UserId, 10_000_000);

        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<IVpnDbContext>();

            db.PlanAudienceRules.Add(new PlanAudienceRule
            {
                Id = Guid.NewGuid(),
                PlanId = shop.PlanId,
                Effect = AudienceEffect.Allow,
                Kind = AudienceRuleKind.MinimumTier,
                Tier = MembershipTier.Elite,
            });

            await db.SaveChangesAsync();
        });

        var result = await BuyAsync(shop.UserId, shop.PlanId);

        Assert.False(result.Succeeded);
        Assert.Equal(PurchaseErrors.NotInAudience, result.ErrorKey);
        Assert.Equal(10_000_000, await BalanceAsync(shop.UserId));
    }

    // ---------------------------------------------------------------------------- the flags ----

    [Fact]
    public async Task With_purchases_switched_off_the_endpoint_is_not_there()
    {
        // A feature that is off must be indistinguishable from one that was never built — 404, not
        // 403, and enforced on the endpoint rather than only in the view.
        await using var factory = new SentinelWebApplicationFactory();

        var userId = await factory.CreateMemberAsync("buy-flag-off");
        var productId = await factory.CreateProductAsync("buy-flag-off-product");
        await factory.GrantAsync(userId, productId);

        using var client = factory.CreateNonRedirectingClient();
        await client.SignInAsync("buy-flag-off", PortalTestData.MemberPassword);

        // With a valid anti-forgery token, so the 404 is the feature gate's answer and not a
        // rejected token's. The gate runs first precisely so it is the one that answers.
        var token = await client.GetAntiForgeryTokenAsync("/vpn/buy-flag-off-product");

        var response = await client.PostAsync(
            "/vpn/buy-flag-off-product/purchase",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("planId", Guid.NewGuid().ToString()),
            ]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_switched_off_purchase_endpoint_answers_as_a_switched_on_one_would()
    {
        // The claim a feature flag makes is indistinguishability, and this is the pair that tests
        // it: the same malformed request against a portal with purchasing on and one with it off.
        // Both must answer identically, or the response itself is a map of what is unreleased.
        //
        // (Without an anti-forgery token both answer 400, which is what makes them identical. The
        // gate's own 404 is what a *well-formed* request meets — see the test above.)
        await using var off = new SentinelWebApplicationFactory();
        await using var on = new WalletEnabledFactory();

        async Task<HttpStatusCode> ProbeAsync(SentinelWebApplicationFactory factory, string name)
        {
            var userId = await factory.CreateMemberAsync(name);
            var productId = await factory.CreateProductAsync($"{name}-product");
            await factory.GrantAsync(userId, productId);

            using var client = factory.CreateNonRedirectingClient();
            await client.SignInAsync(name, PortalTestData.MemberPassword);

            var response = await client.PostAsync(
                $"/vpn/{name}-product/purchase",
                new FormUrlEncodedContent([new("planId", Guid.NewGuid().ToString())]));

            return response.StatusCode;
        }

        Assert.Equal(
            await ProbeAsync(on, "buy-probe-on"),
            await ProbeAsync(off, "buy-probe-off"));
    }

    [Fact]
    public async Task With_the_flags_off_the_service_refuses_even_when_called_directly()
    {
        // The gate is in the service too, so a caller that never passes through MVC is refused.
        await using var factory = new SentinelWebApplicationFactory();

        var userId = await factory.CreateMemberAsync("buy-service-flag-off");

        var result = await factory.WithScopeAsync(services =>
            services.GetRequiredService<IPlanPurchaseService>()
                .PurchaseAsync(userId, new PurchasePlanRequest(Guid.NewGuid(), null)));

        Assert.False(result.Succeeded);
        Assert.Equal(PurchaseErrors.Disabled, result.ErrorKey);
    }

    // ----------------------------------------------------- credit is operator-only, by design ----

    [Fact]
    public void No_member_facing_endpoint_can_increase_a_balance()
    {
        // The boundary this whole feature rests on: credit enters one way, an operator puts it
        // there. Asserted structurally rather than by trying URLs, so a new controller added later
        // is caught by this test rather than by an incident.
        //
        // A "member-facing" action is any non-GET action on a controller outside the Admin area.
        var assembly = typeof(Program).Assembly;

        var offenders = new List<string>();

        foreach (var controller in assembly.GetTypes()
                     .Where(type => typeof(Controller).IsAssignableFrom(type) && !type.IsAbstract))
        {
            var area = controller.GetCustomAttribute<AreaAttribute>()?.RouteValue;

            if (string.Equals(area, "Admin", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var action in controller.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isWrite = action.GetCustomAttributes<HttpPostAttribute>().Any()
                              || action.GetCustomAttributes<HttpPutAttribute>().Any()
                              || action.GetCustomAttributes<HttpPatchAttribute>().Any();

                if (!isWrite)
                {
                    continue;
                }

                // The purchase endpoint spends credit; that is the only wallet-touching write a
                // member may make, and it can only ever reduce a balance.
                var name = $"{controller.Name}.{action.Name}";

                if (name.Contains("credit", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("topup", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("top_up", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("deposit", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("recharge", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("payment", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gateway", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("callback", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(name);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Member-facing endpoints that look like a balance-increase path: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_member_wallet_controller_is_read_only()
    {
        // Stated in that controller's own documentation, and enforced here. Adding a POST to it is
        // changing a security boundary, and this test is what makes that a deliberate act.
        var controller = typeof(Program).Assembly
            .GetTypes()
            .Single(type => type.Name == "WalletController"
                            && type.Namespace == "Sentinel.Web.Controllers");

        var writes = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(action => action.GetCustomAttributes<HttpPostAttribute>().Any()
                             || action.GetCustomAttributes<HttpPutAttribute>().Any()
                             || action.GetCustomAttributes<HttpPatchAttribute>().Any()
                             || action.GetCustomAttributes<HttpDeleteAttribute>().Any())
            .Select(action => action.Name)
            .ToList();

        Assert.True(
            writes.Count == 0,
            "The member wallet controller must stay read-only; found: " + string.Join(", ", writes));
    }
}
