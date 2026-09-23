using System.Security.Cryptography;
using System.Text;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Auth;

/// <summary>
/// R5-61 §3.5 — RFC 6238 TOTP (SHA-1, 30-second step, 6 digits), implemented in-house (no new
/// NuGet package). Stateless/singleton — every method is a pure function of its inputs plus
/// <see cref="TimeProvider"/> (defaults to <see cref="TimeProvider.System"/>, overridable for
/// deterministic tests, e.g. the RFC 6238 Appendix B test vector). Implements
/// <see cref="ITotpService"/> so <c>MfaService</c> (Application layer) can depend on it — registered
/// in DI against that interface (<c>Infrastructure/DependencyInjection.cs</c>), same as every other
/// Infrastructure/Auth/* class here (ResetTokenService, LoginAttemptLimiter, ApiKeyProtector, …).
/// </summary>
public class TotpService(TimeProvider? timeProvider = null) : ITotpService
{
    private const int SecretBytes = 20;
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <summary>±1 step (30s each side) tolerates ordinary clock skew between the server and the
    /// authenticator app — 3 windows checked in total (§3.5).</summary>
    private const int WindowTolerance = 1;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private static readonly char[] Base32Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    /// <summary>Generates a fresh 20-byte random secret, base32-encoded (no padding) — the value
    /// stored (AES-GCM encrypted) in <c>users.totp_secret</c> and embedded in the QR/otpauth URL.</summary>
    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Base32Encode(bytes);
    }

    /// <summary>
    /// Validates a 6-digit code against the current time step, tolerating ±<see cref="WindowTolerance"/>
    /// steps for clock skew. <paramref name="secret"/> is the base32 secret (already decrypted by the
    /// caller). Constant-time-ish: every candidate window is computed and compared regardless of
    /// where a match is found, so early-exit timing does not leak which window matched.
    /// </summary>
    public bool ValidateCode(string secret, string code) => TryValidateCode(secret, code, out _);

    /// <summary>
    /// R5-61 review fix #1 (replay protection) — same validation as <see cref="ValidateCode"/>, plus
    /// the matched time-step counter so the caller can enforce "this step was already used".
    /// <paramref name="step"/> is the sentinel <c>-1</c> on any failure (bad input, bad secret, no
    /// window matched) so a caller can never mistake a failed call for "step -1 was accepted".
    /// </summary>
    public bool TryValidateCode(string secret, string code, out long step)
    {
        step = -1;

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            return false;

        var normalizedCode = code.Trim();
        if (normalizedCode.Length != Digits || !normalizedCode.All(char.IsDigit))
            return false;

        byte[] key;
        try
        {
            key = Base32Decode(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var currentCounter = CurrentCounter();
        var matched = false;
        var matchedStep = -1L;
        for (var delta = -WindowTolerance; delta <= WindowTolerance; delta++)
        {
            var candidateCounter = currentCounter + delta;
            // Nit (§12): a negative counter is not a valid RFC 6238 time step at all (it would only
            // arise this close to the Unix epoch) — skip it rather than clamping to 0, which would
            // otherwise let a candidate counter of -1 quietly compute and potentially match the
            // legitimate code for counter 0.
            if (candidateCounter < 0)
                continue;

            var candidate = ComputeCode(key, candidateCounter);
            if (FixedTimeEquals(candidate, normalizedCode))
            {
                matched = true;
                if (candidateCounter > matchedStep)
                    matchedStep = candidateCounter;
            }
        }

        if (matched)
            step = matchedStep;

        return matched;
    }

    /// <summary>The otpauth:// URL a QR-code generator renders (§3.2) — issuer/label per §3.2.
    /// Nit (§12): only the account (email) and issuer parts are percent-encoded; the "Issuer:account"
    /// separator colon is kept literal, per the otpauth label convention (RFC-ish, Google
    /// Authenticator/most TOTP apps parse "Issuer:account" on an un-encoded colon — escaping it too
    /// would turn the label into an unparseable single token for some authenticator apps).</summary>
    public string GenerateOtpAuthUrl(string email, string secret, string issuer = "Pointer")
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedAccount = Uri.EscapeDataString(email);
        var label = $"{encodedIssuer}:{encodedAccount}";
        return $"otpauth://totp/{label}?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    private long CurrentCounter() => _time.GetUtcNow().ToUnixTimeSeconds() / StepSeconds;

    /// <summary>RFC 6238 / RFC 4226 dynamic truncation over HMAC-SHA1(secret, counter). Callers
    /// never pass a negative counter (see the skip in <see cref="TryValidateCode"/>), so this no
    /// longer clamps one to 0 (§12 nit — clamping would have silently validated against counter 0's
    /// code for an out-of-range candidate).</summary>
    private static string ComputeCode(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var otp = (uint)binary % 1_000_000;
        return otp.ToString("D6");
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a),
            Encoding.ASCII.GetBytes(b)
        );
    }

    // ── Base32 (RFC 4648 §6, no padding) ────────────────────────────────────────────────────────
    // Public so tests can encode a known raw secret (e.g. the RFC 6238 Appendix B ASCII test
    // vector "12345678901234567890") into the base32 form ValidateCode expects.

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bitBuffer = 0,
            bitsInBuffer = 0;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitsInBuffer += 8;
            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                sb.Append(Base32Alphabet[(bitBuffer >> bitsInBuffer) & 0x1f]);
            }
        }
        if (bitsInBuffer > 0)
            sb.Append(Base32Alphabet[(bitBuffer << (5 - bitsInBuffer)) & 0x1f]);
        return sb.ToString();
    }

    /// <summary>Public for the same reason as <see cref="Base32Encode"/> — tests decoding a known
    /// secret independently of <see cref="GenerateSecret"/>.</summary>
    public static byte[] Base32Decode(string base32)
    {
        var input = base32.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>((input.Length * 5) / 8);
        int bitBuffer = 0,
            bitsInBuffer = 0;
        foreach (var c in input)
        {
            var idx = Array.IndexOf(Base32Alphabet, c);
            if (idx < 0)
                throw new FormatException($"Invalid base32 character '{c}'.");
            bitBuffer = (bitBuffer << 5) | idx;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)((bitBuffer >> bitsInBuffer) & 0xff));
            }
        }
        return output.ToArray();
    }
}
