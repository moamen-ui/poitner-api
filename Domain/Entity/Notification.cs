using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

public class Notification : BaseEntity
{
    public Guid? OwnerId { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public int CommentId { get; set; }
    public Comment Comment { get; set; } = null!;
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? ActorId { get; set; }
    public string? Payload { get; set; }
    public DateTime? ReadAt { get; set; }
}
