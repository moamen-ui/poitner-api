using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// R5-61 §3.2 — operator TOTP MFA. Every public entry point gates on
/// <see cref="ICurrentUser.IsSuperAdmin"/> first: MFA exists specifically for the one env-seeded
/// super-admin account (§3.4), never any other identity, even though the columns live on the shared
/// <c>users</c> table.
///
/// Review fixes layered on top of the original implementation:
/// <list type="bullet">
/// <item>#1 replay protection — <c>User.TotpLastStep</c> watermark, checked/persisted here and in
/// <c>AuthService.VerifyMfaLoginAsync</c> (via <see cref="ValidateCodeOrRecoveryAsync"/>).</item>
/// <item>#2 recovery codes — 16 chars (80 bits) and peppered with <see cref="IApiKeyProtector.HmacHex"/>
/// instead of a bare hash.</item>
/// <item>#4 an API-key-scoped session (<see cref="ICurrentUser.KeyScopes"/> non-null) may never
/// enrol/verify/disable MFA — only a full password/TOTP session may.</item>
/// <item>#5 (Gemini) enrol requires the caller's current password (passwordless super admins are
/// refused outright); disable requires a valid code AND the current password; both verify and
/// disable share the ordinary per-e-mail login lockout budget on a wrong attempt.</item>
/// <item>#10 the super-admin gate is re-checked against the loaded row's <c>Role.IsSuperAdmin</c>,
/// never trusting the JWT claim alone.</item>
/// </list>
/// </summary>
public class MfaService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IApiKeyProtector protector,
    ITotpService totp,
    ILoginAttemptLimiter loginLimiter,
    IPasswordHasher passwordHasher,
    IAuditWriter? audit = null
) : IMfaService
{
    private const int RecoveryCodeCount = 8;

    // Review fix #2: 16 chars over a 33-character alphabet ≈ 80.7 bits of entropy (log2(33) * 16),
    // matching the "80 bits" the finding names — up from the original 8 chars (~40 bits).
    private const int RecoveryCodeLength = 16;

    // No 0/O/1/I — avoids characters that are easy to misread when copying a recovery code by hand.
    private static readonly char[] RecoveryCodeAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly IAuditWriter _audit = audit ?? NoopAuditWriter.Instance;

    private async Task<User?> CurrentUserRowAsync()
    {
        if (currentUser.Id is not Guid publicId)
            return null;

        // Own row only — no tenant concept for a super admin, so the ordinary strict-own query
        // filter (which keys off Memberships) would not match; IgnoreQueryFilters + PublicId is the
        // same anonymous-safe lookup AuthService uses for its own identity resolution paths.
        return await unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null && u.PublicId == publicId)
            .FirstOrDefaultAsync();
    }

    public async Task<Result<MfaEnrolResponse>> EnrolAsync(MfaEnrolRequest request)
    {
        // Review fix #4: an API-key session must never be able to mutate MFA state — checked before
        // anything else touches the database.
        if (currentUser.KeyScopes is not null)
            return Result<MfaEnrolResponse>.Forbidden(MessageKeys.Mfa.KeySessionCannotMutateMfa);

        if (!currentUser.IsSuperAdmin)
            return Result<MfaEnrolResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result<MfaEnrolResponse>.NotFound(MessageKeys.User.NotFound);

        // Review fix #10: re-check the loaded row's own Role, never trust the JWT claim alone.
        if (user.Role?.IsSuperAdmin != true)
            return Result<MfaEnrolResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (user.TotpEnabledAt != null)
            return Result<MfaEnrolResponse>.Conflict(MessageKeys.Mfa.AlreadyEnabled);

        // Review fix #5 (Gemini): a passwordless (magic-link only) super admin has no password to
        // confirm enrollment with — refused outright, distinct message from a wrong password.
        if (user.PasswordlessOnly)
            return Result<MfaEnrolResponse>.Failure(MessageKeys.Mfa.PasswordlessCannotEnrol);

        if (!passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
            return Result<MfaEnrolResponse>.Failure(MessageKeys.Mfa.InvalidPassword);

        // A fresh enrol attempt (including a repeat before verify) always overwrites any prior
        // unverified secret — there is nothing to preserve until TotpEnabledAt is actually set.
        var secret = totp.GenerateSecret();
        user.TotpSecret = protector.Encrypt(secret);
        unitOfWork.Repository<User>().Update(user);
        await unitOfWork.SaveChangesAsync();

        return Result<MfaEnrolResponse>.Success(
            new MfaEnrolResponse
            {
                Secret = secret,
                OtpauthUrl = totp.GenerateOtpAuthUrl(user.Email, secret),
            }
        );
    }

    public async Task<Result<MfaVerifyResponse>> VerifyAsync(MfaCodeRequest request)
    {
        if (currentUser.KeyScopes is not null)
            return Result<MfaVerifyResponse>.Forbidden(MessageKeys.Mfa.KeySessionCannotMutateMfa);

        if (!currentUser.IsSuperAdmin)
            return Result<MfaVerifyResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result<MfaVerifyResponse>.NotFound(MessageKeys.User.NotFound);

        if (user.Role?.IsSuperAdmin != true)
            return Result<MfaVerifyResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (user.TotpEnabledAt != null)
            return Result<MfaVerifyResponse>.Conflict(MessageKeys.Mfa.AlreadyEnabled);

        if (string.IsNullOrEmpty(user.TotpSecret))
            return Result<MfaVerifyResponse>.Failure(MessageKeys.Mfa.NotEnrolled);

        // Review fix #5: same per-e-mail budget as a wrong login password (checked before the code
        // is even looked at, same precedent as LoginAsync/VerifyMfaLoginAsync).
        var emailNormalized = EmailNormalizer.NormalizeRequired(user.Email);
        if (await loginLimiter.IsLockedAsync(emailNormalized))
        {
            var retryAfter = await loginLimiter.GetRetryAfterSecondsAsync(emailNormalized);
            return Result<MfaVerifyResponse>.Locked(MessageKeys.Auth.TooManyAttempts, retryAfter);
        }

        var trimmedCode = (request.Code ?? string.Empty).Trim();
        var secret = protector.Decrypt(user.TotpSecret);

        // Review fix #1 (replay protection): a code whose step was already accepted is refused even
        // though it is otherwise a cryptographically valid code for the current window.
        if (
            secret == null
            || !totp.TryValidateCode(secret, trimmedCode, out var step)
            || step <= (user.TotpLastStep ?? -1)
        )
        {
            await loginLimiter.RecordFailureAsync(emailNormalized);
            await WriteChallengeFailedAsync(user);
            return Result<MfaVerifyResponse>.Failure(MessageKeys.Mfa.InvalidCode);
        }

        await loginLimiter.ResetAsync(emailNormalized);

        user.TotpEnabledAt = DateTime.UtcNow;
        user.TotpLastStep = step;
        unitOfWork.Repository<User>().Update(user);

        var plainCodes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = GenerateRecoveryCode();
            plainCodes.Add(code);
            await unitOfWork
                .Repository<UserRecoveryCode>()
                .AddAsync(
                    new UserRecoveryCode { UserId = user.Id, CodeHash = protector.HmacHex(code) }
                );
        }

        await unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthMfaEnrolled,
                AuditTargets.User,
                user.PublicId.ToString(),
                null
            )
        );

        return Result<MfaVerifyResponse>.Success(
            new MfaVerifyResponse { RecoveryCodes = plainCodes }
        );
    }

    public async Task<Result> DisableAsync(MfaCodeRequest request)
    {
        if (currentUser.KeyScopes is not null)
            return Result.Forbidden(MessageKeys.Mfa.KeySessionCannotMutateMfa);

        if (!currentUser.IsSuperAdmin)
            return Result.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (user.Role?.IsSuperAdmin != true)
            return Result.Forbidden(MessageKeys.Common.Forbidden);

        if (user.TotpEnabledAt == null || string.IsNullOrEmpty(user.TotpSecret))
            return Result.Failure(MessageKeys.Mfa.NotEnrolled);

        var emailNormalized = EmailNormalizer.NormalizeRequired(user.Email);
        if (await loginLimiter.IsLockedAsync(emailNormalized))
        {
            var retryAfter = await loginLimiter.GetRetryAfterSecondsAsync(emailNormalized);
            return Result.Locked(MessageKeys.Auth.TooManyAttempts, retryAfter);
        }

        // Review fix #5 (Gemini): disable requires the current password IN ADDITION to a valid
        // TOTP/recovery code — a stolen/left-open session alone is not enough to turn MFA off.
        if (!passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            await loginLimiter.RecordFailureAsync(emailNormalized);
            return Result.Failure(MessageKeys.Mfa.InvalidPassword);
        }

        if (!await ValidateCodeOrRecoveryAsync(user, request.Code ?? string.Empty))
        {
            await loginLimiter.RecordFailureAsync(emailNormalized);
            await WriteChallengeFailedAsync(user);
            return Result.Failure(MessageKeys.Mfa.InvalidCode);
        }

        await loginLimiter.ResetAsync(emailNormalized);

        user.TotpSecret = null;
        user.TotpEnabledAt = null;
        // Review fix #1: clear the replay watermark too — a future re-enrol starts with a clean
        // slate, same as the secret itself (finding #13: recovery code / step must not survive
        // disable → re-enrol).
        user.TotpLastStep = null;
        unitOfWork.Repository<User>().Update(user);

        var codes = await unitOfWork
            .Repository<UserRecoveryCode>()
            .Query()
            .Where(c => c.UserId == user.Id)
            .ToListAsync();
        if (codes.Count > 0)
            unitOfWork.Repository<UserRecoveryCode>().RemoveRange(codes);

        await unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthMfaDisabled,
                AuditTargets.User,
                user.PublicId.ToString(),
                null
            )
        );

        return Result.Success();
    }

    /// <summary>
    /// Shared by <see cref="DisableAsync"/> and <c>AuthService.VerifyMfaLoginAsync</c> (POST
    /// /api/auth/mfa/verify). Review fix #1: the TOTP branch enforces AND persists the replay
    /// watermark (<c>User.TotpLastStep</c>) in the same save that would otherwise be a bare read —
    /// a caller with no other pending write (login-completion) still gets the watermark saved.
    /// </summary>
    public async Task<bool> ValidateCodeOrRecoveryAsync(User user, string code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        if (!string.IsNullOrEmpty(user.TotpSecret))
        {
            var secret = protector.Decrypt(user.TotpSecret);
            if (
                secret != null
                && totp.TryValidateCode(secret, trimmed, out var step)
                && step > (user.TotpLastStep ?? -1)
            )
            {
                user.TotpLastStep = step;
                unitOfWork.Repository<User>().Update(user);
                await unitOfWork.SaveChangesAsync();
                return true;
            }
        }

        // Not a valid (non-replayed) TOTP code — try an unused recovery code. Matched on the
        // peppered HMAC (review fix #2), same one-way lookup shape as an API key.
        var hash = protector.HmacHex(trimmed);
        var recovery = await unitOfWork
            .Repository<UserRecoveryCode>()
            .Query()
            .Where(c => c.UserId == user.Id && c.UsedAt == null && c.CodeHash == hash)
            .FirstOrDefaultAsync();
        if (recovery == null)
            return false;

        recovery.UsedAt = DateTime.UtcNow;
        unitOfWork.Repository<UserRecoveryCode>().Update(recovery);
        await unitOfWork.SaveChangesAsync();
        return true;
    }

    private Task WriteChallengeFailedAsync(User user) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthMfaChallengeFailed,
                AuditTargets.User,
                user.PublicId.ToString(),
                null
            )
        );

    private static string GenerateRecoveryCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(RecoveryCodeLength);
        var sb = new StringBuilder(RecoveryCodeLength);
        foreach (var b in bytes)
            sb.Append(RecoveryCodeAlphabet[b % RecoveryCodeAlphabet.Length]);
        return sb.ToString();
    }
}
