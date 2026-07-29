namespace Sentinel.Application.Users;

public sealed record RoleSummary(string Name, string? Description, int MemberCount);

public interface IRoleSummaryQuery
{
    /// <summary>
    /// Every role with how many accounts hold it, from one grouped query rather than a
    /// per-role membership lookup.
    /// </summary>
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default);
}
