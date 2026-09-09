using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class UpdateCommentStatusRequest
{
    public CommentStatus Status { get; set; }
    public string? Reply { get; set; }
    public string? AppliedByLabel { get; set; }
    // The commit that applied this comment — see Comment.CommitUrl and skill.md's apply flow.
    public string? CommitUrl { get; set; }
}
