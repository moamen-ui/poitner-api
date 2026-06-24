using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
namespace Pointer.Domain.Entity;

public class Comment : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public EnvironmentTag Environment { get; set; }
    public CommentStatus Status { get; set; } = CommentStatus.Open;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public ElementCapture Element { get; set; } = new();
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public string? AppliedByLabel { get; set; }
    // Edit trace: stamped when the author edits the comment body / removes its image.
    public DateTime? EditedAt { get; set; }
    public Guid? EditedBy { get; set; }
    public ICollection<Reply> Replies { get; set; } = new List<Reply>();
}
