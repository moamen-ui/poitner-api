namespace Pointer.Domain.Entity;

/// <summary>
/// A predefined instruction prompt for AI coding agents.
/// Scoping:
///   - Tenant Admin Rule: ProjectId == null && UserId == null (applies to all projects/users in tenant)
///   - Project Admin Rule: ProjectId != null && UserId == null (applies to all users on this project)
///   - User Personal Rule: UserId != null (applies only to comments authored by this user)
/// </summary>
public class AiRule : BaseEntity
{
    public Guid? OwnerId { get; set; }

    /// <summary>null = tenant-wide (admin/deputy only); non-null = project-specific.</summary>
    public int? ProjectId { get; set; }

    /// <summary>null = admin/deputy rule (applies to all); non-null = user-specific rule (applies to user's comments only).</summary>
    public Guid? UserId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; } = 0;
}
