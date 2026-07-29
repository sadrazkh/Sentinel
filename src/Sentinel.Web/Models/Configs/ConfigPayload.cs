using Microsoft.Extensions.Localization;
using Sentinel.Application.Subscriptions;

namespace Sentinel.Web.Models.Configs;

/// <summary>
/// One config as the browser receives it.
/// <para>
/// <see cref="Uri"/> carries the member's own proxy credentials. It is sent because copying it
/// is the entire point of the page — but only ever to its owner, and it is never logged, never
/// audited, and never rendered anywhere an operator can see it.
/// </para>
/// </summary>
public sealed record ConfigPayload(
    string Protocol,
    string Name,
    string? Endpoint,
    string? Network,
    string? Security,
    string? Sni,
    string Uri)
{
    public static ConfigPayload From(ProxyConfig config) => new(
        config.Protocol.ToString().ToUpperInvariant(),
        config.DisplayName,
        config.Endpoint,
        config.Network,
        config.Security,
        config.Sni,
        config.RawUri);
}

public sealed record ConfigGridLabels(
    string Endpoint,
    string Network,
    string Security,
    string Sni,
    string Copy,
    string Copied,
    string CopyFailed,
    string CopyAll,
    string SearchLabel,
    string SearchPlaceholder,
    string NoMatches)
{
    public static ConfigGridLabels From(IStringLocalizer localizer) => new(
        localizer["configs.endpoint"].Value,
        localizer["configs.network"].Value,
        localizer["configs.security"].Value,
        localizer["configs.sni"].Value,
        localizer["configs.copy"].Value,
        localizer["configs.copied"].Value,
        localizer["configs.copyFailed"].Value,
        localizer["configs.copyAll"].Value,
        localizer["configs.search.label"].Value,
        localizer["configs.search.placeholder"].Value,
        localizer["configs.noMatches"].Value);
}
