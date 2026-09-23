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
/// </summary>
public class MfaService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IApiKeyProtector protector,
    ITotpService totp,
    IAuditWriter? audit = null
) : IMfaService
{
    private const int RecoveryCodeCount = 8;
    private const int RecoveryCodeLength = 8;

    // No 0/O/1/I — avoids characters that are easy to misread when copying a recovery code by hand.
    private static readonly char[] RecoveryCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

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

    public async Task<Result<MfaEnrolResponse>> EnrolAsync()
    {
        if (!currentUser.IsSuperAdmin)
            return Result<MfaEnrolResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result<MfaEnrolResponse>.NotFound(MessageKeys.User.NotFound);

        if (user.TotpEnabledAt != null)
            return Result<MfaEnrolResponse>.Conflict(MessageKeys.Mfa.AlreadyEnabled);

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
        if (!currentUser.IsSuperAdmin)
            return Result<MfaVerifyResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result<MfaVerifyResponse>.NotFound(MessageKeys.User.NotFound);

        if (user.TotpEnabledAt != null)
            return Result<MfaVerifyResponse>.Conflict(MessageKeys.Mfa.AlreadyEnabled);

        if (string.IsNullOrEmpty(user.TotpSecret))
            return Result<MfaVerifyResponse>.Failure(MessageKeys.Mfa.NotEnrolled);

        var secret = protector.Decrypt(user.TotpSecret);
        if (secret == null || !totp.ValidateCode(secret, request.Code ?? string.Empty))
        {
            await WriteChallengeFailedAsync(user);
            return Result<MfaVerifyResponse>.Failure(MessageKeys.Mfa.InvalidCode);
        }

        user.TotpEnabledAt = DateTime.UtcNow;
        unitOfWork.Repository<User>().Update(user);

        var plainCodes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = GenerateRecoveryCode();
            plainCodes.Add(code);
            await unitOfWork
                .Repository<UserRecoveryCode>()
                .AddAsync(new UserRecoveryCode { UserId = user.Id, CodeHash = protector.Hash(code) });
        }

        await unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AuthMfaEnrolled, AuditTargets.User, user.PublicId.ToString(), null)
        );

        return Result<MfaVerifyResponse>.Success(new MfaVerifyResponse { RecoveryCodes = plainCodes });
    }

    public async Task<Result> DisableAsync(MfaCodeRequest request)
    {
        if (!currentUser.IsSuperAdmin)
            return Result.Forbidden(MessageKeys.Common.Forbidden);

        var user = await CurrentUserRowAsync();
        if (user == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (user.TotpEnabledAt == null || string.IsNullOrEmpty(user.TotpSecret))
            return Result.Failure(MessageKeys.Mfa.NotEnrolled);

        if (!await ValidateCodeOrRecoveryAsync(user, request.Code ?? string.Empty))
        {
            await WriteChallengeFailedAsync(user);
            return Result.Failure(MessageKeys.Mfa.InvalidCode);
        }

        user.TotpSecret = null;
        user.TotpEnabledAt = null;
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
            new AuditEntry(AuditActions.AuthMfaDisabled, AuditTargets.User, user.PublicId.ToString(), null)
        );

        return Result.Success();
    }

    public async Task<bool> ValidateCodeOrRecoveryAsync(User user, string code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        if (!string.IsNullOrEmpty(user.TotpSecret))
        {
            var secret = protector.Decrypt(user.TotpSecret);
            if (secret != null && totp.ValidateCode(secret, trimmed))
                return true;
        }

        // Not a valid TOTP code (or none on file) — try an unused recovery code. Matched on hash,
        // same one-way lookup as an API key.
        var hash = protector.Hash(trimmed);
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
            new AuditEntry(AuditActions.AuthMfaChallengeFailed, AuditTargets.User, user.PublicId.ToString(), null)
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
