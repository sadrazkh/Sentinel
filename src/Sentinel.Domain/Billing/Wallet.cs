using Sentinel.Domain.Common;

namespace Sentinel.Domain.Billing;

/// <summary>
/// A member's credit balance.
/// <para>
/// The balance here is a <b>cache</b>. <see cref="WalletTransaction"/> is the authority: every
/// movement appends a row, and the row records the balance it produced. Keeping the running total
/// on the wallet is what makes "can this member afford it" one indexed read instead of a sum over
/// their whole history — but if the two ever disagree, the ledger is right and this is wrong.
/// </para>
/// <para>
/// Credit only ever arrives from an operator. There is no payment gateway, no top-up endpoint and
/// no member-facing path that increases this number — not a form, not a hidden field, not an API.
/// That is a deliberate boundary of the first version, not an omission to be filled in later
/// without review.
/// </para>
/// </summary>
public class Wallet : IConcurrencyAware, ITimestamped
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// The running total in the currency's smallest unit, mirroring the ledger.
    /// <para>
    /// Minor units and <see cref="long"/>, never a floating-point type: a balance is a count of
    /// indivisible units, and binary floating point cannot represent a tenth exactly.
    /// </para>
    /// </summary>
    public long BalanceMinorUnits { get; set; }

    /// <summary>
    /// ISO 4217. One currency per wallet: mixing them in a single balance would require a rate,
    /// and a stored rate is a decision about somebody's money that nobody made deliberately.
    /// </summary>
    public string Currency { get; set; } = "IRR";

    /// <summary>
    /// Withheld by an operator. A frozen wallet can still be credited — settling an account is the
    /// usual reason to freeze one — but nothing may be spent from it.
    /// </summary>
    public bool IsFrozen { get; set; }

    public string? FrozenReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Guards every movement. Two concurrent debits both reading a balance of 100 and both
    /// subtracting 80 is exactly the race this prevents: the second write loses on the token and
    /// re-reads instead of overdrawing.
    /// </summary>
    public Guid ConcurrencyToken { get; set; }

    public bool CanSpend(long amountMinorUnits) =>
        !IsFrozen && amountMinorUnits > 0 && BalanceMinorUnits >= amountMinorUnits;
}
