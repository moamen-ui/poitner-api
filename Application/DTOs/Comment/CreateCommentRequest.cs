using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CreateCommentRequest
{
    public string Body { get; set; } = string.Empty;
    public EnvironmentTag Environment { get; set; }
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Optional picked predefined action. The server validates it is active + in-scope for the
    /// resolved project, then snapshots {text, prompt} onto the comment. An invalid/out-of-scope
    /// id is rejected (not silently dropped).
    /// </summary>
    public int? PredefinedActionId { get; set; }

    public ElementCaptureDto Element { get; set; } = new();
}
