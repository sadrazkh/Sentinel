namespace Sentinel.Application.Common;

/// <summary>
/// One page of results plus the total, so the UI can render a pager without a second query
/// shape. Paging is always done in the database — an admin list must never load every user
/// into memory to slice it.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public static PagedResult<T> Empty(int pageSize) => new([], 1, pageSize, 0);

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    /// <summary>1-based index of the first item on this page, for "showing 21–40 of 137".</summary>
    public int FirstItemNumber => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int LastItemNumber => Math.Min(Page * PageSize, TotalCount);
}

/// <summary>
/// Bounds every paged request. A caller-supplied page size is otherwise a denial-of-service
/// knob: <c>?pageSize=1000000</c> would ask the database for the whole table.
/// </summary>
public static class PagingDefaults
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MinPageSize = 5;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) =>
        pageSize <= 0 ? DefaultPageSize : Math.Clamp(pageSize, MinPageSize, MaxPageSize);
}
