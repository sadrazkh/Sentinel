using Sentinel.Application.Accounts;
using Sentinel.Application.Common;

namespace Sentinel.Web.Models.Account;

public sealed class ActivityViewModel
{
    public required PagedResult<ActivityEntry> History { get; init; }

    public required string TimeZoneId { get; init; }
}
