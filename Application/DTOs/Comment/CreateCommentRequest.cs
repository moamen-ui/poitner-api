using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CreateCommentRequest
{
    public string Body { get; set; } = string.Empty;
    public EnvironmentTag Environment { get; set; }
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Optional picked predefined actions (multi-select). The server validates each is active +
    /// in-scope for the resolved project, then snapshots {text, prompt} onto the comment. Any
    /// invalid/out-of-scope id rejects the request (not silently dropped).
    /// </summary>
    public List<int>? PredefinedActionIds { get; set; }

    public ElementCaptureDto Element { get; set; } = new();
}
