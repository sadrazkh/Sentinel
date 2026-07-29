using System.ComponentModel.DataAnnotations;
using Sentinel.Application.Common;
using Sentinel.Application.Subscriptions;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class SubscriptionAdminViewModel
{
    public required PagedResult<SubscriptionAdminRow> Results { get; init; }

    public string? Search { get; init; }

    public bool OnlyDead { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }

    public required DateTimeOffset Now { get; init; }

    public int DeadCount => Results.Items.Count(row => row.IsDeadAt(Now));

    public Dictionary<string, string?> ToRouteValues(int page) => new()
    {
        ["search"] = Search,
        ["onlyDead"] = OnlyDead ? "true" : null,
        ["page"] = page.ToString(),
        ["pageSize"] = Results.PageSize.ToString(),
    };
}

public sealed class AddSubscriptionForUserViewModel
{
    public Guid UserId { get; set; }

    [StringLength(120, ErrorMessage = "validation.tooLong")]
    public string? Title { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(SubscriptionUrlPolicy.MaxLength, ErrorMessage = "validation.tooLong")]
    public string Url { get; set; } = string.Empty;

    [StringLength(512, ErrorMessage = "validation.tooLong")]
    public string? Notes { get; set; }
}
