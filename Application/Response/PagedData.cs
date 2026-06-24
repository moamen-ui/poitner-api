namespace Pointer.Application.Response;

public class PagedData<T>(IReadOnlyList<T> items, Pagination pagination, int? hiddenPrivateCount = null)
{
    public IReadOnlyList<T> Items { get; } = items;
    public Pagination Pagination { get; } = pagination;

    // Number of private comments hidden from the caller (private + not authored
    // by them) under the current filters. Null (omitted) when not applicable.
    public int? HiddenPrivateCount { get; } = hiddenPrivateCount;
}
