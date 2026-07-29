namespace Sentinel.Application.Authorization;

/// <summary>
/// Every non-anonymous endpoint names one of these. Policies — not raw role checks —
/// are used at call sites so the rule behind a policy can change (extra role, claim,
/// permission table) without touching a single controller.
/// </summary>
public static class PolicyNames
{
    /// <summary>Signed in, account healthy, session still live. The floor for the portal.</summary>
    public const string ActiveUser = "policy.active-user";

    /// <summary>Read access to the admin area: SuperAdmin, Admin or Support.</summary>
    public const string BackOfficeRead = "policy.backoffice.read";

    /// <summary>Mutating admin operations: SuperAdmin or Admin. Support is read-only.</summary>
    public const string BackOfficeWrite = "policy.backoffice.write";

    /// <summary>Role assignment and system settings. SuperAdmin only.</summary>
    public const string SystemAdministration = "policy.system.administration";
}
