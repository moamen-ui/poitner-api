namespace Pointer.Application.DTOs.Impersonation;

/// <summary>DB-13 §3.6 — POST /api/admin/tenants/{workspaceId}/impersonate body.</summary>
public class StartImpersonationRequest
{
    /// <summary>Free text, 10–500 chars (StartImpersonationValidator) — shown to the workspace's admins.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>1–60, default 30 (D13.5).</summary>
    public int Minutes { get; set; } = 30;
}
