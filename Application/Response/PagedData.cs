using Pointer.Application.DTOs.Comment;

namespace Pointer.Application.Response;

public class PagedData<T>(
    IReadOnlyList<T> items,
    Pagination pagination,
    int? hiddenPrivateCount = null,
    IReadOnlyDictionary<int, PageContextDto>? pageContexts = null,
    IReadOnlyDictionary<string, ApplyPageDto>? pages = null,
    IReadOnlyDictionary<string, string>? userAgents = null)
{
    public IReadOnlyList<T> Items { get; } = items;
    public Pagination Pagination { get; } = pagination;

    // Number of private comments hidden from the caller (private + not authored
    // by them) under the current filters. Null (omitted) when not applicable.
    public int? HiddenPrivateCount { get; } = hiddenPrivateCount;

    // Keyed by PageContextId — populated only on the comment-list/apply-queue endpoints, null
    // elsewhere. Dedupes console/network payloads across bug-flagged comments sharing a page.
    public IReadOnlyDictionary<int, PageContextDto>? PageContexts { get; } = pageContexts;

    // Apply-queue-only compaction dictionaries (null elsewhere). Keyed by `route + deviceType`
    // (not route alone — two comments on the same route from different devices must not collide)
    // and by a short "u1" ref respectively. See ApplyElementDto.PageRef/PageDto.UaRef.
    public IReadOnlyDictionary<string, ApplyPageDto>? Pages { get; } = pages;
    public IReadOnlyDictionary<string, string>? UserAgents { get; } = userAgents;
}
