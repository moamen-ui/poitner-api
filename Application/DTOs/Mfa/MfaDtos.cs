using System.ComponentModel.DataAnnotations;

namespace Pointer.Application.DTOs.Mfa;

/// <summary>R5-61 review fix #5 (Gemini) — POST /api/me/mfa/enrol request body: enrollment must be
/// confirmed with the caller's current password (a bare `[Authorize]` session is not enough for a
/// security-relevant, MFA-changing action on the one super-admin account).</summary>
public class MfaEnrolRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
}

/// <summary>R5-61 §3.2 — POST /api/me/mfa/enrol response. The dashboard renders <see cref="OtpauthUrl"/>
/// as a QR code (client-side library, no server dependency).</summary>
public class MfaEnrolResponse
{
    /// <summary>The base32 TOTP secret — also embedded in <see cref="OtpauthUrl"/>, shown as a
    /// manual-entry fallback for authenticator apps that cannot scan a QR code.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary><c>otpauth://totp/Pointer:&lt;email&gt;?secret=...&amp;issuer=Pointer&amp;algorithm=SHA1&amp;digits=6&amp;period=30</c>.</summary>
    public string OtpauthUrl { get; set; } = string.Empty;
}

/// <summary>Shared body shape for every "enter your code" action: POST /api/me/mfa/verify,
/// POST /api/me/mfa/disable, and POST /api/auth/mfa/verify. <see cref="Code"/> is a 6-digit TOTP
/// code, or (disable/login-verify only) a 16-character recovery code (review fix #2) — 64 is a
/// generous upper bound for either shape (finding #11), rejected before it ever reaches a service.
/// <see cref="CurrentPassword"/> is read only by POST /api/me/mfa/disable (review fix #5, Gemini);
/// every other caller of this DTO leaves it null and it is ignored.</summary>
public class MfaCodeRequest
{
    [StringLength(64)]
    public string Code { get; set; } = string.Empty;

    public string? CurrentPassword { get; set; }
}

/// <summary>R5-61 §3.2 — POST /api/me/mfa/verify response: the 8 recovery codes, shown to the caller
/// exactly once (only the SHA-256 hash is ever persisted, in <c>user_recovery_codes.code_hash</c>).</summary>
public class MfaVerifyResponse
{
    public List<string> RecoveryCodes { get; set; } = new();
}
