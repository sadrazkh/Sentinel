using Microsoft.EntityFrameworkCore;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Servers;

/// <summary>
/// The operator's read side. Never projects the encrypted token — only its hint — so a credential
/// cannot reach a view even by accident.
/// </summary>
public sealed class VpnServerAdminQuery : IVpnServerAdminQuery
{
    private readonly IVpnDbContext _db;

    public VpnServerAdminQuery(IVpnDbContext db) => _db = db;

    public async Task<IReadOnlyList<VpnServerListItem>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await _db.VpnServers
            .AsNoTracking()
            .OrderBy(s => s.CountryCode)
            .ThenBy(s => s.SelectionPriority)
            .ThenBy(s => s.NameEn)
            .Select(s => new VpnServerListItem(
                s.Id,
                s.Key,
                s.NameFa,
                s.NameEn,
                s.CountryCode,
                s.BaseUrl,
                s.Status,
                s.Health,
                s.ApiTokenHint,
                s.LastHealthCheckAt,
                s.LastHealthError,
                s.MaxClients,
                s.ReservedClients,
                s.SelectionPriority,
                // Counted in SQL: the list shows a number, and loading the profiles to produce
                // one would read every inbound of every server to render a badge.
                s.InboundProfiles.Count(p => p.IsEnabled),
                s.UpdatedAt))
            .ToListAsync(cancellationToken);

    public Task<VpnServerEditModel?> GetForEditAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        _db.VpnServers
            .AsNoTracking()
            .Where(s => s.Id == serverId)
            .Select(s => new VpnServerEditModel(
                s.Id,
                s.Key,
                s.NameFa,
                s.NameEn,
                s.CountryCode,
                s.BaseUrl,
                s.ApiTokenHint,
                s.Status,
                s.MaxClients,
                s.SelectionPriority,
                s.Notes,
                s.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<ServerInboundProfile>> ListInboundsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        await _db.ServerInboundProfiles
            .AsNoTracking()
            .Where(p => p.ServerId == serverId)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.InboundId)
            .ToListAsync(cancellationToken);
}
