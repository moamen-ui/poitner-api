namespace Pointer.Application.DTOs.Role;

public class RoleResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool GrantsAdmin { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the CALLER may rename / enable / disable / delete this role. Mirrors the guards in
    /// RoleService.UpdateAsync and DeleteAsync: system roles are immutable, and a scoped admin may
    /// only manage roles its own tenant owns. Surfaced so a dashboard never offers an action the API
    /// will refuse — a workspace admin used to see global roles with a live actions menu that 404'd.
    /// </summary>
    public bool CanManage { get; set; }
}
