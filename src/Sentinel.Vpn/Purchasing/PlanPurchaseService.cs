using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Billing;
using Sentinel.Application.Common;
using Sentinel.Application.Features;
using Sentinel.Application.Memberships;
using Sentinel.Application.Users;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Billing;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;

namespace Sentinel.Vpn.Purchasing;

/// <summary>
/// Buying a plan with wallet credit.
/// <para>
/// The debit and the service are created against the same <c>DbContext</c> and committed by one
/// <c>SaveChanges</c>. That is the whole reason the wallet lives in the shared context rather than a
/// module of its own: a charge without a service, or a service without a charge, are both states
/// somebody would eventually have to reconcile by hand.
/// </para>
/// <para>
/// Nothing the page sent is trusted for the decision. The price, the plan's availability and the
/// member's audience are all re-read here — the catalogue that drew the button is a rendering, and
/// several minutes may have passed since.
/// </para>
/// </summary>
public sealed class PlanPurchaseService : IPlanPurchaseService
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly IWalletService _wallet;
    private readonly ICustomerServiceManager _services;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly IMemberRoleQuery _roles;
    private readonly IAuditService _audit;
    private readonly IFeatureGate _features;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlanPurchaseService> _logger;

    public PlanPurchaseService(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        IWalletService wallet,
        ICustomerServiceManager services,
        IMembershipStatusResolver membershipResolver,
        IMemberRoleQuery roles,
        IAuditService audit,
        IFeatureGate features,
        TimeProvider timeProvider,
        ILogger<PlanPurchaseService> logger)
    {
        _vpn = vpn;
        _db = db;
        _wallet = wallet;
        _services = services;
        _membershipResolver = membershipResolver;
        _roles = roles;
        _audit = audit;
        _features = features;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OperationResult<PurchaseResult>> PurchaseAsync(
        Guid userId,
        PurchasePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Both flags, not either. Purchasing needs a wallet to spend from, and a portal with the
        // wallet off but purchases on would have a buy button leading nowhere.
        if (!_features.IsEnabled(FeatureNames.Purchases) || !_features.IsEnabled(FeatureNames.Wallet))
        {
            return OperationResult<PurchaseResult>.Failure(PurchaseErrors.Disabled);
        }

        var plan = await _vpn.ServicePlans
            .AsNoTracking()
            .Include(candidate => candidate.AudienceRules)
            .FirstOrDefaultAsync(candidate => candidate.Id == request.PlanId, cancellationToken);

        if (plan is null || !plan.IsVisible)
        {
            // A hidden plan answers as a missing one. Distinguishing them would let somebody probe
            // for plans an operator has withdrawn.
            return OperationResult<PurchaseResult>.Failure(PurchaseErrors.PlanNotFound);
        }

        if (!plan.IsPurchasable)
        {
            return OperationResult<PurchaseResult>.Failure(PurchaseErrors.NotPurchasable);
        }

        // Re-evaluated at the moment of purchase. A member's tier can lapse between rendering the
        // catalogue and pressing the button, and this is the check that decides.
        var subject = await LoadSubjectAsync(userId, cancellationToken);

        if (subject is null)
        {
            return OperationResult<PurchaseResult>.Failure(PurchaseErrors.PlanNotFound);
        }

        var rules = plan.AudienceRules
            .Select(rule => new AudienceRuleFacts(
                rule.Effect, rule.Kind, rule.Tier, rule.RoleName, rule.UserId))
            .ToList();

        if (!PlanAudienceEvaluator.IsInAudience(subject, rules))
        {
            await RecordRefusalAsync(userId, plan.Id, "notInAudience", cancellationToken);

            // Answered as "not for you" rather than as a missing plan, because the member can see it
            // in the catalogue only if they are in its audience — so reaching here means either a
            // stale page or a crafted request, and neither warrants a fiction.
            return OperationResult<PurchaseResult>.Failure(PurchaseErrors.NotInAudience);
        }

        // The idempotency key is scoped to the member and the plan, not taken raw from the request:
        // a key a caller controls entirely could be replayed against a different plan, and the
        // ledger's unique index would then treat two different purchases as one.
        var reference = BuildReference(userId, plan.Id, request.IdempotencyKey);

        // A repeat of a purchase that already went through returns what it produced, rather than
        // charging again. Checked before capacity so a retry succeeds even on a full server.
        var previous = await _db.WalletTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                entry => entry.UserId == userId && entry.Reference == reference, cancellationToken);

        if (previous is not null)
        {
            _logger.LogInformation(
                "Purchase for {UserId} of plan {PlanId} was already applied; returning the original.",
                userId,
                plan.Id);

            return OperationResult<PurchaseResult>.Success(new PurchaseResult(
                previous.RelatedServiceId ?? Guid.Empty,
                previous.Id,
                previous.AmountMinorUnits,
                previous.Currency,
                previous.BalanceAfterMinorUnits));
        }

        // The service is recorded first and deliberately left uncommitted. It has to come first for
        // one reason: its id goes onto the ledger entry, and a ledger entry may never be modified
        // after it is written — so the id has to exist before the entry does.
        var created = await _services.CreateAsync(
            new CreateServiceRequest(userId, plan.Id, "Purchased with wallet credit"),
            cancellationToken,
            saveChanges: false);

        if (!created.Succeeded)
        {
            await RecordRefusalAsync(
                userId, plan.Id, created.ErrorKey ?? "serviceRefused", cancellationToken);

            return OperationResult<PurchaseResult>.Failure(
                created.ErrorKey is ServiceErrors.NoCapacity or ServiceErrors.NoServerAvailable
                    or ServiceErrors.NoUsableInbound
                    ? PurchaseErrors.NoCapacity
                    : PurchaseErrors.PlanNotFound);
        }

        var serviceId = created.Value;

        // Also uncommitted. If the member cannot afford it, nothing at all is saved — the service
        // rows above are discarded with the scope, so there is no unpaid service left behind.
        var charge = await _wallet.SpendAsync(
            userId,
            plan.PriceMinorUnits,
            plan.Currency,
            $"Plan: {plan.NameEn}",
            reference,
            serviceId,
            cancellationToken);

        if (!charge.Succeeded)
        {
            await RecordRefusalAsync(userId, plan.Id, charge.ErrorKey ?? "walletRefused", cancellationToken);

            return OperationResult<PurchaseResult>.Failure(
                charge.ErrorKey == WalletErrors.InsufficientFunds
                    ? PurchaseErrors.InsufficientFunds
                    : PurchaseErrors.WalletUnavailable);
        }

        var entry = _db.WalletTransactions.Local
            .FirstOrDefault(candidate => candidate.Id == charge.Value);

        await _audit.RecordAsync(
            AuditEntry.For(PurchaseAuditActions.PlanPurchased, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("userId", userId)
                    .Set("planSlug", plan.Key)
                    .Set("amountMinorUnits", plan.PriceMinorUnits)
                    .Set("currency", plan.Currency),
            },
            cancellationToken);

        // One commit for the debit, the service, its bindings and its provisioning job.
        await _db.SaveChangesAsync(cancellationToken);

        var balance = entry?.BalanceAfterMinorUnits ?? 0;

        _logger.LogInformation(
            "Member {UserId} purchased plan {PlanKey}; service {ServiceId} queued.",
            userId,
            plan.Key,
            serviceId);

        return OperationResult<PurchaseResult>.Success(new PurchaseResult(
            serviceId, charge.Value, plan.PriceMinorUnits, plan.Currency, balance));
    }

    // --------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// Binds the caller's key to this member and this plan.
    /// <para>
    /// Without the binding, a key is a value the caller chooses freely — and reusing one across
    /// plans would make the ledger's uniqueness index collapse two different purchases into one,
    /// silently giving away the second.
    /// </para>
    /// <para>
    /// With no key at all the reference is still per-member-and-plan but carries a fresh
    /// discriminator, so a member may deliberately buy the same plan twice.
    /// </para>
    /// </summary>
    private string BuildReference(Guid userId, Guid planId, string? key)
    {
        var discriminator = string.IsNullOrWhiteSpace(key)
            ? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            : key.Trim();

        // Truncated to fit the column; the member and plan ids are fixed-width, so only the
        // caller-supplied part can be cut, and it is a discriminator rather than a secret.
        var reference = $"purchase:{userId:N}:{planId:N}:{discriminator}";

        return reference.Length <= WalletTransaction.ReferenceMaxLength
            ? reference
            : reference[..WalletTransaction.ReferenceMaxLength];
    }

    private Task RecordRefusalAsync(
        Guid userId,
        Guid planId,
        string reason,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditEntry.For(PurchaseAuditActions.PlanPurchaseRefused, nameof(ServicePlan), planId) with
            {
                Result = AuditResult.Failure,
                Metadata = AuditMetadata.Create()
                    .Set("userId", userId)
                    .Set("reason", reason),
            },
            cancellationToken);

    /// <summary>
    /// The member's tier and roles, from the resolved membership rather than the raw row — a lapsed
    /// Elite membership must not buy an Elite-only plan.
    /// </summary>
    private async Task<AudienceSubject?> LoadSubjectAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var facts = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                Membership = user.Membership == null
                    ? null
                    : new MembershipFacts(
                        user.Membership.Tier,
                        user.Membership.AdminState,
                        user.Membership.StartsAt,
                        user.Membership.EndsAt,
                        user.Membership.GracePeriodDaysOverride),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (facts is null)
        {
            return null;
        }

        var snapshot = _membershipResolver.Resolve(facts.Membership, now);
        var roles = await _roles.GetRoleNamesAsync(userId, cancellationToken);

        return AudienceSubject.For(
            userId, snapshot.GrantsAccess ? snapshot.Tier : null, roles);
    }
}
