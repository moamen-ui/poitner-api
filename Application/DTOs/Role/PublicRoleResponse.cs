namespace Pointer.Application.DTOs.Role;

/// <summary>
/// Minimal, anonymous-safe role shape for the public signup / re-apply dropdowns.
/// Only non-admin, active roles are ever returned through this DTO.
/// </summary>
public class PublicRoleResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
