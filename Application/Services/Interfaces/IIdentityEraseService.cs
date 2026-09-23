using Pointer.Application.DTOs.User;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-11c §3.4 — the single erase routine (GDPR "delete my account"): tombstones the identity
/// (e-mail/name/password replaced, PublicId kept), ends every membership, destroys per-user
/// secrets (API keys, device logins, quick-access links, notifications, personal AI rules), and
/// scrubs the person's address out of <c>invites.email</c>. Comments/replies stay, attributed to
/// the tombstone; screenshots stay (F5). Blocked (Conflict) while the identity is the sole live
/// Workspace Admin of any workspace (S-13) — transfer ownership first.
/// </summary>
public interface IIdentityEraseService
{
    /// <summary>DELETE /api/me — the account holder erases themselves. Password-confirmed; a
    /// PasswordlessOnly identity is refused here (§3.4b — use the e-mailed link instead).</summary>
    Task<Result> EraseSelfAsync(DeleteMyAccountRequest request);

    /// <summary>DELETE /api/admin/identities/{publicId} — a super admin erases any (non-super-admin)
    /// identity.</summary>
    Task<Result> EraseByPublicIdAsync(Guid publicId);

    /// <summary>POST /api/me/request-erase — e-mails a scoped one-time link (30 min) that lets a
    /// PasswordlessOnly identity confirm its own erase without a password.</summary>
    Task<Result> RequestEraseLinkAsync();

    /// <summary>POST /api/auth/confirm-erase — anonymous; redeems the scoped link minted by
    /// <see cref="RequestEraseLinkAsync"/>.</summary>
    Task<Result> EraseByTokenAsync(string token);
}
