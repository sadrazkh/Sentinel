using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Catalog;

namespace Sentinel.Infrastructure.Catalog;

public sealed class ApplicationAdminQuery : IApplicationAdminQuery
{
    private readonly ISentinelDbContext _db;
    private readonly TimeProvider _timeProvider;

    public ApplicationAdminQuery(ISentinelDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ApplicationListItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // The catalogue is small and entirely operator-managed, so it is listed whole rather
        // than paged. The grant count is a correlated subquery, not a per-row lookup.
        return await _db.PortalApplications
            .AsNoTracking()
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.NameEn)
            .Select(a => new ApplicationListItem(
                a.Id,
                a.Key,
                a.NameFa,
                a.NameEn,
                a.IconPath,
                a.PublishStatus,
                a.IsEnabled,
                a.IsBeta,
                a.DisplayOrder,
                a.RequiresExplicitEntitlement,
                a.MinimumTier,
                a.Entitlements.Count(e =>
                    e.RevokedAt == null
                    && e.IsEnabled
                    && e.StartsAt <= now
                    && (e.ExpiresAt == null || e.ExpiresAt > now)),
                a.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public Task<ApplicationEditModel?> GetForEditAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        _db.PortalApplications
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ApplicationEditModel(
                a.Id,
                a.Key,
                a.NameFa,
                a.NameEn,
                a.DescriptionFa,
                a.DescriptionEn,
                a.IconPath,
                a.LaunchUrl,
                a.PublishStatus,
                a.IsEnabled,
                a.IsBeta,
                a.DisplayOrder,
                a.RequiresExplicitEntitlement,
                a.MinimumTier,
                a.ConcurrencyToken))
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task<string?> GetIconNameAsync(
        string applicationKey,
        CancellationToken cancellationToken = default)
    {
        var key = applicationKey.Trim().ToLowerInvariant();

        return _db.PortalApplications
            .AsNoTracking()
            .Where(a => a.Key == key)
            .Select(a => a.IconPath)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
