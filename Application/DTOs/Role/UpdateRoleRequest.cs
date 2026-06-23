namespace Pointer.Application.DTOs.Role;

public class UpdateRoleRequest
{
    public string? Name { get; set; }
    public bool? GrantsAdmin { get; set; }
    public bool? IsActive { get; set; }
}
