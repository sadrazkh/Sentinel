using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Billing;
using Sentinel.Domain.Billing;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>A host with the credit ledger switched on. It ships off, so a test has to ask for it.</summary>
public sealed class WalletEnabledFactory : SentinelWebApplicationFactory
{
    protected override void ConfigureTestSettings(IWebHostBuilder builder)
    {
        builder.UseSetting("Features:WalletEnabled", "true");
        builder.UseSetting("Features:PurchasesEnabled", "true");
    }
}

/// <summary>
/// The credit ledger.
/// <para>
/// Three properties are worth more than the rest here, and most of these tests exist for one of
/// them: a balance never goes negative, an entry is never edited or deleted, and a repeated request
/// applies once. Everything else about a wallet can be rebuilt from the ledger; those three are what
/// make the ledger worth rebuilding from.
/// </para>
/// </summary>
public sealed class WalletLedgerTests : IClassFixture<WalletEnabledFactory>
{
    private readonly WalletEnabledFactory _factory;

    public WalletLedgerTests(WalletEnabledFactory factory) => _factory = factory;

    private Task<T> WithWalletAsync<T>(Func<IWalletService, Task<T>> action) =>
        _factory.WithScopeAsync(services => action(services.GetRequiredService<IWalletService>()));

    private Task<long> BalanceAsync(Guid userId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .Wallets.AsNoTracking()
                .Where(wallet => wallet.UserId == userId)
                .Select(wallet => wallet.BalanceMinorUnits)
                .FirstAsync());

    private Task<Guid> CreditAsync(Guid userId, long amount, string? reference = null) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IWalletService>().CreditAsync(
                new AdjustWalletRequest(userId, amount, "test credit", reference),
                performedByUserId: userId);

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    // ---------------------------------------------------------------------------- movements ----

    [Fact]
    public async Task A_new_wallet_starts_empty_and_in_one_currency()
    {
        var userId = await _factory.CreateMemberAsync("wallet-new");

        var wallet = await WithWalletAsync(service => service.GetOrCreateAsync(userId));

        Assert.True(wallet.Succeeded, wallet.ErrorKey);
        Assert.Equal(0, wallet.Value!.BalanceMinorUnits);
        Assert.Equal("IRR", wallet.Value.Currency);
        Assert.False(wallet.Value.IsFrozen);
    }

    [Fact]
    public async Task A_wallet_is_never_created_for_a_member_who_does_not_exist()
    {
        // Otherwise a caller could seed rows by guessing ids.
        var result = await WithWalletAsync(service => service.GetOrCreateAsync(Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(WalletErrors.MemberNotFound, result.ErrorKey);
    }

    [Fact]
    public async Task Crediting_raises_the_balance_and_records_what_it_became()
    {
        var userId = await _factory.CreateMemberAsync("wallet-credit");

        await CreditAsync(userId, 500_000);
        await CreditAsync(userId, 250_000);

        Assert.Equal(750_000, await BalanceAsync(userId));

        var ledger = await WithWalletAsync(service => service.GetLedgerAsync(userId)!);

        Assert.NotNull(ledger);
        Assert.Equal(2, ledger!.Entries.Count);

        // Each row carries the balance it produced, so the chain can be replayed and checked.
        Assert.Equal(500_000, ledger.Entries[0].BalanceAfterMinorUnits);
        Assert.Equal(750_000, ledger.Entries[1].BalanceAfterMinorUnits);
        Assert.True(ledger.IsConsistent);
    }

    [Fact]
    public async Task An_amount_of_zero_or_less_is_refused_rather_than_normalised()
    {
        // Zero is not a movement; a negative amount is a direction expressed the wrong way. Both
        // mean the caller is confused, and quietly accepting either writes a nonsense entry.
        var userId = await _factory.CreateMemberAsync("wallet-zero");

        foreach (var amount in new long[] { 0, -1, -500 })
        {
            var result = await WithWalletAsync(service => service.CreditAsync(
                new AdjustWalletRequest(userId, amount, null, null), userId));

            Assert.False(result.Succeeded);
            Assert.Equal(WalletErrors.AmountInvalid, result.ErrorKey);
        }
    }

    // ------------------------------------------------------------------- never below zero ----

    [Fact]
    public async Task A_debit_larger_than_the_balance_is_refused()
    {
        var userId = await _factory.CreateMemberAsync("wallet-overdraw");

        await CreditAsync(userId, 100_000);

        var result = await WithWalletAsync(service => service.DebitAsync(
            new AdjustWalletRequest(userId, 100_001, "too much", null), userId));

        Assert.False(result.Succeeded);
        Assert.Equal(WalletErrors.InsufficientFunds, result.ErrorKey);
        Assert.Equal(100_000, await BalanceAsync(userId));
    }

    [Fact]
    public async Task Spending_exactly_the_balance_is_allowed_and_leaves_nothing()
    {
        // The boundary. Off-by-one here is the difference between a wallet that can be emptied and
        // one that always keeps a unit back.
        var userId = await _factory.CreateMemberAsync("wallet-exact");

        await CreditAsync(userId, 100_000);

        var result = await WithWalletAsync(service => service.DebitAsync(
            new AdjustWalletRequest(userId, 100_000, "all of it", null), userId));

        Assert.True(result.Succeeded, result.ErrorKey);
        Assert.Equal(0, await BalanceAsync(userId));
    }

    [Fact]
    public async Task Concurrent_debits_cannot_both_take_the_same_credit()
    {
        // The race the concurrency token exists for: two spends reading a balance of 100 and both
        // subtracting 80. One must lose, and the balance must never go below zero.
        var userId = await _factory.CreateMemberAsync("wallet-race");

        await CreditAsync(userId, 100_000);

        var attempts = Enumerable.Range(0, 6).Select(index =>
            _factory.WithScopeAsync(services =>
                services.GetRequiredService<IWalletService>().DebitAsync(
                    new AdjustWalletRequest(userId, 80_000, $"race {index}", null), userId)));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result => result.Succeeded));
        Assert.Equal(20_000, await BalanceAsync(userId));
    }

