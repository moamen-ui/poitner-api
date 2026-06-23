namespace Pointer.Application.DTOs.Role;

public class RoleResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool GrantsAdmin { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
}
