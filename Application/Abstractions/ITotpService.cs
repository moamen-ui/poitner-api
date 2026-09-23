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

    /// <summary>The otpauth:// URL a QR-code generator renders.</summary>
    string GenerateOtpAuthUrl(string email, string secret, string issuer = "Pointer");
}
