namespace Pointer.Application.DTOs.Role;

public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public bool GrantsAdmin { get; set; }
}
