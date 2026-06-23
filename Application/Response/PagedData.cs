namespace Pointer.Application.Response;

public class PagedData<T>(IReadOnlyList<T> items, Pagination pagination)
{
    public IReadOnlyList<T> Items { get; } = items;
    public Pagination Pagination { get; } = pagination;
}
