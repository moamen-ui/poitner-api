using Pointer.Application.DTOs.Impersonation;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>DB-13 §3.6 — starts/ends/lists audited, time-boxed impersonation sessions (F2).</summary>
public interface IImpersonationService
{
    /// <summary>
    /// Super admin only, not already impersonating. Inserts the session row, issues the read-only
    /// impersonation token, audits <c>impersonation.started</c>, and best-effort e-mails the
    /// workspace's live admins.
    /// </summary>
    Task<Result<ImpersonationStartResponse>> StartAsync(
        Guid workspaceId,
        StartImpersonationRequest request
    );

    /// <summary>
    /// Ends the caller's own live session (<paramref name="sessionId"/> is used only when the caller
    /// presents a plain super-admin token — an impersonation token always ends its own session).
    /// Audits <c>impersonation.ended</c> with the final request count and duration.
    /// </summary>
    Task<Result> EndAsync(long? sessionId);

    /// <summary>
    /// Super admin: every session, optionally filtered by workspace. Workspace admin: only the
    /// sessions that targeted their own workspace (operator identity omitted — D13.6).
    /// </summary>
    Task<Result<PagedData<ImpersonationSessionDto>>> ListAsync(
        Guid? workspaceId,
        int page,
        int pageSize
    );
}
