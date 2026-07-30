using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Billing;
using Sentinel.Domain.Billing;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The wallet's back-office screens over real HTTP.
/// <para>
/// This controller is the entire write surface of the ledger, so what it refuses matters as much as
/// what it does: an ordinary member cannot reach it, a read-only operator cannot move money, and no
/// form field can decide who was credited or who did the crediting.
/// </para>
/// </summary>
public sealed class WalletAdminTests : IClassFixture<WalletEnabledFactory>
{
    private readonly WalletEnabledFactory _factory;

    public WalletAdminTests(WalletEnabledFactory factory) => _factory = factory;

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

    private Task<long> BalanceAsync(Guid userId) =>
        _factory.WithScopeAsync(async services =>
        {
            var wallet = await services.GetRequiredService<ISentinelDbContext>()
                .Wallets.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.UserId == userId);

            return wallet?.BalanceMinorUnits ?? 0;
        });

    // ----------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_another_members_wallet()
    {
        var target = await _factory.CreateMemberAsync("wallet-admin-target");

        using var client = await ClientAsync("wallet-admin-member");

        var response = await client.GetAsync($"/Admin/Wallets/{target}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_read_a_wallet_but_cannot_move_money()
    {
        var target = await _factory.CreateMemberAsync("wallet-support-target");

        using var client = await ClientAsync("wallet-support", RoleNames.Support);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Admin/Wallets/{target}")).StatusCode);

        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Wallets/{target}");

        var response = await client.PostAsync(
            $"/Admin/Wallets/{target}/credit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmountMinorUnits", "1000000"),
            ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, await BalanceAsync(target));
    }

    [Fact]
    public async Task Crediting_without_an_anti_forgery_token_is_refused()
    {
        var target = await _factory.CreateMemberAsync("wallet-csrf-target");

        using var client = await ClientAsync("wallet-csrf-admin", RoleNames.Admin);

        var response = await client.PostAsync(
            $"/Admin/Wallets/{target}/credit",
            new FormUrlEncodedContent([new("AmountMinorUnits", "1000000")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await BalanceAsync(target));
    }

    // ------------------------------------------------------------------ what a form decides ----

    [Fact]
    public async Task The_member_credited_comes_from_the_route_and_not_the_form()
    {
        // Otherwise an operator with write access to one member's wallet could credit any other by
        // editing a hidden field — and the audit row would name the wrong account.
        var target = await _factory.CreateMemberAsync("wallet-route-target");
        var other = await _factory.CreateMemberAsync("wallet-route-other");

        using var client = await ClientAsync("wallet-route-admin", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Wallets/{target}");

        await client.PostAsync(
            $"/Admin/Wallets/{target}/credit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmountMinorUnits", "500000"),

                // An invented field naming somebody else.
                new("UserId", other.ToString()),
            ]));

        Assert.Equal(500_000, await BalanceAsync(target));
        Assert.Equal(0, await BalanceAsync(other));
    }

    [Fact]
    public async Task The_operator_recorded_is_the_signed_in_one_and_not_a_form_value()
    {
        var target = await _factory.CreateMemberAsync("wallet-actor-target");
        var innocent = await _factory.CreateMemberAsync("wallet-actor-innocent");

        var adminId = await _factory.CreateMemberAsync("wallet-actor-admin");
        await _factory.AddToRoleAsync("wallet-actor-admin", RoleNames.Admin);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("wallet-actor-admin", PortalTestData.MemberPassword);

        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Wallets/{target}");

        await client.PostAsync(
            $"/Admin/Wallets/{target}/credit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmountMinorUnits", "10000"),
                new("PerformedByUserId", innocent.ToString()),
            ]));

        client.Dispose();

        var entry = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .FirstAsync(row => row.UserId == target));

        Assert.Equal(adminId, entry.PerformedByUserId);
        Assert.NotEqual(innocent, entry.PerformedByUserId);
    }

    // ------------------------------------------------------------------------------ actions ----

    [Fact]
    public async Task An_administrator_credits_debits_and_reverses()
    {
        var target = await _factory.CreateMemberAsync("wallet-admin-flow");

        using var client = await ClientAsync("wallet-admin-flow-admin", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync($"/Admin/Wallets/{target}");

        await client.PostAsync(
            $"/Admin/Wallets/{target}/credit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmountMinorUnits", "900000"),
                new("Description", "opening float"),
            ]));

        Assert.Equal(900_000, await BalanceAsync(target));

        await client.PostAsync(
            $"/Admin/Wallets/{target}/debit",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmountMinorUnits", "400000"),
            ]));

        Assert.Equal(500_000, await BalanceAsync(target));

        var debitId = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .Where(row => row.UserId == target && row.Kind == WalletTransactionKind.OperatorDebit)
                .Select(row => row.Id)
                .FirstAsync());

        await client.PostAsync(
            $"/Admin/Wallets/{target}/reverse/{debitId}",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        // Back where it was, by appending the opposite — the debit is still in the ledger.
        Assert.Equal(900_000, await BalanceAsync(target));

        var kinds = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .Where(row => row.UserId == target)
                .Select(row => row.Kind)
                .ToListAsync());

        Assert.Equal(3, kinds.Count);
        Assert.Contains(WalletTransactionKind.OperatorDebit, kinds);
        Assert.Contains(WalletTransactionKind.Reversal, kinds);
    }

    [Fact]
    public async Task The_page_never_shows_a_raw_localisation_key()
    {
        var target = await _factory.CreateMemberAsync("wallet-keys-target");

        using var client = await ClientAsync("wallet-keys-admin", RoleNames.Admin);

        var page = await client.GetStringAsync($"/Admin/Wallets/{target}");

        Assert.DoesNotContain("admin.wallet.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("walletKind.", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- the flag off ----

    [Fact]
    public async Task With_the_wallet_switched_off_the_admin_screens_are_not_there()
    {
        await using var factory = new SentinelWebApplicationFactory();

        var target = await factory.CreateMemberAsync("wallet-off-target");

        await factory.CreateMemberAsync("wallet-off-admin");
        await factory.AddToRoleAsync("wallet-off-admin", RoleNames.Admin);

        using var client = factory.CreateNonRedirectingClient();
        await client.SignInAsync("wallet-off-admin", PortalTestData.MemberPassword);

        var response = await client.GetAsync($"/Admin/Wallets/{target}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_the_wallet_switched_off_a_member_has_no_wallet_page()
    {
        await using var factory = new SentinelWebApplicationFactory();

        await factory.CreateMemberAsync("wallet-off-member");

        using var client = factory.CreateNonRedirectingClient();
        await client.SignInAsync("wallet-off-member", PortalTestData.MemberPassword);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/wallet")).StatusCode);
    }

    [Fact]
    public async Task A_member_sees_only_their_own_statement()
    {
        // There is no parameter on the member's page that could name another account — the owner
        // comes from the signed-in claim. This asserts the page reflects that.
        var mine = await _factory.CreateMemberAsync("wallet-mine");
        var theirs = await _factory.CreateMemberAsync("wallet-theirs");

        await _factory.WithScopeAsync(async services =>
        {
            var wallet = services.GetRequiredService<IWalletService>();

            await wallet.CreditAsync(
                new AdjustWalletRequest(mine, 111_111, "mine", null), mine);

            await wallet.CreditAsync(
                new AdjustWalletRequest(theirs, 999_999, "theirs", null), theirs);
        });

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("wallet-mine", PortalTestData.MemberPassword);

        var page = await client.GetStringAsync("/wallet");

        Assert.Contains("111", page, StringComparison.Ordinal);
        Assert.DoesNotContain("999,999", page, StringComparison.Ordinal);
        Assert.DoesNotContain("theirs", page, StringComparison.Ordinal);
    }
}
