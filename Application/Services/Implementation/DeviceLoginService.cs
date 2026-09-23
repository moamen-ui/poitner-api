using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using System.Security.Cryptography;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Implements the browser-based ("device code") CLI sign-in described on
/// <see cref="Pointer.Domain.Entity.DeviceLogin"/>.
/// </summary>
public class DeviceLoginService : IDeviceLoginService
{
    private const int ExpiresInSeconds = 600;
    private const int IntervalSeconds = 3;

    // Excludes 0/O/1/I — the whole point of a "type this into the browser" code is that every
    // character is unambiguous read aloud or in an unfamiliar font.
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IApiKeyService _apiKeys;
    private readonly IBrandingService _branding;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;

    public DeviceLoginService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IApiKeyService apiKeys,
        IBrandingService branding,
        IMembershipService memberships,
        IAuditWriter? audit = null)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _apiKeys = apiKeys;
        _branding = branding;
        _memberships = memberships;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    public async Task<Result<DeviceLoginStartResponse>> StartAsync(DeviceLoginStartRequest request)
    {
        var now = DateTime.UtcNow;

        // No background job exists for this table — sweep long-dead rows (more than a day past
        // expiry) inline on the one path that creates new ones. Cheap: this table churns fast and
        // never accumulates more than a day's worth of abandoned flows between calls.
        var stale = await _unitOfWork.Repository<DeviceLogin>().Query()
            .Where(d => d.ExpiresAt < now.AddDays(-1))
            .ToListAsync();
        if (stale.Count > 0)
        {
            _unitOfWork.Repository<DeviceLogin>().RemoveRange(stale);
            await _unitOfWork.SaveChangesAsync();
        }

        var clientName = string.IsNullOrWhiteSpace(request.ClientName)
            ? "pointer-feedback CLI"
            : request.ClientName.Trim();

        var rawDeviceCode = QuickAccessTokenGenerator.NewToken();
        var userCode = await GenerateUniqueUserCodeAsync(now);

        var row = new DeviceLogin
        {
            DeviceCodeHash = QuickAccessTokenGenerator.Hash(rawDeviceCode),
            UserCode = userCode,
            ClientName = clientName,
            Status = DeviceLoginStatus.Pending,
            ExpiresAt = now.AddSeconds(ExpiresInSeconds),
        };
        await _unitOfWork.Repository<DeviceLogin>().AddAsync(row);
        await _unitOfWork.SaveChangesAsync();

        // Urls.App is an absolute, admin-configured value (not built from the request's own
        // origin) — same "" publicBase AuthService.RequestPasswordResetAsync passes when it only
        // needs Urls.App and not any asset URL.
        var branding = await _branding.BuildResponseAsync("", new HashSet<string>());
        var appUrl = branding.Urls.App.TrimEnd('/');
        var verificationUrl = $"{appUrl}/cli-login?code={Uri.EscapeDataString(userCode)}";

        return Result<DeviceLoginStartResponse>.Success(new DeviceLoginStartResponse
        {
            DeviceCode = rawDeviceCode,
            UserCode = userCode,
            VerificationUrl = verificationUrl,
            ExpiresInSeconds = ExpiresInSeconds,
            IntervalSeconds = IntervalSeconds,
        });
    }

    public async Task<Result<DeviceLoginPollResponse>> PollAsync(DeviceLoginPollRequest request)
    {
        var now = DateTime.UtcNow;
        var deviceCode = (request.DeviceCode ?? string.Empty).Trim();
        if (deviceCode.Length == 0)
            return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "unknown" });

        var hash = QuickAccessTokenGenerator.Hash(deviceCode);

        // Anonymous path with no session at all — this table is deliberately exempt from the
        // tenant query filter (see AppDbContext / DeviceLogin remarks), so no IgnoreQueryFilters()
        // is needed here.
        var row = await _unitOfWork.Repository<DeviceLogin>().Query()
            .FirstOrDefaultAsync(d => d.DeviceCodeHash == hash && d.DeletedAt == null);

        if (row is null)
            return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "unknown" });

        if (row.Status == DeviceLoginStatus.Pending && row.ExpiresAt <= now)
            return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "expired" });

        switch (row.Status)
        {
            case DeviceLoginStatus.Pending:
                return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "pending" });

            case DeviceLoginStatus.Denied:
                return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "denied" });

            // Already handed out once — reported the same as a row nobody approved, so a leaked
            // device code (or a CLI that polled twice) cannot replay this to fetch the key again.
            case DeviceLoginStatus.Consumed:
                return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "expired" });

            case DeviceLoginStatus.Approved:
                if (row.UserId is not Guid userId)
                    // Should be unreachable (Approve always stamps UserId) — treat as expired
                    // rather than throwing on a caller-facing anonymous endpoint.
                    return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "expired" });

                var user = await _unitOfWork.Repository<User>().Query()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.PublicId == userId && u.DeletedAt == null);

                if (user is null)
                    return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "expired" });

                var keyResult = await _apiKeys.GetOrCreateAsync(user.PublicId, row.OwnerId);
                if (!keyResult.Found || keyResult.RawKey is null)
                    // Key exists but couldn't be decrypted/minted — nothing to hand the CLI. Report
                    // "expired" (a retryable "start over"), never leave the row Approved-forever.
                    return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "expired" });

                row.Status = DeviceLoginStatus.Consumed;
                row.ConsumedAt = now;
                _unitOfWork.Repository<DeviceLogin>().Update(row);
                await _unitOfWork.SaveChangesAsync();

                var branding = await _branding.BuildResponseAsync("", new HashSet<string>());

                return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse
                {
                    Status = "approved",
                    ApiKey = keyResult.RawKey,
                    DisplayName = user.DisplayName,
                    Email = user.Email,
                    Server = branding.Urls.App,
                });

            default:
                return Result<DeviceLoginPollResponse>.Success(new DeviceLoginPollResponse { Status = "unknown" });
        }
    }

    public async Task<Result<DeviceLoginInfoResponse>> GetInfoAsync(string userCode)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<DeviceLoginInfoResponse>.Forbidden(MessageKeys.DeviceLogin.SuperAdminNotAllowed);

        var now = DateTime.UtcNow;
        var row = await FindLatestByUserCodeAsync(userCode);

        if (row is null || (row.Status == DeviceLoginStatus.Pending && row.ExpiresAt <= now))
            return Result<DeviceLoginInfoResponse>.NotFound(MessageKeys.DeviceLogin.NotFound);

        return Result<DeviceLoginInfoResponse>.Success(ToInfo(row, now));
    }

    public async Task<Result<DeviceLoginInfoResponse>> ApproveAsync(string userCode)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<DeviceLoginInfoResponse>.Forbidden(MessageKeys.DeviceLogin.SuperAdminNotAllowed);
        if (_currentUser.Id is not Guid publicId)
            return Result<DeviceLoginInfoResponse>.Failure(MessageKeys.Auth.InvalidCredentials);
        // S-14: a non-super-admin without a tenant claim is Forbidden, never approves into a null workspace.
        if (_currentUser.TenantId is not Guid tenant)
            return Result<DeviceLoginInfoResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-11a/R16: the caller's own MEMBERSHIP in this workspace must be live, active and
        // Approved — a disabled/removed admin cannot approve a device login.
        var callerIdentity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        var callerMembership = callerIdentity != null
            ? await _memberships.GetMembershipAsync(callerIdentity.Id, tenant)
            : null;
        if (callerMembership == null || !callerMembership.IsActive || callerMembership.ApprovalStatus != Domain.Enums.ApprovalStatus.Approved)
            return Result<DeviceLoginInfoResponse>.Forbidden(MessageKeys.Auth.Disabled);

        var now = DateTime.UtcNow;
        var row = await FindLatestByUserCodeAsync(userCode);

        if (row is null || (row.Status == DeviceLoginStatus.Pending && row.ExpiresAt <= now))
            return Result<DeviceLoginInfoResponse>.NotFound(MessageKeys.DeviceLogin.NotFound);

        if (row.Status != DeviceLoginStatus.Pending)
            return Result<DeviceLoginInfoResponse>.Conflict(MessageKeys.DeviceLogin.AlreadyDecided);

        row.Status = DeviceLoginStatus.Approved;
        row.UserId = publicId;
        row.OwnerId = _currentUser.TenantId;
        row.ApprovedAt = now;
        _unitOfWork.Repository<DeviceLogin>().Update(row);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.DeviceApproved,
                AuditTargets.DeviceLogin,
                row.Id.ToString(),
                _currentUser.TenantId
            )
        );

        return Result<DeviceLoginInfoResponse>.Success(ToInfo(row, now));
    }

    public async Task<Result<DeviceLoginInfoResponse>> DenyAsync(string userCode)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<DeviceLoginInfoResponse>.Forbidden(MessageKeys.DeviceLogin.SuperAdminNotAllowed);

        var now = DateTime.UtcNow;
        var row = await FindLatestByUserCodeAsync(userCode);

        if (row is null || (row.Status == DeviceLoginStatus.Pending && row.ExpiresAt <= now))
            return Result<DeviceLoginInfoResponse>.NotFound(MessageKeys.DeviceLogin.NotFound);

        if (row.Status != DeviceLoginStatus.Pending)
            return Result<DeviceLoginInfoResponse>.Conflict(MessageKeys.DeviceLogin.AlreadyDecided);

        row.Status = DeviceLoginStatus.Denied;
        _unitOfWork.Repository<DeviceLogin>().Update(row);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.DeviceDenied,
                AuditTargets.DeviceLogin,
                row.Id.ToString(),
                _currentUser.TenantId
            )
        );

        return Result<DeviceLoginInfoResponse>.Success(ToInfo(row, now));
    }

    /// <summary>UserCode uniqueness is enforced only among non-terminal rows (see the entity's
    /// remarks), so more than one row can share a code across time. The one that matters for
    /// GetInfo/Approve/Deny is always the most recently created — order by CreatedAt so a stale
    /// terminal row from an earlier cycle is never picked over the live one.</summary>
    private async Task<DeviceLogin?> FindLatestByUserCodeAsync(string userCode)
    {
        var normalized = (userCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0) return null;

        return await _unitOfWork.Repository<DeviceLogin>().Query()
            .Where(d => d.UserCode == normalized && d.DeletedAt == null)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync();
    }

    private static DeviceLoginInfoResponse ToInfo(DeviceLogin row, DateTime now) => new()
    {
        UserCode = row.UserCode,
        ClientName = row.ClientName,
        CreatedAt = row.CreatedAt,
        ExpiresAt = row.ExpiresAt,
        Status = row.Status switch
        {
            DeviceLoginStatus.Pending => row.ExpiresAt <= now ? "expired" : "pending",
            DeviceLoginStatus.Approved => "approved",
            DeviceLoginStatus.Denied => "denied",
            // Already consumed by the CLI — from the dashboard's point of view this code did its
            // job; there is no "already used" state worth distinguishing from "approved" here.
            DeviceLoginStatus.Consumed => "approved",
            _ => "expired",
        },
    };

    private async Task<string> GenerateUniqueUserCodeAsync(DateTime now)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = GenerateUserCode();
            var collides = await _unitOfWork.Repository<DeviceLogin>().Query()
                .AnyAsync(d => d.UserCode == candidate && d.DeletedAt == null &&
                    ((d.Status == DeviceLoginStatus.Pending && d.ExpiresAt > now) || d.Status == DeviceLoginStatus.Approved));
            if (!collides) return candidate;
        }

        // Astronomically unlikely with a 32^8 space — fail loudly rather than silently hand out a
        // colliding code.
        throw new InvalidOperationException("Could not generate a unique device login code.");
    }

    private static string GenerateUserCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[9];
        for (var i = 0; i < 4; i++) chars[i] = UserCodeAlphabet[bytes[i] % UserCodeAlphabet.Length];
        chars[4] = '-';
        for (var i = 4; i < 8; i++) chars[i + 1] = UserCodeAlphabet[bytes[i] % UserCodeAlphabet.Length];
        return new string(chars);
    }
}
