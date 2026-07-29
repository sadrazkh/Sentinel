using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Common;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.Web.Areas.Admin.Models;

/// <summary>
/// The filter state of the user list, bound from the query string so a filtered view can be
/// bookmarked, shared and reloaded.
/// </summary>
public sealed class UserListFilterViewModel
{
    [StringLength(128)]
    public string? Search { get; set; }

    public UserAccountStatus? Status { get; set; }

    [StringLength(32)]
    public string? Role { get; set; }

    public bool? HasMembership { get; set; }

    /// <summary>Days ahead to look for memberships about to lapse. Null disables the filter.</summary>
    [Range(1, 365)]
    public int? ExpiringWithinDays { get; set; }

    public UserSortField SortBy { get; set; } = UserSortField.CreatedAt;

    public bool Descending { get; set; } = true;

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(PagingDefaults.MinPageSize, PagingDefaults.MaxPageSize)]
    public int PageSize { get; set; } = PagingDefaults.DefaultPageSize;

    public UserListRequest ToRequest(DateTimeOffset now) => new(
        Search,
        Status,
        Role,
        ExpiringWithinDays is { } days ? now.AddDays(days) : null,
        HasMembership,
        SortBy,
        Descending,
        Page,
        PageSize);

    /// <summary>Route values for pager and sort links, so no filter is lost when navigating.</summary>
    public Dictionary<string, string?> ToRouteValues(int? page = null, UserSortField? sortBy = null)
    {
        var values = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            values["search"] = Search;
        }

        if (Status is { } status)
        {
            values["status"] = status.ToString();
        }

        if (!string.IsNullOrWhiteSpace(Role))
        {
            values["role"] = Role;
        }

        if (HasMembership is { } hasMembership)
        {
            values["hasMembership"] = hasMembership ? "true" : "false";
        }

        if (ExpiringWithinDays is { } expiring)
        {
            values["expiringWithinDays"] = expiring.ToString();
        }

        var effectiveSort = sortBy ?? SortBy;
        values["sortBy"] = effectiveSort.ToString();

        // Clicking the column you are already sorted by flips the direction; a different
        // column starts descending, which is what "most recent first" means for every field
        // an operator is likely to reach for.
        values["descending"] = sortBy is { } requested
            ? (requested == SortBy ? !Descending : true).ToString().ToLowerInvariant()
            : Descending.ToString().ToLowerInvariant();

        values["page"] = (page ?? Page).ToString();

        if (PageSize != PagingDefaults.DefaultPageSize)
        {
            values["pageSize"] = PageSize.ToString();
        }

        return values;
    }
}

public sealed class UserListViewModel
{
    public required PagedResult<UserListItem> Results { get; init; }

    public required UserListFilterViewModel Filter { get; init; }

    public required string TimeZoneId { get; init; }

    public required bool CanWrite { get; init; }
}

public sealed class UserDetailViewModel
{
    public required UserDetail User { get; init; }

    public required string TimeZoneId { get; init; }

    public required bool CanWrite { get; init; }

    public required bool CanManageRoles { get; init; }

    /// <summary>True when the signed-in administrator is looking at their own account.</summary>
    public required bool IsSelf { get; init; }
}

public sealed class CreateUserViewModel
{
    [Required(ErrorMessage = "validation.required")]
    [StringLength(64, MinimumLength = 3, ErrorMessage = "validation.length")]
    [RegularExpression("^[a-zA-Z0-9._@+-]+$", ErrorMessage = "admin.validation.userNameCharacters")]
    [Display(Name = "admin.user.userName")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationUser.DisplayNameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [EmailAddress(ErrorMessage = "validation.email")]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.email")]
    public string Email { get; set; } = string.Empty;

    [StringLength(32, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.phone")]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Only a ceiling is enforced here. The real strength rules belong to Identity's password
    /// validator, which is configured in one place and stays authoritative.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [DataType(DataType.Password)]
    [Display(Name = "admin.user.password")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "admin.validation.passwordMismatch")]
    [Display(Name = "admin.user.passwordConfirm")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Display(Name = "admin.user.roles")]
    public List<string> Roles { get; set; } = [RoleNames.Member];

    [Required]
    [StringLength(ApplicationUser.CultureMaxLength)]
    public string PreferredCulture { get; set; } = "fa";

    [Required]
    [StringLength(ApplicationUser.TimeZoneMaxLength)]
    public string TimeZoneId { get; set; } = "Asia/Tehran";
}

public sealed class EditUserProfileViewModel
{
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationUser.DisplayNameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [EmailAddress(ErrorMessage = "validation.email")]
    [StringLength(256, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.email")]
    public string Email { get; set; } = string.Empty;

    [StringLength(32, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.phone")]
    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(ApplicationUser.CultureMaxLength)]
    public string PreferredCulture { get; set; } = "fa";

    [Required]
    [StringLength(ApplicationUser.TimeZoneMaxLength)]
    public string TimeZoneId { get; set; } = "Asia/Tehran";
}

public sealed class ChangeUserStatusViewModel
{
    public Guid UserId { get; set; }

    [Display(Name = "admin.user.status")]
    public UserAccountStatus Status { get; set; }

    /// <summary>Only meaningful for a suspension; ignored otherwise.</summary>
    [Display(Name = "admin.user.suspendedUntil")]
    [DataType(DataType.Date)]
    public DateTime? SuspendedUntil { get; set; }

    [StringLength(ApplicationUser.StatusNoteMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.user.statusNote")]
    public string? Note { get; set; }
}

public sealed class SetUserRolesViewModel
{
    public Guid UserId { get; set; }

    [Display(Name = "admin.user.roles")]
    public List<string> Roles { get; set; } = [];
}

public sealed class MembershipEditViewModel
{
    public Guid UserId { get; set; }

    [Display(Name = "admin.membership.tier")]
    public MembershipTier Tier { get; set; } = MembershipTier.Basic;

    [Display(Name = "admin.membership.adminState")]
    public MembershipAdminState AdminState { get; set; } = MembershipAdminState.Active;

    [Required(ErrorMessage = "validation.required")]
    [DataType(DataType.Date)]
    [Display(Name = "admin.membership.startsAt")]
    public DateTime StartsAt { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "admin.membership.endsAt")]
    public DateTime? EndsAt { get; set; }

    [Range(0, 90, ErrorMessage = "validation.range")]
    [Display(Name = "admin.membership.graceOverride")]
    public int? GracePeriodDaysOverride { get; set; }

    [StringLength(Membership.NotesMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.membership.notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Round-tripped through the form. A stale value means somebody else edited this
    /// membership since the page was rendered, and the save is refused rather than applied
    /// on top of their change.
    /// </summary>
    public Guid? ConcurrencyToken { get; set; }
}
