namespace Pointer.Application.Abstractions;

/// <summary>
/// R5-61 §3.5 — RFC 6238 TOTP (SHA-1, 30 s step, 6 digits). Implemented in
/// <c>Infrastructure/Auth/TotpService.cs</c>, exposed here (rather than as a bare concrete
/// dependency) so <c>Application/Services/Implementation/MfaService.cs</c> can depend on it without
/// the Application project referencing Infrastructure (Application has no ProjectReference to
/// Infrastructure — the reverse is true — so a plain constructor parameter of the concrete type
/// would not compile there).
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a fresh 20-byte random secret, base32-encoded (no padding).</summary>
    string GenerateSecret();

    /// <summary>Validates a 6-digit code against the current time step, tolerating ±1 step
    /// (3 windows total) for clock skew. <paramref name="secret"/> is the base32 secret.</summary>
    bool ValidateCode(string secret, string code);

    /// <summary>
    /// R5-61 review fix #1 (replay protection) — same validation as <see cref="ValidateCode"/>, but
    /// also returns the RFC 6238 time-step counter (<paramref name="step"/>) the code matched, so the
    /// caller can refuse a code whose step is <c>&lt;= (User.TotpLastStep ?? -1)</c> and persist the
    /// new watermark. On success <paramref name="step"/> is the matched (highest, if more than one
    /// window somehow matches) counter; on failure it is a fixed non-matching sentinel (<c>-1</c>) —
    /// never a real counter for a false return, so a caller cannot accidentally treat a failed match
    /// as "step -1 accepted".
    /// </summary>
    bool TryValidateCode(string secret, string code, out long step);

    /// <summary>The otpauth:// URL a QR-code generator renders.</summary>
    string GenerateOtpAuthUrl(string email, string secret, string issuer = "Pointer");
}
