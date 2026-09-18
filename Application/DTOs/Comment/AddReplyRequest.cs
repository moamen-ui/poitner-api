namespace Pointer.Application.DTOs.Comment;

public class AddReplyRequest
{
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// The automated tool posting this reply (e.g. "claude-code"), for the AI apply flow only —
    /// ignored (stored null) when the caller is a human surface (widget/dashboard). See
    /// CommentService.Normalize.
    /// </summary>
    public string? AiTool { get; set; }

    /// <summary>The model id the tool reported running as (e.g. "claude-sonnet-5"). Same rule as
    /// <see cref="AiTool"/>.</summary>
    public string? AiModel { get; set; }
}
