namespace Pointer.Application.DTOs.Auth;

/// <summary>DB-11b: exchanges a selection token (or an ordinary full token) for a full JWT of the
/// chosen membership. See <c>POST /api/auth/switch-workspace</c>.</summary>
public class SwitchWorkspaceRequest
{
    public Guid WorkspaceId { get; set; }
}
