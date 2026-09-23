namespace Pointer.Domain.Enums;

/// <summary>
/// Who performed an audited action (DB-12). Append-only — new kinds are added at the end, existing
/// ints are never renumbered (DB-RULES R10).
/// </summary>
public enum AuditActorKind
{
    /// <summary>An authenticated workspace member/admin acting in their workspace.</summary>
    User = 1,

    /// <summary>A super admin acting on operator surfaces (tenants, plans, settings, branding).</summary>
    SuperAdmin = 2,

    /// <summary>Hosted jobs and anonymous paths with no resolved identity.</summary>
    System = 3,

    /// <summary>A super admin acting under a DB-13 impersonation session.</summary>
    Impersonation = 4,
}
