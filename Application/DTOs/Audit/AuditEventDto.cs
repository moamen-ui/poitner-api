namespace Pointer.Application.DTOs.Audit;

/// <summary>
/// One audit row as shown on the Security log page (DB-12 §3.8a). Same DTO for both views;
/// <c>UserAgent</c>/<c>IpHash</c> are populated only in the super-admin <c>/all</c> view (null in
/// the workspace view), and the operator's identity is never named to a workspace (D12.5): rows
/// whose ActorKind is SuperAdmin or Impersonation carry <c>ActorUserId = null</c> and the
/// <c>OperatorLabel</c> name there — the full identity exists only in <c>/all</c>.
/// </summary>
public class AuditEventDto
{
    /// <summary>What a workspace admin sees instead of the operator's uuid/name (D12.5).</summary>
    public const string OperatorLabel = "Operator";

    /// <summary>What an actor-less row (hosted job, anonymous failure) shows.</summary>
    public const string SystemLabel = "System";

    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public string ActorKind { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public IReadOnlyDictionary<string, string>? Before { get; set; }
    public IReadOnlyDictionary<string, string>? After { get; set; }
    public string? RequestId { get; set; }
    public long? ImpersonationSessionId { get; set; }

    /// <summary>/all view only.</summary>
    public string? UserAgent { get; set; }

    /// <summary>/all view only.</summary>
    public string? IpHash { get; set; }
}
