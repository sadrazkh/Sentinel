using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Vpn.Delivery;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;

namespace Sentinel.Vpn.Provisioning;

/// <summary>
/// Reads customer services.
/// <para>
/// The member-facing projections never carry the panel identifier or the delivery token hash. The
/// identifier addresses their client on a third-party system and the hash is a credential; neither
/// has any business reaching a page, so neither is selected.
/// </para>
/// </summary>
public sealed class CustomerServiceQuery : ICustomerServiceQuery
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly IDeliverySecretProtector _secrets;
    private readonly TimeProvider _timeProvider;

    public CustomerServiceQuery(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        IDeliverySecretProtector secrets,
        TimeProvider timeProvider)
    {
        _vpn = vpn;
        _db = db;
        _secrets = secrets;
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyList<CustomerServiceView>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        ForUserAsync(userId, productId: null, cancellationToken);

    public Task<IReadOnlyList<CustomerServiceView>> GetForUserAndProductAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken = default) =>
        ForUserAsync(userId, productId, cancellationToken);

    private async Task<IReadOnlyList<CustomerServiceView>> ForUserAsync(
        Guid userId,
        Guid? productId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // Scoped by owner in the query itself, not filtered afterwards: one member's list can never
        // contain another's row, whatever else goes wrong downstream.
        var query = _vpn.CustomerServices
            .AsNoTracking()
            .Where(service => service.UserId == userId
                              && service.Status != CustomerServiceStatus.Ended);

        if (productId is { } product)
        {
            query = query.Where(service => service.ProductId == product);
        }

        var rows = await query
            .OrderByDescending(service => service.CreatedAt)
            .Select(service => new
            {
                service.Id,
                service.ProductId,
                service.PlanNameFa,
                service.PlanNameEn,
                service.Status,
                CountryCode = service.Server == null ? null : service.Server.CountryCode,
                service.TrafficBytes,
                service.UsedBytes,
                service.DeviceLimit,
                service.StartsAt,
                service.ExpiresAt,
                service.LastUsageSyncAt,
                service.LastOnlineAt,

                // The hash never leaves the database — it is the request path's comparand, and a page
                // has no use for it. The sealed copy does leave, but only to be opened for the owner
                // whose row this is, which the WHERE clause above has already established.
                HasToken = service.DeliveryTokenHash != null,
                service.DeliveryTokenSealed,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CustomerServiceView(
                row.Id,
                row.ProductId,
                row.PlanNameFa,
                row.PlanNameEn,
                row.Status,
                row.CountryCode,
                row.TrafficBytes,
                row.UsedBytes,
                row.DeviceLimit,
                row.StartsAt,
                row.ExpiresAt,
                row.LastUsageSyncAt,
                row.LastOnlineAt,
                row.HasToken,
                _secrets.Open(row.DeliveryTokenSealed),

                // Recomputed against the clock rather than read from the status alone: a service can
                // pass its expiry between sweeps, and a stale status must not keep it looking live.
                IsUsable: row.Status == CustomerServiceStatus.Active
                          && (row.ExpiresAt is not { } expires || expires > now)
                          && (row.TrafficBytes <= 0 || row.UsedBytes < row.TrafficBytes)))
            .ToList();
    }

    public async Task<IReadOnlyList<CustomerServiceAdminRow>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var services = await _vpn.CustomerServices
            .AsNoTracking()
            .OrderByDescending(service => service.CreatedAt)
            .Select(service => new
            {
                service.Id,
                service.UserId,
                service.PlanNameEn,
                service.Status,
                service.ServerId,
                ServerKey = service.Server == null ? null : service.Server.Key,
                CountryCode = service.Server == null ? null : service.Server.CountryCode,
                service.PanelClientEmail,
                service.TrafficBytes,
                service.UsedBytes,
                service.ExpiresAt,
                service.LastUsageSyncAt,
                service.LastError,
                service.CreatedAt,
                service.ConcurrencyToken,
            })
            .ToListAsync(cancellationToken);

        if (services.Count == 0)
        {
            return [];
        }

        var userIds = services.Select(service => service.UserId).Distinct().ToList();

        // Names in one extra query rather than a join across the module boundary, matched in memory.
        var users = await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.UserName, user.DisplayName })
            .ToListAsync(cancellationToken);

        var byId = users.ToDictionary(user => user.Id);

        var serviceIds = services.Select(service => service.Id).ToList();

        // Unfinished jobs per service, so the list can distinguish "working on it" from "stuck".
        var pendingCounts = await _vpn.ProvisioningJobs
            .AsNoTracking()
            .Where(job => serviceIds.Contains(job.ServiceId)
                          && (job.Status == ProvisioningJobStatus.Pending
                              || job.Status == ProvisioningJobStatus.Running
                              || job.Status == ProvisioningJobStatus.Failed))
            .GroupBy(job => job.ServiceId)
            .Select(group => new { ServiceId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var pendingByService = pendingCounts.ToDictionary(row => row.ServiceId, row => row.Count);

        return services
            .Select(service =>
            {
                byId.TryGetValue(service.UserId, out var user);
                pendingByService.TryGetValue(service.Id, out var pending);

                return new CustomerServiceAdminRow(
                    service.Id,
                    service.UserId,
                    user?.UserName ?? "—",
                    user?.DisplayName ?? "—",
                    service.PlanNameEn,
                    service.Status,
                    service.ServerId,
                    service.ServerKey,
                    service.CountryCode,
                    service.PanelClientEmail,
                    service.TrafficBytes,
                    service.UsedBytes,
                    service.ExpiresAt,
                    service.LastUsageSyncAt,
                    service.LastError,
                    pending,
                    service.CreatedAt,
                    service.ConcurrencyToken);
            })
            .ToList();
    }
}
