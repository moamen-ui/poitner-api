using System;

namespace Pointer.Domain.Entity;

public class UsageEvent
{
    public int Id { get; set; }
    public Guid? OwnerId { get; set; }
    public int? ProjectId { get; set; }
    public Guid? UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Meta { get; set; }
    public DateTime CreatedAt { get; set; }
}
