using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class UpdateCommentStatusRequest
{
    public CommentStatus Status { get; set; }
    public string? Reply { get; set; }
    public string? AppliedByLabel { get; set; }
    // The commit that applied this comment — see Comment.CommitUrl and skill.md's apply flow.
    public string? CommitUrl { get; set; }

    /// <summary>
    /// The raw commit sha, alongside CommitUrl. The URL is for a human to click; this is what
    /// deploy detection tests for ancestry against a deployed build, which a URL cannot answer.
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>
    /// The automated tool posting the accompanying <see cref="Reply"/> (e.g. "claude-code"), for the
    /// AI apply flow (`apply --mark`) only — ignored (stored null) when the caller is a human surface.
    /// See CommentService.Normalize.
    /// </summary>
    public string? AiTool { get; set; }

    /// <summary>The model id the tool reported running as (e.g. "claude-sonnet-5"). Same rule as
    /// <see cref="AiTool"/>.</summary>
    public string? AiModel { get; set; }
}
