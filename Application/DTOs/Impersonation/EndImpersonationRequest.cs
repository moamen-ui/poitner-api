namespace Pointer.Application.DTOs.Impersonation;

/// <summary>
/// DB-13 §3.6 — POST /api/admin/impersonation/end body. Null when the caller presents the
/// impersonation token itself (the session is <c>ICurrentUser.ImpersonationSessionId</c>); set when
/// a plain super-admin token ends its own live session by id instead.
/// </summary>
public class EndImpersonationRequest
{
    public long? SessionId { get; set; }
}