    [Fact]
    public async Task A_frozen_wallet_can_be_credited_but_not_spent()
    {
        // Settling an account is the usual reason to freeze one, so credit still has to work.
        var userId = await _factory.CreateMemberAsync("wallet-frozen");

        await CreditAsync(userId, 100_000);

        var frozen = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IWalletService>()
                .SetFrozenAsync(userId, true, "under review", userId));

        Assert.True(frozen.Succeeded, frozen.ErrorKey);

        var debit = await WithWalletAsync(service => service.DebitAsync(
            new AdjustWalletRequest(userId, 1_000, null, null), userId));

        Assert.False(debit.Succeeded);
        Assert.Equal(WalletErrors.Frozen, debit.ErrorKey);

        var credit = await WithWalletAsync(service => service.CreditAsync(
            new AdjustWalletRequest(userId, 5_000, null, null), userId));

        Assert.True(credit.Succeeded, credit.ErrorKey);
        Assert.Equal(105_000, await BalanceAsync(userId));
    }

    // ---------------------------------------------------------------------- append-only ----

    [Fact]
    public async Task A_ledger_entry_cannot_be_edited()
    {
        // Enforced in SaveChanges, not by convention. A rule everyone has to remember is one that
        // lasts until the first person in a hurry.
        var userId = await _factory.CreateMemberAsync("wallet-immutable-edit");
        var entryId = await CreditAsync(userId, 50_000);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _factory.WithScopeAsync(async services =>
            {
                var db = services.GetRequiredService<ISentinelDbContext>();

                var entry = await db.WalletTransactions.FirstAsync(row => row.Id == entryId);
                entry.AmountMinorUnits = 1;

                await db.SaveChangesAsync();
            }));

        Assert.Contains("append-only", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing changed.
        Assert.Equal(50_000, await BalanceAsync(userId));
    }

    [Fact]
    public async Task A_ledger_entry_cannot_be_deleted()
    {
        var userId = await _factory.CreateMemberAsync("wallet-immutable-delete");
        var entryId = await CreditAsync(userId, 50_000);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _factory.WithScopeAsync(async services =>
            {
                var db = services.GetRequiredService<ISentinelDbContext>();

                var entry = await db.WalletTransactions.FirstAsync(row => row.Id == entryId);
                db.WalletTransactions.Remove(entry);

                await db.SaveChangesAsync();
            }));

        var stillThere = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ISentinelDbContext>()
                .WalletTransactions.AsNoTracking()
                .AnyAsync(row => row.Id == entryId));

        Assert.True(stillThere);
    }

    // ------------------------------------------------------------------------- reversal ----

    [Fact]
    public async Task Reversing_a_credit_appends_the_opposite_and_links_the_two()
    {
        var userId = await _factory.CreateMemberAsync("wallet-reverse");
        var entryId = await CreditAsync(userId, 90_000);

        var reversalId = await WithWalletAsync(async service =>
        {
            var result = await service.ReverseAsync(entryId, userId, "keyed in twice");

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        Assert.Equal(0, await BalanceAsync(userId));

        var ledger = await WithWalletAsync(service => service.GetLedgerAsync(userId)!);

        Assert.Equal(2, ledger!.Entries.Count);

        var reversal = ledger.Entries.Single(entry => entry.Id == reversalId);

        Assert.Equal(WalletTransactionKind.Reversal, reversal.Kind);
        Assert.Equal(WalletEntryDirection.Debit, reversal.Direction);
        Assert.Equal(entryId, reversal.ReversesTransactionId);

        // The original stays exactly where it was, marked as reversed rather than removed.
        var original = ledger.Entries.Single(entry => entry.Id == entryId);

        Assert.True(original.IsReversed);
        Assert.False(original.CanReverse);
        Assert.Equal(90_000, original.AmountMinorUnits);
    }

    [Fact]
    public async Task An_entry_can_only_be_reversed_once()
    {
        // A second reversal would double the correction, leaving the ledger further from the truth
        // than before somebody tried to fix it.
        var userId = await _factory.CreateMemberAsync("wallet-reverse-once");
        var entryId = await CreditAsync(userId, 30_000);

        Assert.True((await WithWalletAsync(s => s.ReverseAsync(entryId, userId, null))).Succeeded);

        var second = await WithWalletAsync(s => s.ReverseAsync(entryId, userId, null));

        Assert.False(second.Succeeded);
        Assert.Equal(WalletErrors.AlreadyReversed, second.ErrorKey);
        Assert.Equal(0, await BalanceAsync(userId));
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        // Otherwise two operators can undo each other indefinitely and nobody can read the result.
        var userId = await _factory.CreateMemberAsync("wallet-reverse-reversal");
        var entryId = await CreditAsync(userId, 30_000);

        var reversalId = await WithWalletAsync(async s =>
            (await s.ReverseAsync(entryId, userId, null)).Value);

        var result = await WithWalletAsync(s => s.ReverseAsync(reversalId, userId, null));

        Assert.False(result.Succeeded);
        Assert.Equal(WalletErrors.CannotReverseReversal, result.ErrorKey);
    }

    [Fact]
    public async Task Reversing_a_credit_the_member_has_already_spent_is_refused()
    {
        // The ledger does not go negative even to correct itself. An operator has to settle it
        // another way, which is the honest outcome rather than a wallet quietly in debt.
        var userId = await _factory.CreateMemberAsync("wallet-reverse-spent");
        var entryId = await CreditAsync(userId, 40_000);

        Assert.True((await WithWalletAsync(s => s.DebitAsync(
            new AdjustWalletRequest(userId, 40_000, "spent", null), userId))).Succeeded);

        var result = await WithWalletAsync(s => s.ReverseAsync(entryId, userId, null));

        Assert.False(result.Succeeded);
        Assert.Equal(WalletErrors.InsufficientFunds, result.ErrorKey);
        Assert.Equal(0, await BalanceAsync(userId));
    }

    // ----------------------------------------------------------------------- idempotency ----

    [Fact]
    public async Task The_same_reference_applies_once()
    {
        // A retried call after a timeout, or a double-submitted form. Either must not credit twice.
        var userId = await _factory.CreateMemberAsync("wallet-idempotent");

        var first = await CreditAsync(userId, 70_000, "invoice-4417");
        var second = await CreditAsync(userId, 70_000, "invoice-4417");

        Assert.Equal(first, second);
        Assert.Equal(70_000, await BalanceAsync(userId));
    }

    [Fact]
    public async Task Concurrent_calls_with_the_same_reference_apply_once()
    {
        // The case a prior read cannot cover: both callers look, both find nothing, both insert.
        // The unique index is what stops the second, and the loser returns the winner's entry.
        var userId = await _factory.CreateMemberAsync("wallet-idempotent-race");

        var attempts = Enumerable.Range(0, 5).Select(_ =>
            _factory.WithScopeAsync(services =>
                services.GetRequiredService<IWalletService>().CreditAsync(
                    new AdjustWalletRequest(userId, 25_000, "same key", "settlement-991"), userId)));

        var results = await Task.WhenAll(attempts);

        Assert.All(results, result => Assert.True(result.Succeeded, result.ErrorKey));
        Assert.Single(results.Select(result => result.Value).Distinct());
        Assert.Equal(25_000, await BalanceAsync(userId));
    }

    [Fact]
    public async Task Different_references_are_separate_movements()
    {
        var userId = await _factory.CreateMemberAsync("wallet-different-refs");

        await CreditAsync(userId, 10_000, "one");
        await CreditAsync(userId, 10_000, "two");

        Assert.Equal(20_000, await BalanceAsync(userId));
    }

    // -------------------------------------------------------------------------- the flag ----

    [Fact]
    public async Task With_the_wallet_switched_off_the_service_refuses_rather_than_hides()
    {
        // The gate is in the service, not only on the controller: a background job or a future
        // caller that never passes through MVC is refused just the same.
        await using var factory = new SentinelWebApplicationFactory();

        var userId = await factory.CreateMemberAsync("wallet-flag-off");

        var result = await factory.WithScopeAsync(services =>
            services.GetRequiredService<IWalletService>().CreditAsync(
                new AdjustWalletRequest(userId, 10_000, null, null), userId));

        Assert.False(result.Succeeded);
        Assert.Equal(WalletErrors.Disabled, result.ErrorKey);
    }
}
