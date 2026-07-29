namespace Sentinel.Domain.Auditing;

/// <summary>
/// Stable, greppable identifiers for audited operations. Stored as strings so that new
/// actions never require a migration, and so log analysis does not need an enum map.
/// </summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string LoginBlocked = "auth.login.blocked";
    public const string Logout = "auth.logout";
    public const string LogoutAllDevices = "auth.logout_all";
    public const string PasswordChanged = "auth.password.changed";
    public const string PasswordResetByAdmin = "auth.password.reset_by_admin";

    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserStatusChanged = "user.status.changed";
    public const string UserRolesChanged = "user.roles.changed";

    public const string MembershipCreated = "membership.created";
    public const string MembershipUpdated = "membership.updated";

    public const string ApplicationCreated = "application.created";
    public const string ApplicationUpdated = "application.updated";
    public const string ApplicationIconChanged = "application.icon.changed";

    public const string EntitlementGranted = "entitlement.granted";
    public const string EntitlementRevoked = "entitlement.revoked";
    public const string EntitlementUpdated = "entitlement.updated";

    public const string ApplicationLaunched = "application.launched";
    public const string ApplicationLaunchDenied = "application.launch.denied";

    public const string SettingsUpdated = "settings.updated";
}
