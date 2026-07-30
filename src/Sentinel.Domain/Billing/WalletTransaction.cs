namespace Sentinel.Domain.Billing;

/// <summary>
/// Why money moved. The direction is <see cref="WalletEntryDirection"/>; this says what happened.
/// </summary>
public enum WalletTransactionKind
{
    /// <summary>An operator added credit. The only way a balance ever goes up.</summary>
    OperatorCredit = 0,

    /// <summary>An operator took credit away — a correction, or settling an account.</summary>
    OperatorDebit = 1,

    /// <summary>A member spent credit on a plan.</summary>
    Purchase = 2,

    /// <summary>
    /// Credit given back because something the member paid for could not be delivered — a
    /// provisioning attempt that failed for good, or a service withdrawn early.
    /// </summary>
    Refund = 3,

    /// <summary>
    /// Undoes an earlier entry. The <em>only</em> way to correct the ledger: rows are never edited
    /// and never deleted, so a mistake is fixed by recording its opposite and linking the two.
    /// </summary>
    Reversal = 4,
}

public enum WalletEntryDirection
{
    /// <summary>Increases the balance.</summary>
    Credit = 0,

    /// <summary>Decreases it.</summary>
    Debit = 1,
}

/// <summary>
/// One immutable movement of credit.
/// <para>
/// Append-only, and that is the whole point of the type. There is no <c>UpdatedAt</c> and no
/// concurrency token, because nothing ever updates a row: an entry written in error is corrected by
/// appending a <see cref="WalletTransactionKind.Reversal"/> that points at it. A ledger you can edit
/// is not a ledger — it is a balance with a history-shaped decoration, and the first time somebody
/// disputes a charge the decoration is worth nothing.
/// </para>
/// <para>
/// Every row carries <see cref="BalanceAfterMinorUnits"/>, the balance it produced. That makes the
/// chain self-checking: replaying the entries must reproduce the wallet's stored total, and where it
/// does not, the row at which they diverge is the one to look at.
/// </para>
/// </summary>
public class WalletTransaction
{
    public const int DescriptionMaxLength = 500;
    public const int ReferenceMaxLength = 128;

    public Guid Id { get; set; }

    public Guid WalletId { get; set; }

    public Wallet? Wallet { get; set; }

    /// <summary>
    /// Denormalised from the wallet so the ledger can be read per member without a join, and so an
    /// entry still names its owner if a wallet row is ever rebuilt.
    /// </summary>
    public Guid UserId { get; set; }

    public WalletTransactionKind Kind { get; set; }

    public WalletEntryDirection Direction { get; set; }

    /// <summary>
    /// Always <b>positive</b>, in minor units. The direction is a separate field rather than the
    /// sign, so a row cannot express "a credit of minus fifty" — which reads as a debit to a person
    /// and as a credit to a <c>SUM</c>.
    /// </summary>
    public long AmountMinorUnits { get; set; }

    /// <summary>The balance this entry produced. See the class remarks.</summary>
    public long BalanceAfterMinorUnits { get; set; }

    public string Currency { get; set; } = "IRR";

    /// <summary>Free text for a person. Never a secret, never a raw exception.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The caller's idempotency key, unique per wallet.
    /// <para>
    /// A financial write that is retried after a timeout must not apply twice, and a form that is
    /// double-submitted must not buy two services. The uniqueness is enforced by an index, so the
    /// guarantee survives two replicas racing rather than resting on a prior read.
    /// </para>
    /// </summary>
    public string? Reference { get; set; }

    /// <summary>The entry this one reverses. Set only on a <see cref="WalletTransactionKind.Reversal"/>.</summary>
    public Guid? ReversesTransactionId { get; set; }

    public WalletTransaction? ReversesTransaction { get; set; }

    /// <summary>
    /// The operator who caused this, where one did. Null for a member's own purchase — that entry's
    /// actor is the owner, which <see cref="UserId"/> already names.
    /// </summary>
    public Guid? PerformedByUserId { get; set; }

    /// <summary>What was bought, where this entry paid for something.</summary>
    public Guid? RelatedServiceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // ---- derived -----------------------------------------------------------------------

    /// <summary>The signed effect on the balance, for replaying the chain.</summary>
    public long SignedAmount =>
        Direction == WalletEntryDirection.Credit ? AmountMinorUnits : -AmountMinorUnits;
}
