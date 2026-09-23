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

    // DB-18 §3.4 — self-service pause/delete lifecycle.
    public DateTime? PausedAt { get; set; }
    public bool PausedByOperator { get; set; }

    /// <summary>Who paused it. Null when <see cref="PausedByOperator"/> (R17 redaction) or not paused.</summary>
    public string? PausedByName { get; set; }
    public DateTime? DeletionRequestedAt { get; set; }
    public DateTime? DeletionScheduledFor { get; set; }
    public string? DeletionRequestedByName { get; set; }

    /// <summary>The §3.4 lifecycle guard, evaluated without side effects.</summary>
    public bool CanManageLifecycle { get; set; }
    public int GraceDays { get; set; }
}
