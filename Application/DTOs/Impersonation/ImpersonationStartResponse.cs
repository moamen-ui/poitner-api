namespace Pointer.Application.DTOs.Impersonation;

/// <summary>DB-13 §3.6 — the read-only, time-boxed impersonation token and its session metadata.</summary>
public class ImpersonationStartResponse
{
    public string Token { get; set; } = string.Empty;
    public long SessionId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
}
