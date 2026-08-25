namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Ingestion-side page context, sent on CreateCommentRequest only when the visitor checked
/// "Report as a bug". Sibling of Element, not nested inside it. Server-side, this is only ever
/// persisted when both the comment is flagged (IsBugReport) AND the owning project has
/// PageContextCaptureEnabled — see CommentService.CreateAsync.
/// </summary>
public class PageContextCaptureDto
{
    public string SessionId { get; set; } = string.Empty;
    public List<ConsoleEntryInputDto> ConsoleEntries { get; set; } = new();
    public List<NetworkEntryInputDto> NetworkEntries { get; set; } = new();
}

public class ConsoleEntryInputDto
{
    public string Level { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }
    public int Count { get; set; } = 1;
    public DateTime? OccurredAt { get; set; }
}

public class NetworkEntryInputDto
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    public DateTime? OccurredAt { get; set; }
}
