namespace Pointer.Domain.Entity;

/// <summary>
/// A data-driven role (managed by admins in the dashboard). Roles are labels for stakeholders
/// (Developer / PM / Tester / …). The single capability that matters for authorization is
/// <see cref="GrantsAdmin"/>: any user whose role grants admin can manage the dashboard.
/// </summary>
public class Role : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether holders of this role can access admin endpoints / the dashboard.</summary>
    public bool GrantsAdmin { get; set; }

    /// <summary>System roles (e.g. Admin) are seeded and cannot be renamed, disabled, or deleted.</summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<User> Users { get; set; } = new List<User>();
}
