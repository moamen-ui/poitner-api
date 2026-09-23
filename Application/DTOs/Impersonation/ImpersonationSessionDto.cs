namespace Pointer.Application.DTOs.Impersonation;

/// <summary>
/// DB-13 §3.6 — one row on GET /api/admin/impersonation. Operator identity is deliberately omitted
/// (D13.6): a workspace admin never learns which operator viewed their workspace.
/// </summary>
public class ImpersonationSessionDto
{
    public long Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public int RequestCount { get; set; }
    public string Reason { get; set; } = string.Empty;
}
