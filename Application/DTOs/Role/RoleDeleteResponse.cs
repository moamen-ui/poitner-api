namespace Pointer.Application.DTOs.Role;

public class RoleDeleteResponse
{
    public int Id { get; set; }

    /// <summary>How many users were moved off the deleted role.</summary>
    public int ReassignedUsers { get; set; }

    /// <summary>The role users were moved to (null when there were no users to move).</summary>
    public int? ReassignedToRoleId { get; set; }
}
