using Sentinel.Application.Common;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Provisioning;

/// <summary>
/// What an operator supplies to create a service.
/// <para>
/// Deliberately just a member and a plan. Every term — traffic, duration, devices — is read from the
/// plan row, and the server is chosen by <see cref="ServerSelector"/>. There is no field here for a
/// quota, an expiry, a server or an inbound, because a request shape that carried one would be a
/// customer setting their own terms.
/// </para>
/// </summary>
public sealed record CreateServiceRequest(Guid UserId, Guid PlanId, string? Notes);

/// <summary>What the member sees of one provisioned service.</summary>
public sealed record CustomerServiceView(
    Guid Id,
    Guid ProductId,
    string PlanNameFa,
    string PlanNameEn,
    CustomerServiceStatus Status,
    string? CountryCode,
    long TrafficBytes,
    long UsedBytes,
    int DeviceLimit,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsageSyncAt,
    DateTimeOffset? LastOnlineAt,
    bool HasDeliveryToken,

    /// <summary>
    /// The member's own delivery token, unsealed for them alone. Null when the service has no link
    /// yet, or when the key ring has moved on and the link has to be re-issued.
    /// <para>
    /// This projection is only ever built for the signed-in owner. It must not be handed to an
    /// operator view, which is why <see cref="CustomerServiceAdminRow"/> has no equivalent field.
    /// </para>
    /// </summary>
    string? DeliveryToken,
    bool IsUsable)
{
    public bool IsUnlimitedTraffic => TrafficBytes <= 0;

    public long? RemainingBytes =>
        IsUnlimitedTraffic ? null : Math.Max(0, TrafficBytes - UsedBytes);

    public int? UsedPercent => TrafficBytes <= 0
        ? null
        : (int)Math.Clamp(UsedBytes * 100 / TrafficBytes, 0, 100);

    public int? DaysRemaining => ExpiresAt is not { } expires
        ? null
        : Math.Max(0, (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalDays));
}

/// <summary>
/// The operator's view. Adds the placement and the panel identifier, which a member never receives —
/// the identifier is what addresses their client on a third-party system.
/// </summary>
public sealed record CustomerServiceAdminRow(
    Guid Id,
    Guid UserId,
    string UserName,
    string UserDisplayName,
    string PlanNameEn,
    CustomerServiceStatus Status,
    Guid? ServerId,
    string? ServerKey,
    string? CountryCode,
    string? PanelClientEmail,
    long TrafficBytes,
    long UsedBytes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsageSyncAt,
    string? LastError,
    int PendingJobCount,
    DateTimeOffset CreatedAt,
    Guid ConcurrencyToken)
{
        /// <summary>Services an operator has to look at, rather than ones simply working or finished.</summary>
    public bool NeedsAttention =>
        Status is CustomerServiceStatus.NeedsAttention
        || (Status is CustomerServiceStatus.Provisioning or CustomerServiceStatus.Decommissioning
            && PendingJobCount == 0);
}

public static class ServiceErrors
{
    public const string NotFound = "admin.error.serviceNotFound";
    public const string PlanNotFound = "admin.error.servicePlanNotFound";
    public const string MemberNotFound = "admin.error.serviceMemberNotFound";
    public const string NoServerAvailable = "admin.error.serviceNoServer";
    public const string NoCapacity = "admin.error.serviceNoCapacity";
    public const string NoUsableInbound = "admin.error.serviceNoInbound";
    public const string AlreadyEnded = "admin.error.serviceAlreadyEnded";
    public const string BusyProvisioning = "admin.error.serviceBusy";

    /// <summary>
    /// A migration is in flight, so the service's server is about to change. Every lifecycle
    /// operation queues a job against the server the service is on <em>now</em>, and that server may
    /// not exist for this customer by the time the job runs.
    /// </summary>
    public const string BusyMigrating = "admin.error.serviceMigrating";
}

/// <summary>
/// The lifecycle of a customer's VPN service.
/// <para>
/// Every method records intent and returns; the panel work happens in a background worker. That is
/// not just for responsiveness — it is what makes the intent survive a process restart in the middle
/// of a panel call, which is precisely when the outcome is unknown.
/// </para>
/// </summary>
public interface ICustomerServiceManager
{
    /// <summary>
    /// Creates a service and queues its provisioning. Reserves capacity synchronously, so a member
    /// is told immediately if there is nowhere to put it rather than finding out later.
    /// </summary>
    /// <param name="saveChanges">
    /// Pass <c>false</c> to leave the rows uncommitted so a caller can commit them with something
    /// else. The purchase flow needs this: the wallet debit and the service have to land in one
    /// transaction, and it also has to write the service's id onto the ledger entry — which is only
    /// possible while that entry is still an unsaved insert, because a committed one may never be
    /// modified.
    /// </param>
    Task<OperationResult<Guid>> CreateAsync(
        CreateServiceRequest request,
        CancellationToken cancellationToken = default,
        bool saveChanges = true);

    Task<OperationResult> SuspendAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task<OperationResult> ResumeAsync(Guid serviceId, CancellationToken cancellationToken = default);

    /// <summary>Extends the expiry and pushes the new terms to the panel.</summary>
    Task<OperationResult> RenewAsync(
        Guid serviceId,
        int additionalDays,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ResetTrafficAsync(Guid serviceId, CancellationToken cancellationToken = default);

    /// <summary>Removes the client from the panel and ends the service.</summary>
    Task<OperationResult> DecommissionAsync(Guid serviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a fresh delivery token, invalidating the old one.
    /// <para>
    /// Returns the plaintext once. It is never readable again, so the caller has to show it now — and
    /// this is the only remedy once a delivery URL has leaked.
    /// </para>
    /// </summary>
    Task<OperationResult<string>> RotateDeliveryTokenAsync(
        Guid serviceId,
        Guid requestingUserId,
        CancellationToken cancellationToken = default);
}

public interface ICustomerServiceQuery
{
    /// <summary>A member's own services. Scoped by owner, so one member cannot read another's.</summary>
    Task<IReadOnlyList<CustomerServiceView>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerServiceView>> GetForUserAndProductAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerServiceAdminRow>> ListAsync(
        CancellationToken cancellationToken = default);
}
