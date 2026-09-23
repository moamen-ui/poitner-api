namespace Pointer.Application.DTOs.Audit;

/// <summary>
/// Filters for the audit read API (DB-12 §3.8a). <c>Action</c> is a PREFIX match (e.g.
/// <c>"member."</c> lists every member.* action). <c>WorkspaceId</c> applies to the super-admin
/// <c>/all</c> view only — the workspace view is always scoped to the caller's own workspace
/// regardless of what this carries.
/// </summary>
public class AuditQuery
{
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
    public string? Action { get; set; }
    public Guid? Actor { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>/all view only.</summary>
    public Guid? WorkspaceId { get; set; }

    public int Page { get; set; } = 1;

    /// <summary>Capped at 200 by the service (MessageKeys.Audit.PageSizeTooLarge).</summary>
    public int PageSize { get; set; } = 50;
}
