namespace Pointer.Domain.Entity;

public class Reply : BaseEntity
{
    public int CommentId { get; set; }
    public Comment Comment { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? OwnerId { get; set; }
}
