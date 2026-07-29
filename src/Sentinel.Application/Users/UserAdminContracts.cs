using Sentinel.Application.Common;
using Sentinel.Application.Memberships;
using Sentinel.Application.Security;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Users;

public enum UserSortField
{
    CreatedAt = 0,
    DisplayName = 1,
    UserName = 2,
    LastLoginAt = 3,
    Status = 4,
}

/// <summary>
/// Filters for the admin user list. Every one of them is expressible in SQL — deliberately.
/// <para>
/// There is no "membership status" filter, because that status is computed and filtering on it
/// would mean either reimplementing the resolver in LINQ (a second copy of the rule, free to
/// drift) or paging in memory. <see cref="MembershipEndsBefore"/> answers the question an
/// operator actually asks — "who needs chasing?" — and does it in the database.
/// </para>
/// </summary>
public sealed record UserListRequest(
    string? Search = null,
    UserAccountStatus? Status = null,
    string? Role = null,
    DateTimeOffset? MembershipEndsBefore = null,
    bool? HasMembership = null,
    UserSortField SortBy = UserSortField.CreatedAt,
    bool Descending = true,
    int Page = 1,
    int PageSize = PagingDefaults.DefaultPageSize)
{
    public UserListRequest Normalized() => this with
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
        Role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim(),
        Page = PagingDefaults.NormalizePage(Page),
        PageSize = PagingDefaults.NormalizePageSize(PageSize),
    };
}

public sealed record UserListItem(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    UserAccountStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Roles,
    MembershipSnapshot Membership);

public sealed record UserEntitlementSummary(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    bool IsEnabled,
    DateTimeOffset StartsAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? Notes);

public sealed record UserDetail(
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
    bool LockoutActive,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount,
    IReadOnlyList<string> Roles,
    MembershipSnapshot Membership,
    MembershipEditModel? MembershipEdit,
    IReadOnlyList<UserEntitlementSummary> Entitlements,
    IReadOnlyList<LoginAttemptView> RecentLoginAttempts,
    int ActiveSessionCount);

public sealed record CreateUserRequest(
    string UserName,
    string DisplayName,
    string Email,
    string? PhoneNumber,
    string Password,
    IReadOnlyList<string> Roles,
    string PreferredCulture,
    string TimeZoneId);

public sealed record UpdateUserProfileRequest(
    string DisplayName,
    string Email,
    string? PhoneNumber,
    string PreferredCulture,
    string TimeZoneId);

public sealed record ChangeUserStatusRequest(
    UserAccountStatus Status,
    DateTimeOffset? SuspendedUntil,
    string? Note);

/// <summary>
/// The editable shape of a membership. Carries the concurrency token so a stale form is
/// rejected rather than silently overwriting a change made in the meantime.
/// </summary>
public sealed record MembershipEditModel(
    MembershipTier Tier,
    MembershipAdminState AdminState,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? GracePeriodDaysOverride,
    string? Notes,
    Guid ConcurrencyToken);

public sealed record MembershipEditRequest(
    MembershipTier Tier,
    MembershipAdminState AdminState,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? GracePeriodDaysOverride,
    string? Notes,
    /// <summary><c>null</c> when creating the first membership for a user.</summary>
    Guid? ConcurrencyToken);

public interface IUserAdminQuery
{
    Task<PagedResult<UserListItem>> SearchAsync(
        UserListRequest request,
        CancellationToken cancellationToken = default);

    Task<UserDetail?> GetDetailAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IUserAdminService
{
    Task<OperationResult<Guid>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateProfileAsync(
        Guid userId,
        UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ChangeStatusAsync(
        Guid userId,
        ChangeUserStatusRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetRolesAsync(
        Guid userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default);
}

public interface IMembershipAdminService
{
    Task<OperationResult> SaveAsync(
        Guid userId,
        MembershipEditRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the real resolver over unsaved form values so the editor can show the effective
    /// status before anything is committed. Using the same resolver as everything else is the
    /// point: a preview computed separately would eventually disagree with reality.
    /// </summary>
    MembershipSnapshot Preview(MembershipEditRequest request);
}
