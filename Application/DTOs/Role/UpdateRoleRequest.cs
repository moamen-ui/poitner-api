namespace Pointer.Application.DTOs.Role;

public class UpdateRoleRequest
{
    public string? Name { get; set; }
    public bool? GrantsAdmin { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>See Role.QuickAccess. null (property omitted) → leave untouched.</summary>
    public bool? QuickAccess { get; set; }
}
