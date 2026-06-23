using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentResponse
{
    public int Id { get; set; }
    public CommentStatus Status { get; set; }
    public EnvironmentTag Environment { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public string? AppliedByLabel { get; set; }
    public ElementCaptureDto Element { get; set; } = new();
    public List<ReplyResponse> Replies { get; set; } = new();
}
