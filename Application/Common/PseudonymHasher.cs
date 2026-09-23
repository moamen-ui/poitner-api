using System.Security.Cryptography;
using System.Text;

namespace Pointer.Application.Common;

/// <summary>
/// Pseudonymous hashes for audit rows (DB-12 D12.3): an unknown e-mail is recorded as 16 hex
/// chars — the first 8 bytes of SHA-256 — enough to correlate repeated failures against one
/// address, not enough to enumerate. Raw e-mails are never written to audit_events (append-only
/// rows cannot be erased; R14/R17). Hash inputs are always the NORMALISED e-mail
/// (<see cref="EmailNormalizer.Normalize"/> / <see cref="EmailNormalizer.NormalizeRequired"/>,
/// DB-11a §3.3a) so the same address always hashes the same.
/// </summary>
public static class PseudonymHasher
{
    /// <summary>Lower-case hex of the first 8 SHA-256 bytes of <paramref name="normalisedEmail"/>.</summary>
    public static string EmailHash(string normalisedEmail)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalisedEmail));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
