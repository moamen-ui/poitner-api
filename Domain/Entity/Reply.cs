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

}
