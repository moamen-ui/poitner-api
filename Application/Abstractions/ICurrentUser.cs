namespace Pointer.Application.Abstractions;

public interface ICurrentUser
{
    Guid? Id { get; }
    bool IsAdmin { get; }
    bool IsSuperAdmin { get; }
    bool IsQuickAccess { get; }
    Guid? TenantId { get; }

    /// <summary>The caller's Role.Id (from the JWT's existing "role_id" claim). Null when there is
    /// no authenticated user at all — never null for a real authenticated caller, since every User
    /// has a RoleId.</summary>
    int? RoleId { get; }

    /// <summary>Raw <c>key_scopes</c> claim value, present only when this session was opened with an
    /// API key (DB-11b F2). Null for a password/selection-token session.</summary>
    string? KeyScopes { get; }

    /// <summary>Raw <c>scope</c> claim value (DB-11b). <c>"select_workspace"</c> for a 5-minute
    /// selection token; null for an ordinary full token.</summary>
    string? Scope { get; }

    /// <summary>DB-13: the impersonation_sessions.id from the JWT "imp" claim; null for every ordinary token.</summary>
    long? ImpersonationSessionId { get; }

    /// <summary>True only for a super admin acting under a live impersonation session (scope=impersonate).</summary>
    bool IsImpersonating { get; }

    /// <summary>DB-13 review fix #2: the impersonation token's own hard expiry (its JWT "exp" claim,
    /// which equals the session's ExpiresAt — see JwtTokenService.IssueImpersonation), used to clamp
    /// signed screenshot URLs so they never outlive the session. Null for every non-impersonating
    /// caller. Default interface implementation so every hand-written test double implementing
    /// ICurrentUser keeps compiling unchanged (only HttpCurrentUser overrides it).</summary>
    DateTime? ImpersonationExpiresAt => null;
}
