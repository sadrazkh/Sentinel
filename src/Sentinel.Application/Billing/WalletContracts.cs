using Sentinel.Application.Common;
using Sentinel.Domain.Billing;

namespace Sentinel.Application.Billing;

/// <summary>
/// What an operator supplies to move credit.
/// <para>
/// There is deliberately no member-facing counterpart to this record. Credit enters a wallet one
/// way only — an operator doing it — and the absence of any other request shape is the mechanism,
/// not a convention somebody could break by adding a controller.
/// </para>
/// </summary>
public sealed record AdjustWalletRequest(
    Guid UserId,

    /// <summary>Positive minor units. The direction is the caller's method, not the sign.</summary>
    long AmountMinorUnits,

    string? Description,

    /// <summary>
    /// Idempotency key. Two requests carrying the same one apply once, which is what makes a
    /// retried form post or a repeated call safe.
    /// </summary>
    string? Reference);

/// <summary>One member's wallet as a page reads it.</summary>
public sealed record WalletView(
    Guid Id,
    Guid UserId,
    long BalanceMinorUnits,
    string Currency,
    bool IsFrozen,
    string? FrozenReason,
    DateTimeOffset UpdatedAt);

/// <summary>A member and their balance, for the back office's list.</summary>
public sealed record WalletHolderView(
    Guid UserId,
    string UserName,
    string DisplayName,
    long BalanceMinorUnits,
    string Currency,
    bool IsFrozen,
    bool HasWallet,
    DateTimeOffset? LastMovementAt);

/// <summary>One ledger entry, in the order it happened.</summary>
public sealed record WalletEntryView(
    Guid Id,
    WalletTransactionKind Kind,
    WalletEntryDirection Direction,
    long AmountMinorUnits,
    long BalanceAfterMinorUnits,
    string Currency,
    string? Description,
    Guid? ReversesTransactionId,
    bool IsReversed,
    DateTimeOffset CreatedAt)
{
    /// <summary>Whether this entry may still be reversed: not itself a reversal, and not already one.</summary>
    public bool CanReverse => !IsReversed && Kind != WalletTransactionKind.Reversal;
}

public sealed record WalletLedger(WalletView Wallet, IReadOnlyList<WalletEntryView> Entries)
{
    /// <summary>
    /// Replays the entries and compares the result with the stored balance.
    /// <para>
    /// The ledger is the authority and the wallet's total is a cache, so this is the check that says
    /// whether the cache is still telling the truth. Shown to an operator rather than silently
    /// corrected: a divergence means something wrote a balance without an entry, and quietly fixing
    /// the number would hide the bug that produced it.
    /// </para>
    /// </summary>
    public bool IsConsistent =>
        Entries.Count == 0
            ? Wallet.BalanceMinorUnits == 0
            : Entries[^1].BalanceAfterMinorUnits == Wallet.BalanceMinorUnits;
}

public static class WalletErrors
{
    public const string Disabled = "admin.error.walletDisabled";
    public const string MemberNotFound = "admin.error.walletMemberNotFound";
    public const string NotFound = "admin.error.walletNotFound";
    public const string AmountInvalid = "admin.error.walletAmountInvalid";
    public const string InsufficientFunds = "admin.error.walletInsufficientFunds";
    public const string Frozen = "admin.error.walletFrozen";
    public const string CurrencyMismatch = "admin.error.walletCurrencyMismatch";
    public const string EntryNotFound = "admin.error.walletEntryNotFound";
    public const string AlreadyReversed = "admin.error.walletAlreadyReversed";
    public const string CannotReverseReversal = "admin.error.walletCannotReverseReversal";
    public const string Contended = "admin.error.walletContended";
}

/// <summary>
/// The credit ledger.
/// <para>
/// Three rules shape this interface, and each is enforced here rather than left to callers:
/// </para>
/// <list type="bullet">
/// <item><b>Credit is operator-only.</b> <see cref="CreditAsync"/> takes the operator's id and is
/// reachable only from the back office. Nothing member-facing increases a balance.</item>
/// <item><b>Entries are never edited or deleted.</b> A mistake is corrected by
/// <see cref="ReverseAsync"/>, which appends the opposite entry and links the two.</item>
/// <item><b>A balance never goes negative.</b> The check and the write are one guarded operation,
/// so two concurrent spends cannot both pass it.</item>
/// </list>
/// <para>
/// Every method is idempotent on its <c>Reference</c>: calling twice with the same key applies
/// once and returns the entry that already exists.
/// </para>
/// </summary>
public interface IWalletService
{
    /// <summary>Reads a member's wallet, creating an empty one on first look.</summary>
    Task<OperationResult<WalletView>> GetOrCreateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every member and what they hold, for the back office.
    /// <para>
    /// Includes members with no wallet row yet, shown as a zero balance. An operator looking for
    /// somebody to credit should find them whether or not the portal has happened to create a row
    /// for them — "no wallet" is not a state they should have to resolve first.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<WalletHolderView>> ListHoldersAsync(
        string? search = null,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<WalletLedger?> GetLedgerAsync(
        Guid userId,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Adds credit. The only way a balance goes up, and only an operator can call it.</summary>
    Task<OperationResult<Guid>> CreditAsync(
        AdjustWalletRequest request,
        Guid performedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Takes credit away. Refused rather than allowed to overdraw.</summary>
    Task<OperationResult<Guid>> DebitAsync(
        AdjustWalletRequest request,
        Guid performedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends credit on the member's own behalf.
    /// <para>
    /// Separate from <see cref="DebitAsync"/> because it is not an operator action: it takes no
    /// operator id, it records <see cref="WalletTransactionKind.Purchase"/>, and the amount comes
    /// from a price the caller has already read from a catalogue row.
    /// </para>
    /// <para>
    /// Deliberately does <b>not</b> save: the caller commits it together with whatever the member
    /// bought, so a debit can never be recorded without the thing it paid for.
    /// </para>
    /// </summary>
    Task<OperationResult<Guid>> SpendAsync(
        Guid userId,
        long amountMinorUnits,
        string currency,
        string description,
        string? reference,
        Guid? relatedServiceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends the opposite of an earlier entry. The only correction this ledger has.
    /// </summary>
    Task<OperationResult<Guid>> ReverseAsync(
        Guid transactionId,
        Guid performedByUserId,
        string? description,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetFrozenAsync(
        Guid userId,
        bool frozen,
        string? reason,
        Guid performedByUserId,
        CancellationToken cancellationToken = default);
}

public static class WalletAuditActions
{
    public const string Credited = "wallet.credited";
    public const string Debited = "wallet.debited";
    public const string Spent = "wallet.spent";
    public const string Reversed = "wallet.reversed";
    public const string Frozen = "wallet.frozen";
    public const string Unfrozen = "wallet.unfrozen";
}
