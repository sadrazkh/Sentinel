namespace Sentinel.Application.Access;

/// <summary>
/// The single authority for "may this member use this application?".
/// <para>
/// Every caller — the catalogue listing, the launch endpoint, and anything added later —
/// goes through here, so a new rule is added in one place and takes effect everywhere at
/// once. The service only assembles the inputs; the rules themselves live in
/// <see cref="AccessRuleEvaluator"/>.
/// </para>
/// </summary>
public interface IAccessDecisionService
{
    Task<AccessDecision> EvaluateAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The member's whole catalogue with a decision attached to each entry, from one read.
    /// </summary>
    Task<PortalCatalog> GetCatalogAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a launch by application key. Returns <c>null</c> when no such application
    /// exists. The destination URL is included only if the launch is permitted.
    /// </summary>
    Task<LaunchResolution?> ResolveLaunchAsync(
        Guid userId,
        string applicationKey,
        CancellationToken cancellationToken = default);
}
