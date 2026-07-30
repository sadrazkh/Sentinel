using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Billing;
using Sentinel.Domain.Billing;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class WalletListViewModel
{
    public required IReadOnlyList<WalletHolderView> Holders { get; init; }

    public required string? Search { get; init; }

    public required bool CanWrite { get; init; }
}

public sealed class WalletDetailViewModel
{
    public required Guid UserId { get; init; }

    public required string UserName { get; init; }

    public required string DisplayName { get; init; }

    public required WalletLedger? Ledger { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Newest first for reading. The service returns oldest-first so the running balance can be
    /// checked against the last row; an operator wants the opposite.
    /// </summary>
    public IReadOnlyList<WalletEntryView> Entries =>
        Ledger is null ? [] : Ledger.Entries.Reverse().ToList();
}

/// <summary>
/// What an operator supplies to move credit.
/// <para>
/// An amount, a note and an optional idempotency key. The member comes from the route and the
/// operator from the signed-in principal — neither is a field here, because both are identities and
/// a form is not where an identity should come from.
/// </para>
/// </summary>
public sealed class WalletAdjustViewModel
{
    /// <summary>
    /// Minor units, and a whole number. Entering a price in major units is the mistake this bound
    /// leaves visible rather than silently multiplying by a hundred.
    /// </summary>
    [Range(1, 1_000_000_000_000, ErrorMessage = "admin.error.walletAmountInvalid")]
    [Display(Name = "admin.wallet.amount")]
    public long AmountMinorUnits { get; set; }

    [StringLength(WalletTransaction.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.wallet.description")]
    public string? Description { get; set; }

    [StringLength(WalletTransaction.ReferenceMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.wallet.reference")]
    public string? Reference { get; set; }
}
