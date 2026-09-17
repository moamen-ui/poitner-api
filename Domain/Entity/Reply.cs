namespace Pointer.Domain.Entity;

public class Reply : BaseEntity
{
    public int CommentId { get; set; }
    public Comment Comment { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? OwnerId { get; set; }

    /// <summary>Advisory: the comment body looked like it contained a credential or payload.</summary>
    /// <remarks>Computed server-side on write. Never exposed to AI-facing surfaces — see R2-06.</remarks>
    public bool HasPayloadFlag { get; set; }

    /// <summary>Names of the matched detector patterns; empty when clean.</summary>
    public List<string> PayloadFlags { get; set; } = new();

    /// <summary>
    /// True when this reply was posted by an automated caller (the AI apply flow — CLI/pointer.sh/
    /// skill.md) rather than a human typing into the widget or dashboard.
    /// </summary>
    /// <remarks>
    /// Same signal as <see cref="Comment.HasPayloadFlag"/>'s exposure rule: the X-Pointer-Client
    /// header (ICurrentClient.IsHumanSurface), which only the widget and dashboard send. Read-only
    /// forever once set — see CommentService.EditReplyAsync/DeleteReplyAsync, which refuse to
    /// touch an AI reply regardless of caller.
    /// </remarks>
    public bool IsAi { get; set; }
}
