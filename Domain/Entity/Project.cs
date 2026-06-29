namespace Pointer.Domain.Entity;

public class Project : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public Guid? OwnerId { get; set; }
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
