using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IInviteService
{
    // ── Admin (auth, tenant-scoped) ────────────────────────────────────────────

    /// <summary>Create an invite for the caller's tenant. Owner is non-null or the call is Forbidden.</summary>
    Task<Result<InviteResponse>> CreateAsync(CreateInviteRequest request);

    /// <summary>List this tenant's active (not revoked/expired) invites. Never returns another tenant's.</summary>
    Task<Result<List<InviteResponse>>> ListAsync();

    /// <summary>Revoke an invite by id — explicit own-owner scope; unreachable cross-tenant.</summary>
    Task<Result> RevokeAsync(int id);

    // ── Anonymous accept flow ──────────────────────────────────────────────────

    /// <summary>Safe preview for a code (anonymous). NotFound for invalid/expired/revoked/used-up.</summary>
    Task<Result<InvitePreviewResponse>> GetPreviewAsync(string code);

    /// <summary>
    /// Accept an invite (anonymous): create an Approved + active tenant-scoped user and return a
    /// login token. The invite IS the authorization — it is validated thoroughly first.
    /// </summary>
    Task<Result<LoginResponse>> AcceptAsync(AcceptInviteRequest request);
}
