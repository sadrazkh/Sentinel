using Sentinel.Application.Common;

namespace Sentinel.Vpn.Purchasing;

/// <summary>
/// What a member sends to buy a plan.
/// <para>
/// A plan and an idempotency key. Nothing else — and in particular <b>no price</b>. The amount
/// charged is read from the plan row inside the transaction; a request that could name its own price
/// would be a customer setting it, and there is no shape here in which they could.
/// </para>
/// </summary>
public sealed record PurchasePlanRequest(
    Guid PlanId,

    /// <summary>
    /// Makes a double-submitted form buy one service rather than two. Supplied by the page and
    /// carried into the ledger, where a unique index enforces it.
    /// </summary>
    string? IdempotencyKey);

/// <summary>What a completed purchase produced.</summary>
public sealed record PurchaseResult(
    Guid ServiceId,
    Guid TransactionId,
    long ChargedMinorUnits,
    string Currency,
    long RemainingBalanceMinorUnits);

public static class PurchaseErrors
{
    public const string Disabled = "purchase.error.disabled";
    public const string PlanNotFound = "purchase.error.planNotFound";
    public const string NotPurchasable = "purchase.error.notPurchasable";
    public const string NotInAudience = "purchase.error.notInAudience";
    public const string AlreadyOwned = "purchase.error.alreadyOwned";
    public const string NoCapacity = "purchase.error.noCapacity";
    public const string InsufficientFunds = "purchase.error.insufficientFunds";
    public const string WalletUnavailable = "purchase.error.walletUnavailable";
}

/// <summary>
/// Buying a plan with wallet credit.
/// <para>
/// One operation, one transaction: the debit and the service are written together, so the ledger
/// can never hold a charge for something that was not created — and a service can never exist that
/// nobody paid for.
/// </para>
/// <para>
/// Every input to the decision is re-read here rather than trusted from the page: the price, whether
/// the plan is on sale, and whether this member is in its audience. The catalogue that rendered the
/// button is a view, and a view is not an authorisation.
/// </para>
/// </summary>
public interface IPlanPurchaseService
{
    Task<OperationResult<PurchaseResult>> PurchaseAsync(
        Guid userId,
        PurchasePlanRequest request,
        CancellationToken cancellationToken = default);
}

public static class PurchaseAuditActions
{
    public const string PlanPurchased = "purchase.plan";
    public const string PlanPurchaseRefused = "purchase.refused";
}
