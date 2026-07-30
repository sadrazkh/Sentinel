namespace Sentinel.Vpn.Panel;

/// <summary>
/// How a panel call ended.
/// <para>
/// <see cref="UnknownOutcome"/> is the important one. A timeout or a dropped connection on a
/// <em>write</em> means we do not know whether the panel applied it — and the wrong move is to
/// retry, because the first attempt may have succeeded and a second would create a duplicate or
/// double-charge. It is a distinct outcome so no caller can accidentally treat it as a failure.
/// </para>
/// </summary>
public enum PanelOutcome
{
    Success = 0,

    /// <summary>The panel answered and refused. Safe to treat as final.</summary>
    Rejected = 1,

    /// <summary>The token was refused. Final, and worth alerting on: the credential is wrong.</summary>
    Unauthorized = 2,

    /// <summary>The panel answered that the thing does not exist.</summary>
    NotFound = 3,

    /// <summary>
    /// The request may or may not have been applied — timeout, connection reset, unparseable
    /// response. Never blind-retry: reconcile first.
    /// </summary>
    UnknownOutcome = 4,

    /// <summary>The address failed the SSRF policy, so nothing was sent at all. Always safe.</summary>
    Blocked = 5,
}

/// <summary>
/// The result of one panel call.
/// <para>
/// A result type rather than exceptions: a panel being unreachable is an ordinary operating
/// condition for this system, not a fault, and the distinction between "refused" and "unknown"
/// has to survive to the caller — an exception loses it the moment somebody writes a broad catch.
/// </para>
/// </summary>
public sealed record PanelResult<T>(PanelOutcome Outcome, T? Value, string? Message)
{
    public bool IsSuccess => Outcome == PanelOutcome.Success;

    /// <summary>True when the call definitely did not change anything, so a retry is safe.</summary>
    public bool IsDefinitelyUnapplied =>
        Outcome is PanelOutcome.Rejected or PanelOutcome.Unauthorized
            or PanelOutcome.NotFound or PanelOutcome.Blocked;

    public static PanelResult<T> Success(T value) => new(PanelOutcome.Success, value, null);

    public static PanelResult<T> Failure(PanelOutcome outcome, string? message = null) =>
        new(outcome, default, message);
}

/// <summary>One inbound as the panel reports it.</summary>
public sealed record PanelInbound(
    int Id,
    string Remark,
    string Protocol,
    bool Enable,
    int Port);

/// <summary>
/// A client's traffic counters, from <c>GET /panel/api/clients/traffic/{email}</c>.
/// <para>
/// The panel's <c>totalGB</c> is in <b>bytes</b> despite its name, and its timestamps are epoch
/// milliseconds. Both are converted at the client boundary so nothing downstream has to remember.
/// </para>
/// </summary>
public sealed record PanelClientTraffic(
    string Email,
    long UploadBytes,
    long DownloadBytes,
    long TotalAllowanceBytes,
    bool Enabled,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastOnlineAt,
    int InboundId)
{
    public long UsedBytes => UploadBytes + DownloadBytes;

    /// <summary><c>null</c> when the allowance is unlimited, which the panel expresses as zero.</summary>
    public long? RemainingBytes =>
        TotalAllowanceBytes <= 0 ? null : Math.Max(0, TotalAllowanceBytes - UsedBytes);

    public bool IsUnlimited => TotalAllowanceBytes <= 0;
}

/// <summary>
/// What the portal asks the panel to create.
/// <para>
/// Deliberately narrow. There is no UUID, no password, no protocol and no inbound-side setting
/// here: the panel generates every per-protocol secret itself when we omit them, and the inbound
/// already fixes the protocol. That means a member can never influence their own credential, and
/// the portal never has to hold one.
/// </para>
/// </summary>
public sealed record PanelClientRequest(
    string Email,
    IReadOnlyList<int> InboundIds,
    /// <summary>Bytes. Zero means unlimited — the panel's own convention.</summary>
    long TotalAllowanceBytes,
    DateTimeOffset? ExpiresAt,
    int IpLimit,
    bool Enabled);

/// <summary>A client as the panel reports it, including which inbounds it is attached to.</summary>
public sealed record PanelClient(
    string Email,
    string? SubscriptionId,
    bool Enabled,
    long TotalAllowanceBytes,
    DateTimeOffset? ExpiresAt,
    int IpLimit,
    IReadOnlyList<int> InboundIds);

/// <summary>Coarse panel status, for the health check.</summary>
public sealed record PanelStatus(bool XrayRunning, string? XrayVersion);
