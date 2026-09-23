using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.CurrentUser;

public class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(
            accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? accessor.HttpContext?.User.FindFirst("sub")?.Value,
            out var g
        )
            ? g
            : null;

    public bool IsAdmin => accessor.HttpContext?.User.FindFirst("is_admin")?.Value == "true";

    public bool IsSuperAdmin =>
        accessor.HttpContext?.User.FindFirst("is_super_admin")?.Value == "true";

    public bool IsQuickAccess =>
        accessor.HttpContext?.User.FindFirst("is_quick_access")?.Value == "true";

    public Guid? TenantId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst("tenant")?.Value, out var g) ? g : null;

    public int? RoleId =>
        int.TryParse(accessor.HttpContext?.User.FindFirst("role_id")?.Value, out var id)
            ? id
            : null;

    public string? KeyScopes => accessor.HttpContext?.User.FindFirst("key_scopes")?.Value;

    public string? Scope => accessor.HttpContext?.User.FindFirst("scope")?.Value;

    public long? ImpersonationSessionId =>
        long.TryParse(accessor.HttpContext?.User.FindFirst("imp")?.Value, out var imp) ? imp : null;

    public bool IsImpersonating => ImpersonationSessionId != null;

    // DB-13 review fix #2: the token's own "exp" claim (Unix seconds) — for an impersonation token
    // this is exactly the session's ExpiresAt (JwtTokenService.IssueImpersonation signs `expires:
    // expiresAt`). Only meaningful while impersonating; null otherwise so callers never need to
    // guard on IsImpersonating separately.
    public DateTime? ImpersonationExpiresAt =>
        IsImpersonating
        && long.TryParse(accessor.HttpContext?.User.FindFirst("exp")?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
            : null;
}
