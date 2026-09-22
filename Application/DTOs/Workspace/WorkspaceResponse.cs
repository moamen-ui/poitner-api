namespace Pointer.Application.DTOs.Workspace;

/// <summary>Response of GET and PUT /api/admin/workspace/name — the caller's own workspace row.</summary>
public class WorkspaceResponse
{
    public Guid Id { get; set; } // == the JWT tenant claim / owner_id

    public string Name { get; set; } = string.Empty;

    /// <summary>True while Name is still the DB-03 placeholder ("Workspace") — the UI prompts.</summary>
    public bool IsPlaceholderName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
