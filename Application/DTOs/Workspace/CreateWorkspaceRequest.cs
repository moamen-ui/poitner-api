using System.ComponentModel.DataAnnotations;

namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-19 §3.5: POST /api/me/workspaces body. Name rules: WorkspaceNameRules (1–120, no control chars).</summary>
public class CreateWorkspaceRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
}
