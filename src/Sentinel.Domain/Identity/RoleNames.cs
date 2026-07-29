namespace Sentinel.Domain.Identity;

/// <summary>
/// Roles are coarse identity buckets. Fine-grained rules live in authorization policies
/// (see <c>Sentinel.Application.Authorization.PolicyNames</c>) so that permissions can be
/// re-shaped later without renaming roles that are already assigned in the database.
/// </summary>
public static class RoleNames
{
    /// <summary>Full control, including role assignment and system settings.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>Manages users, memberships, applications and entitlements.</summary>
    public const string Admin = "Admin";

    /// <summary>Read-only access to user and audit data for troubleshooting.</summary>
    public const string Support = "Support";

    /// <summary>An ordinary portal customer.</summary>
    public const string Member = "Member";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Admin, Support, Member];

    /// <summary>Roles that may reach any part of the admin area.</summary>
    public static readonly IReadOnlyList<string> BackOffice = [SuperAdmin, Admin, Support];
}
