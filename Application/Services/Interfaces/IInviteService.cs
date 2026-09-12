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

    /// <summary>
    /// Revoke an invite by id — explicit own-owner scope; unreachable cross-tenant.
    /// For a quick-access invite this also revokes the magic link it issued: the link is the
    /// credential, and revoking the audit row alone would leave it working.
    /// </summary>
    Task<Result> RevokeAsync(int id);

    /// <summary>
    /// Issue a fresh magic link for a quick-access invite and invalidate the previous one.
    ///
    /// This is the answer to a leaked link. Revoking would cut the client off entirely; rotating
    /// keeps the same provisioned user and project and only replaces the bearer token, so the
    /// admin can hand over a new link without re-inviting anyone.
    /// </summary>
    Task<Result<InviteResponse>> RotateQuickLinkAsync(int id);

    /// <summary>
    /// Re-sends an invitation. By default the same code is kept and only the expiry is extended, so
    /// a link already sitting in someone's inbox keeps working. <paramref name="rotate"/> mints a
    /// new code instead — the answer to a leaked link, which also invalidates the old one.
    /// </summary>
    Task<Result<InviteResponse>> ResendAsync(int id, bool rotate = false);

    // ── Anonymous accept flow ──────────────────────────────────────────────────

    /// <summary>Safe preview for a code (anonymous). NotFound for invalid/expired/revoked/used-up.</summary>
    Task<Result<InvitePreviewResponse>> GetPreviewAsync(string code);

    /// <summary>
    /// Accept an invite (anonymous): create an Approved + active tenant-scoped user and return a
    /// login token. The invite IS the authorization — it is validated thoroughly first.
    /// </summary>
    Task<Result<LoginResponse>> AcceptAsync(AcceptInviteRequest request);
}
