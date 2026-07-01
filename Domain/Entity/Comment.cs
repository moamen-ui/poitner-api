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
    public Guid? OwnerId { get; set; }

    // Predefined-action snapshot (v1 single-select). We snapshot {text, prompt} at
    // create time rather than FK to PredefinedAction so (1) the prompt never has to be
    // re-resolved (and never reaches the browser via a join), and (2) editing/deleting an
    // action definition later does not rewrite historical comments.
    // FUTURE (multi-select): these migrate into a child CommentAction[] collection.
    public string? PickedActionText { get; set; }
    public string? PickedActionPrompt { get; set; }

    public ICollection<Reply> Replies { get; set; } = new List<Reply>();
}
