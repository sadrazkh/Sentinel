namespace Sentinel.Vpn.Panel;

/// <summary>
/// Where a panel lives and how to authenticate to it.
/// <para>
/// Passed per call rather than held by the client, because the portal talks to many panels and a
/// client bound to one would need an instance per server. The token is decrypted only for the
/// duration of the call.
/// </para>
/// </summary>
public sealed record PanelEndpoint(string BaseUrl, string ApiToken)
{
    /// <summary>Never include the token in a string representation — these get logged.</summary>
    public override string ToString() => $"PanelEndpoint {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>
/// The portal's whole surface against a 3x-ui panel.
/// <para>
/// Only the endpoints the portal actually needs, and only through the panel's documented API. The
/// panel's database is never touched, and the portal is never a proxy for arbitrary panel paths:
/// both would mean shipping a general-purpose remote-control channel, which is a far larger thing
/// to secure than the handful of operations provisioning really requires.
/// </para>
/// <para>
/// Clients are addressed by e-mail because that is the panel's own natural key in 3.x — one client
/// can be attached to several inbounds, and the e-mail is what identifies it across all of them.
/// </para>
/// </summary>
public interface IThreeXUiClient
{
    /// <summary>Confirms the panel answers and the token is accepted. Used by the health check.</summary>
    Task<PanelResult<PanelStatus>> GetStatusAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the panel's inbounds, so an operator can choose which to allowlist rather than typing
    /// ids by hand. Uses the slim variant: the full one carries every client on every inbound.
    /// </summary>
    Task<PanelResult<IReadOnlyList<PanelInbound>>> ListInboundsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default);

    Task<PanelResult<PanelClient>> GetClientAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default);

    Task<PanelResult<PanelClientTraffic>> GetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a client and attaches it to the given inbounds in one call.
    /// <para>
    /// A <see cref="PanelOutcome.UnknownOutcome"/> here must never be blind-retried: the client may
    /// already exist. Reconcile with <see cref="GetClientAsync"/> first.
    /// </para>
    /// </summary>
    Task<PanelResult<PanelClient>> CreateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a client.
    /// <para>
    /// The panel <b>replaces</b> the row rather than patching it, so the request must carry every
    /// field that should survive. Sending a partial update silently wipes what was left out.
    /// </para>
    /// </summary>
    Task<PanelResult<PanelClient>> UpdateClientAsync(
        PanelEndpoint endpoint,
        PanelClientRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches an existing client to further inbounds.
    /// <para>
    /// This plus <see cref="DetachAsync"/> is what makes moving a service between servers safe:
    /// attach at the destination, verify, then detach at the source. Deleting and recreating would
    /// leave a window with no working configuration at all.
    /// </para>
    /// </summary>
    Task<PanelResult<bool>> AttachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default);

    Task<PanelResult<bool>> DetachAsync(
        PanelEndpoint endpoint,
        string email,
        IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every configuration URL for a client, across all its inbounds — the same strings the
    /// panel's own copy button produces.
    /// </summary>
    Task<PanelResult<IReadOnlyList<string>>> GetClientLinksAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>Zeroes a client's counters and re-enables it across every attached inbound.</summary>
    Task<PanelResult<bool>> ResetTrafficAsync(
        PanelEndpoint endpoint,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a client.
    /// <para>
    /// <paramref name="keepTraffic"/> leaves the usage record behind, which matters when a service
    /// is being moved rather than ended: the counters are the customer's remaining allowance.
    /// </para>
    /// </summary>
    Task<PanelResult<bool>> DeleteClientAsync(
        PanelEndpoint endpoint,
        string email,
        bool keepTraffic,
        CancellationToken cancellationToken = default);

    /// <summary>The e-mails currently connected, deduped by the panel across its nodes.</summary>
    Task<PanelResult<IReadOnlyList<string>>> GetOnlineClientsAsync(
        PanelEndpoint endpoint,
        CancellationToken cancellationToken = default);
}
