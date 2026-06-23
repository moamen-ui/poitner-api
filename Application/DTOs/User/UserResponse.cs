namespace Pointer.Application.DTOs.User;

public class UserResponse
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; }
}
