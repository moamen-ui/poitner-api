using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Response-side page context: console errors/warnings and failed/slow network requests captured on
/// one page during one browser-tab visit. Shared by every bug-flagged comment on that page/visit —
/// see PagedData&lt;T&gt;.PageContexts (paged endpoints, referenced by id) and CommentResponse.PageContext
/// (single-item endpoint, embedded inline).
/// </summary>
public class PageContextDto
{
    public int Id { get; set; }
    public string Route { get; set; } = string.Empty;
    public EnvironmentTag Environment { get; set; }
    public DateTime LastEventAt { get; set; }
    public List<ConsoleEntryDto> ConsoleEntries { get; set; } = new();
    public List<NetworkEntryDto> NetworkEntries { get; set; } = new();
}

public class ConsoleEntryDto
{
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }
    public int Count { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class NetworkEntryDto
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    public DateTime OccurredAt { get; set; }
}
