namespace Pointer.Domain.Entity;

public class StatusPresentation : BaseEntity
{
    public int StatusValue { get; set; }   // CommentStatus int
    public string? Label { get; set; }
    public string? Color { get; set; }
    public int? DisplayOrder { get; set; }
    public Guid? OwnerId { get; set; }
}
