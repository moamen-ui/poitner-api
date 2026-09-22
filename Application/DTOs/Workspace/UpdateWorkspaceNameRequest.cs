namespace Pointer.Application.DTOs.Workspace;

/// <summary>PUT /api/admin/workspace/name body.</summary>
public class UpdateWorkspaceNameRequest
{
    public string Name { get; set; } = string.Empty;
}
