using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Common;
using Sentinel.Application.Memberships;
using Sentinel.Application.Security;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Users;

/// <summary>
/// Takes the concrete <see cref="SentinelDbContext"/> rather than
/// <c>ISentinelDbContext</c>: the role filter has to join Identity's own <c>UserRoles</c> and
/// <c>Roles</c> tables, and widening the application-layer interface to expose them would
/// leak Identity's schema to every consumer for the sake of one query.
/// </summary>
public sealed class UserAdminQuery : IUserAdminQuery
{
    private const int RecentLoginCount = 10;

    private readonly SentinelDbContext _db;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly TimeProvider _timeProvider;

    public UserAdminQuery(
        SentinelDbContext db,
        IMembershipStatusResolver membershipResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _membershipResolver = membershipResolver;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<UserListItem>> SearchAsync(
        UserListRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Normalized();

        var now = _timeProvider.GetUtcNow();

        var query = _db.Users.AsNoTracking();

        if (request.Search is { } search)
        {
            // EF.Functions.Like keeps this a parameterised LIKE. Interpolating the term into
            // SQL would be an injection hole; building it with string concatenation of the
            // wildcards only is safe because the term itself stays a parameter.
            var pattern = $"%{EscapeLike(search)}%";

            query = query.Where(u =>
                EF.Functions.Like(u.UserName!, pattern)
                || EF.Functions.Like(u.DisplayName, pattern)
                || EF.Functions.Like(u.Email!, pattern)
                || EF.Functions.Like(u.NormalizedPhoneNumber!, pattern));
        }

        if (request.Status is { } status)
        {
            query = query.Where(u => u.Status == status);
        }

        if (request.Role is { } role)
        {
            var normalizedRole = role.ToUpperInvariant();

            query = query.Where(u => _db.UserRoles
                .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.NormalizedName })
                .Any(x => x.UserId == u.Id && x.NormalizedName == normalizedRole));
        }

        if (request.HasMembership is { } hasMembership)
        {
            query = hasMembership
                ? query.Where(u => u.Membership != null)
                : query.Where(u => u.Membership == null);
        }

        if (request.MembershipEndsBefore is { } endsBefore)
        {
            query = query.Where(u => u.Membership != null
                                     && u.Membership.EndsAt != null
                                     && u.Membership.EndsAt <= endsBefore);
        }

