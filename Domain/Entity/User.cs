using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

public class User : BaseEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;
    public string? Language { get; set; }
    public string? Theme { get; set; }
    public Guid? OwnerId { get; set; }
    public bool IsDemo { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Whether a super-admin has already used their one-time demo extension for this user.</summary>
    public bool DemoExtended { get; set; }

    /// <summary>Per-tenant override of the demo comment cap. Null = use the global setting.</summary>
    public int? DemoCommentCapOverride { get; set; }

    /// <summary>Per-tenant override of the demo TTL (hours), used when extending. Null = use the global setting.</summary>
    public int? DemoTtlHoursOverride { get; set; }
}
