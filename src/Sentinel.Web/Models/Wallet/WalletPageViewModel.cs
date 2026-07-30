using Sentinel.Application.Billing;

namespace Sentinel.Web.Models.Wallet;

public sealed class WalletPageViewModel
{
    public required WalletLedger? Ledger { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>Newest first: a statement reads backwards from now.</summary>
    public IReadOnlyList<WalletEntryView> Entries =>
        Ledger is null ? [] : Ledger.Entries.Reverse().ToList();

    public long Balance => Ledger?.Wallet.BalanceMinorUnits ?? 0;

    public string Currency => Ledger?.Wallet.Currency ?? "IRR";

    public bool IsFrozen => Ledger?.Wallet.IsFrozen ?? false;
}