        // Counted before paging, on the same filtered query, so the pager stays truthful.
        var totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<UserListItem>.Empty(request.PageSize);
        }

        query = ApplySort(query, request);

        var rows = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(u => new UserRow(
                u.Id,
                u.UserName!,
                u.DisplayName,
                u.Email,
                u.PhoneNumber,
                u.Status,
                u.CreatedAt,
                u.LastLoginAt,
                _db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                    .ToList(),
                u.Membership == null
                    ? null
                    : new MembershipFacts(
                        u.Membership.Tier,
                        u.Membership.AdminState,
                        u.Membership.StartsAt,
                        u.Membership.EndsAt,
                        u.Membership.GracePeriodDaysOverride)))
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new UserListItem(
                row.Id,
                row.UserName,
                row.DisplayName,
                row.Email,
                row.PhoneNumber,
                row.Status,
                row.CreatedAt,
                row.LastLoginAt,
                row.Roles,
                _membershipResolver.Resolve(row.Membership, now)))
            .ToList();

        return new PagedResult<UserListItem>(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<UserDetail?> GetDetailAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var detail = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new DetailRow(
                u.Id,
                u.UserName!,
                u.DisplayName,
                u.Email,
                u.PhoneNumber,
                u.Status,
                u.SuspendedUntil,
                u.StatusNote,
                u.PreferredCulture,
                u.TimeZoneId,
                u.CreatedAt,
                u.UpdatedAt,
                u.LastLoginAt,
                u.LockoutEnd,
                u.AccessFailedCount,
                _db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                    .ToList(),
                u.Membership == null
                    ? null
                    : new MembershipEditModel(
                        u.Membership.Tier,
                        u.Membership.AdminState,
                        u.Membership.StartsAt,
                        u.Membership.EndsAt,
                        u.Membership.GracePeriodDaysOverride,
                        u.Membership.Notes,
                        u.Membership.ConcurrencyToken),
                u.Entitlements
                    .OrderBy(e => e.Product!.DisplayOrder)
                    .Select(e => new UserEntitlementSummary(
                        e.ProductId,
                        e.Product!.Key,
                        e.Product.NameEn,
                        e.IsEnabled,
                        e.StartsAt,
                        e.ExpiresAt,
                        e.RevokedAt,
                        e.Notes))
                    .ToList(),
                u.LoginAttempts
                    .OrderByDescending(a => a.OccurredAt)
                    .Take(RecentLoginCount)
                    .Select(a => new LoginAttemptView(
                        a.OccurredAt, a.Succeeded, a.FailureReason, a.IpAddress, a.UserAgent))
                    .ToList(),
                u.Sessions.Count(s => s.RevokedAt == null && s.ExpiresAt > now)))
            .FirstOrDefaultAsync(cancellationToken);

        if (detail is null)
        {
            return null;
        }

        var membershipFacts = detail.MembershipEdit is { } edit
            ? new MembershipFacts(
                edit.Tier, edit.AdminState, edit.StartsAt, edit.EndsAt, edit.GracePeriodDaysOverride)
            : null;

        return new UserDetail(
            detail.Id,
            detail.UserName,
            detail.DisplayName,
            detail.Email,
            detail.PhoneNumber,
            detail.Status,
            detail.SuspendedUntil,
            detail.StatusNote,
            detail.PreferredCulture,
            detail.TimeZoneId,
            detail.CreatedAt,
            detail.UpdatedAt,
            detail.LastLoginAt,
            detail.LockoutEnd is { } lockoutEnd && lockoutEnd > now,
            detail.LockoutEnd,
            detail.AccessFailedCount,
            detail.Roles,
            _membershipResolver.Resolve(membershipFacts, now),
            detail.MembershipEdit,
            detail.Entitlements,
            detail.RecentLoginAttempts,
            detail.ActiveSessionCount);
    }

    private static IQueryable<ApplicationUser> ApplySort(
        IQueryable<ApplicationUser> query,
        UserListRequest request) =>
        (request.SortBy, request.Descending) switch
        {
            (UserSortField.DisplayName, false) => query.OrderBy(u => u.DisplayName).ThenBy(u => u.Id),
            (UserSortField.DisplayName, true) => query.OrderByDescending(u => u.DisplayName).ThenBy(u => u.Id),
            (UserSortField.UserName, false) => query.OrderBy(u => u.UserName).ThenBy(u => u.Id),
            (UserSortField.UserName, true) => query.OrderByDescending(u => u.UserName).ThenBy(u => u.Id),
            (UserSortField.LastLoginAt, false) => query.OrderBy(u => u.LastLoginAt).ThenBy(u => u.Id),
            (UserSortField.LastLoginAt, true) => query.OrderByDescending(u => u.LastLoginAt).ThenBy(u => u.Id),
            (UserSortField.Status, false) => query.OrderBy(u => u.Status).ThenBy(u => u.Id),
            (UserSortField.Status, true) => query.OrderByDescending(u => u.Status).ThenBy(u => u.Id),

            // Id is a UUID v7, so the tiebreaker is also chronological — and, more importantly,
            // it makes the order total, which stops rows from shifting between pages.
            (_, false) => query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id),
            _ => query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id),
        };

    /// <summary>
    /// Escapes the LIKE metacharacters so a search for "50%" does not turn into a wildcard.
    /// The term still travels as a parameter; this only stops it from meaning something else.
    /// </summary>
    private static string EscapeLike(string term) => term
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    private sealed record UserRow(
        Guid Id,
        string UserName,
        string DisplayName,
        string? Email,
        string? PhoneNumber,
        UserAccountStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastLoginAt,
        List<string> Roles,
        MembershipFacts? Membership);

    private sealed record DetailRow(
        Guid Id,
        string UserName,
        string DisplayName,
        string? Email,
        string? PhoneNumber,
        UserAccountStatus Status,
        DateTimeOffset? SuspendedUntil,
        string? StatusNote,
        string PreferredCulture,
        string TimeZoneId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? LastLoginAt,
        DateTimeOffset? LockoutEnd,
        int AccessFailedCount,
        List<string> Roles,
        MembershipEditModel? MembershipEdit,
        List<UserEntitlementSummary> Entitlements,
        List<LoginAttemptView> RecentLoginAttempts,
        int ActiveSessionCount);
}
