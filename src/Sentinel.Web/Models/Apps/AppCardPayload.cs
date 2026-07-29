using System.Globalization;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Access;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Models.Apps;

/// <summary>
/// One card as the browser receives it. Every string is already translated: the server owns
/// the message catalogue, so the island never has to.
/// <para>
/// There is no destination URL here — <see cref="OpenUrl"/> points at the portal's own launch
/// endpoint, which makes the decision again before redirecting anywhere.
/// </para>
/// </summary>
public sealed record AppCardPayload(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? IconUrl,
    bool IsBeta,
    bool CanLaunch,
    /// <summary>
    /// Present only when the card is launchable. A locked card carries no link at all, so the
    /// payload describes exactly what the member may do and nothing more.
    /// </summary>
    string? OpenUrl,
    string BadgeClass,
    string BadgeLabel,
    string? TierLabel,
    string? Reason)
{
    public static AppCardPayload From(
        ApplicationCard card,
        IStringLocalizer localizer,
        CultureInfo culture,
        Func<string, string> openUrlFor)
    {
        var isPersian = culture.TwoLetterISOLanguageName == "fa";
        var (badgeClass, badgeLabelKey) = AccessPresentation.CardStatusBadge(card);

        return new AppCardPayload(
            card.Id,
            card.Key,
            isPersian ? card.NameFa : card.NameEn,
            isPersian ? card.DescriptionFa : card.DescriptionEn,
            ResolveIconUrl(card.IconPath),
            card.IsBeta,
            card.CanLaunch,
            card.CanLaunch ? openUrlFor(card.Key) : null,
            badgeClass,
            localizer[badgeLabelKey].Value,
            card.MinimumTier is { } tier ? localizer[AccessPresentation.TierKey(tier)].Value : null,
            card.CanLaunch
                ? null
                : localizer[AccessPresentation.DenialReasonKey(card.Decision.Reason)].Value);
    }

    /// <summary>
    /// Only root-relative paths are emitted. Anything else stored in the column — an absolute
    /// URL, a traversal attempt — is dropped and the card falls back to its letter avatar,
    /// so a bad row cannot turn into an off-site image request that leaks the viewer's address.
    /// </summary>
    private static string? ResolveIconUrl(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return null;
        }

        var trimmed = iconPath.Trim();

        var isSafe = trimmed.StartsWith('/')
                     && !trimmed.StartsWith("//", StringComparison.Ordinal)
                     && !trimmed.Contains("..", StringComparison.Ordinal);

        return isSafe ? trimmed : null;
    }
}

/// <summary>The handful of UI strings the island renders that do not belong to a single card.</summary>
public sealed record AppGridLabels(
    string Open,
    string Beta,
    string SearchLabel,
    string SearchPlaceholder,
    string FilterLabel,
    Dictionary<string, string> Filters,
    string EmptyTitle,
    string EmptyBody,
    string EmptyFilteredTitle,
    string EmptyFilteredBody,
    string ClearFilters)
{
    public static AppGridLabels From(IStringLocalizer localizer) => new(
        localizer["apps.open"].Value,
        localizer["appBadge.beta"].Value,
        localizer["apps.search.label"].Value,
        localizer["apps.search.placeholder"].Value,
        localizer["apps.filter.label"].Value,
        new Dictionary<string, string>
        {
            ["all"] = localizer["apps.filter.all"].Value,
            ["available"] = localizer["apps.filter.available"].Value,
            ["locked"] = localizer["apps.filter.locked"].Value,
        },
        localizer["apps.empty.title"].Value,
        localizer["apps.empty.body"].Value,
        localizer["apps.emptyFiltered.title"].Value,
        localizer["apps.emptyFiltered.body"].Value,
        localizer["apps.clearFilters"].Value);
}
