using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Subscriptions;

namespace Sentinel.Web.Models.Configs;

public sealed class ConfigsViewModel
{
    public required IReadOnlyList<SubscriptionView> Subscriptions { get; init; }

    public required string TimeZoneId { get; init; }

    public required bool CanAddOwn { get; init; }

    public required int MaxSources { get; init; }

    public int TotalConfigs => Subscriptions.Sum(s => s.Configs.Count);
}

public sealed class AddSubscriptionViewModel
{
    [StringLength(120, ErrorMessage = "validation.tooLong")]
    [Display(Name = "configs.add.title")]
    public string? Title { get; set; }

    /// <summary>
    /// Only length and presence here. Scheme, host and port rules live in
    /// <see cref="SubscriptionUrlPolicy"/>, which the service applies — duplicating them in an
    /// attribute would create a second copy that can drift from the one that matters.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(SubscriptionUrlPolicy.MaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "configs.add.url")]
    public string Url { get; set; } = string.Empty;
}
