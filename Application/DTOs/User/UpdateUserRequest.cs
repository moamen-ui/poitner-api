namespace Pointer.Application.DTOs.User;

public class UpdateUserRequest
{
    public int? RoleId { get; set; }
    public bool? IsActive { get; set; }
    public string? Password { get; set; }
}
