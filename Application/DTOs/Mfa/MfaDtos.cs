namespace Pointer.Application.DTOs.Mfa;

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
/// code, or (disable/login-verify only) an 8-character recovery code.</summary>
public class MfaCodeRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>R5-61 §3.2 — POST /api/me/mfa/verify response: the 8 recovery codes, shown to the caller
/// exactly once (only the SHA-256 hash is ever persisted, in <c>user_recovery_codes.code_hash</c>).</summary>
public class MfaVerifyResponse
{
    public List<string> RecoveryCodes { get; set; } = new();
}
