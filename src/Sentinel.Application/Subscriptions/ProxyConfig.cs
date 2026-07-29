namespace Sentinel.Application.Subscriptions;

public enum ProxyProtocol
{
    Unknown = 0,
    Vless = 1,
    Vmess = 2,
    Trojan = 3,
    Shadowsocks = 4,
    Hysteria2 = 5,
    Tuic = 6,
}

/// <summary>
/// One entry from a subscription, as far as it can be understood without pretending to be a
/// VPN client.
/// <para>
/// <see cref="RawUri"/> is the original line, kept because it is what the member copies into
/// their own client — it is the payload, not a detail. It also carries their credentials, so
/// it is never logged, never audited, and never leaves the response to its owner.
/// </para>
/// </summary>
public sealed record ProxyConfig(
    ProxyProtocol Protocol,
    string Remark,
    string? Host,
    int? Port,
    /// <summary>tls, reality, or none — whatever the entry declares.</summary>
    string? Security,
    /// <summary>Transport: tcp, ws, grpc, xhttp…</summary>
    string? Network,
    string? Sni,
    string RawUri)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? (Host ?? "—") : Remark;

    public string? Endpoint => Host is null ? null : Port is null ? Host : $"{Host}:{Port}";
}

/// <summary>
/// The <c>subscription-userinfo</c> response header, which is how a provider reports quota and
/// expiry. Present on every mainstream panel, and the only reliable way to know a subscription
/// has run out without guessing from the remark text.
/// </summary>
public sealed record SubscriptionUserInfo(
    long? UploadBytes,
    long? DownloadBytes,
    long? TotalBytes,
    DateTimeOffset? ExpiresAt)
{
    public static readonly SubscriptionUserInfo Empty = new(null, null, null, null);

    public long? UsedBytes => UploadBytes is null && DownloadBytes is null
        ? null
        : (UploadBytes ?? 0) + (DownloadBytes ?? 0);

    public long? RemainingBytes => TotalBytes is null || UsedBytes is null
        ? null
        // A provider can report more used than the quota; a negative "remaining" helps nobody.
        : Math.Max(0, TotalBytes.Value - UsedBytes.Value);

    /// <summary>0–100, or <c>null</c> when the provider reports no quota (unlimited).</summary>
    public int? UsedPercent => TotalBytes is null or 0 || UsedBytes is null
        ? null
        : (int)Math.Clamp(UsedBytes.Value * 100 / TotalBytes.Value, 0, 100);

    public bool IsExpiredAt(DateTimeOffset instant) => ExpiresAt is { } expires && expires <= instant;

    public bool IsQuotaExhausted => RemainingBytes is 0 && TotalBytes is > 0;
}

/// <summary>The whole outcome of reading one subscription.</summary>
public sealed record SubscriptionContent(
    IReadOnlyList<ProxyConfig> Configs,
    SubscriptionUserInfo UserInfo,
    string? ProfileTitle)
{
    public static readonly SubscriptionContent Empty =
        new([], SubscriptionUserInfo.Empty, null);
}
