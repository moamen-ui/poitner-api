namespace Pointer.Application.DTOs.User;

public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int RoleId { get; set; }

    /// <summary>
    /// Required when the caller is a super admin: the existing workspace (tenant OwnerId) to add
    /// them to as a "Workspace Admin Deputy" — super admins can no longer self-own a workspace or
    /// assign any other role via this endpoint. Ignored for a non-super-admin caller, who always
    /// adds to their own tenant.
    /// </summary>
    public Guid? TargetOwnerId { get; set; }
}
