using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// R5-61 §3.2 — operator (super-admin-only) TOTP MFA: enrol, verify (completes enrollment, mints
/// recovery codes), disable. Every method gates on <c>ICurrentUser.IsSuperAdmin</c> (§3.4) — MFA is
/// scoped to the one env-seeded super-admin account, never any other identity.
/// </summary>
public interface IMfaService
{
    /// <summary>POST /api/me/mfa/enrol — generates and stores a fresh (unverified) TOTP secret.
    /// 403 if not super admin; 409 if MFA is already enabled.</summary>
    Task<Result<MfaEnrolResponse>> EnrolAsync();

    /// <summary>POST /api/me/mfa/verify — validates the code against the pending secret; on success,
    /// enables MFA and mints 8 recovery codes (returned once). 403 not super admin; 409 already
    /// enabled; a plain failure if the code is wrong or nothing was enrolled.</summary>
    Task<Result<MfaVerifyResponse>> VerifyAsync(MfaCodeRequest request);

    /// <summary>POST /api/me/mfa/disable — validates a current TOTP or recovery code, then clears
    /// the secret/recovery codes. 403 not super admin; a plain failure if the code is wrong or MFA
    /// was never enabled.</summary>
    Task<Result> DisableAsync(MfaCodeRequest request);

    /// <summary>
    /// Shared by <see cref="DisableAsync"/> and <c>AuthService</c>'s login-completion step
    /// (POST /api/auth/mfa/verify): true if <paramref name="code"/> is a live TOTP code for
    /// <paramref name="user"/>'s stored secret, OR an unused recovery code (which this call consumes
    /// — <c>UsedAt</c> is stamped and saved before returning true). Does not check
    /// <c>IsSuperAdmin</c>/enablement — callers already know the identity.
    /// </summary>
    Task<bool> ValidateCodeOrRecoveryAsync(User user, string code);
}
