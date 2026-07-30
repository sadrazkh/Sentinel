using Sentinel.Application.Common;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Servers;

/// <summary>
/// A server row for the operator's list.
/// <para>
/// Carries the token hint, never the token. Once saved, the credential is write-only as far as the
/// portal's own UI is concerned — an operator who needs a different one enters a new one.
/// </para>
/// </summary>
public sealed record VpnServerListItem(
    Guid Id,
    string Key,
    string NameFa,
    string NameEn,
    string CountryCode,
    string BaseUrlHost,
    VpnServerStatus Status,
    VpnServerHealth Health,
    string? ApiTokenHint,
    DateTimeOffset? LastHealthCheckAt,
    string? LastHealthError,
    int MaxClients,
    int ReservedClients,
    int SelectionPriority,
    int EnabledInboundCount,
    DateTimeOffset UpdatedAt)
{
    public int RemainingCapacity => Math.Max(0, MaxClients - ReservedClients);

    /// <summary>
    /// A server with no allowlisted inbound cannot take a service no matter what its status says,
    /// so the list flags it rather than leaving an operator to work it out from a zero.
    /// </summary>
    public bool IsMisconfigured =>
        Status == VpnServerStatus.Active && (EnabledInboundCount == 0 || MaxClients <= 0);
}

public sealed record VpnServerEditModel(
    Guid Id,
    string Key,
    string NameFa,
    string NameEn,
    string CountryCode,
    string BaseUrl,
    string? ApiTokenHint,
    VpnServerStatus Status,
    int MaxClients,
    int SelectionPriority,
    string? Notes,
    Guid? ConcurrencyToken);

/// <summary>
/// A save request.
/// <para>
/// <see cref="ApiToken"/> is <c>null</c> to mean "leave the stored one alone". That is what lets an
/// operator change a server's capacity without re-typing a credential they cannot read back.
/// </para>
/// </summary>
public sealed record VpnServerSaveRequest(
    string Key,
    string NameFa,
    string NameEn,
    string CountryCode,
    string BaseUrl,
    string? ApiToken,
    VpnServerStatus Status,
    int MaxClients,
    int SelectionPriority,
    string? Notes,
    Guid? ConcurrencyToken);

/// <summary>One inbound as offered for allowlisting, paired with whether it is already allowlisted.</summary>
public sealed record DiscoveredInbound(
    int InboundId,
    string Remark,
    string Protocol,
    bool EnabledOnPanel,
    int Port,
    bool AlreadyAllowlisted);

/// <summary>The outcome of probing a server: what the panel said, and what the portal recorded.</summary>
public sealed record ServerProbeResult(
    bool Reachable,
    VpnServerHealth Health,
    string? Error,
    bool XrayRunning,
    string? XrayVersion,
    int InboundCount);

public static class VpnServerErrors
{
    public const string NotFound = "admin.error.vpnServerNotFound";
    public const string KeyTaken = "admin.error.vpnServerKeyTaken";
    public const string KeyInvalid = "admin.error.vpnServerKeyInvalid";
    public const string BaseUrlInvalid = "admin.error.vpnServerUrlInvalid";
    public const string TokenRequired = "admin.error.vpnServerTokenRequired";
    public const string TokenUnreadable = "admin.error.vpnServerTokenUnreadable";
    public const string CountryInvalid = "admin.error.vpnServerCountryInvalid";
    public const string InboundNotOnPanel = "admin.error.vpnInboundNotOnPanel";
    public const string ServerHasServices = "admin.error.vpnServerHasServices";
}

public interface IVpnServerAdminService
{
    Task<OperationResult<Guid>> SaveAsync(
        Guid? serverId,
        VpnServerSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Contacts the panel and records what came back.
    /// <para>
    /// Also the only way a server leaves <see cref="VpnServerStatus.Unverified"/>: a server nobody
    /// has successfully reached is never handed a customer's service.
    /// </para>
    /// </summary>
    Task<OperationResult<ServerProbeResult>> ProbeAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the panel's inbounds so an operator can choose, rather than typing ids.</summary>
    Task<OperationResult<IReadOnlyList<DiscoveredInbound>>> DiscoverInboundsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an inbound to the allowlist, after confirming it exists on the panel.</summary>
    Task<OperationResult> AllowlistInboundAsync(
        Guid serverId,
        int inboundId,
        string label,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetInboundEnabledAsync(
        Guid profileId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveInboundAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a server's endpoint for the modules that provision against it. Returns
    /// <c>null</c> when the server is gone or its stored token can no longer be decrypted.
    /// </summary>
    Task<Panel.PanelEndpoint?> ResolveEndpointAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}

public interface IVpnServerAdminQuery
{
    Task<IReadOnlyList<VpnServerListItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<VpnServerEditModel?> GetForEditAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerInboundProfile>> ListInboundsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}
