namespace Pointer.Domain.Enums;

/// <summary>
/// How an DB-13 impersonation session ended. Append-only — new reasons are added at the end,
/// existing ints are never renumbered (DB-RULES R10).
/// </summary>
public enum ImpersonationEndReason
{
    /// <summary>The operator called POST /api/admin/impersonation/end before the time box elapsed.</summary>
    Manual = 1,

    /// <summary>The 5-minute sweep closed the session after its ExpiresAt passed.</summary>
    Expired = 2,
}
