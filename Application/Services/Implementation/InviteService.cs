using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Tenant invite links/codes. Admin CRUD is tenant-scoped (strict-own query filter + explicit
/// own-owner load on revoke); accept is anonymous but the invite itself is the authorization, so
/// it is validated thoroughly (exists, not revoked/expired/used-up, email-lock) before a user is
/// created Approved + active.
/// </summary>
public class InviteService : IInviteService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ISettingsService _settings;

    private const int DefaultTtlDays = 7;
    private const string DefaultAppBaseUrl = "https://app.pointer.moamen.work";

    public InviteService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ISettingsService settings)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _settings = settings;
    }

    // ── Admin (auth, tenant-scoped) ────────────────────────────────────────────

    public async Task<Result<InviteResponse>> CreateAsync(CreateInviteRequest request)
    {
        // ISOLATION-LOAD-BEARING: an invite MUST carry a non-null owner — it is the tenant boundary.
        // Scoped admin → their tenant; super-admin (no tenant) → their own id, so a super-admin
        // acting for a workspace still produces a concretely-owned invite (never a null-owner row
        // that the strict-own filter can't scope).
        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner)
            return Result<InviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);

        // If a role is pinned, it must be a valid, active, NON-admin role of THIS tenant or global.
        Role? role = null;
        if (request.RoleId is int pinnedRoleId)
        {
            role = await ResolveAssignableRoleAsync(pinnedRoleId, owner);
            if (role == null)
                return Result<InviteResponse>.Failure(MessageKeys.Role.Invalid);
        }

        var ttlDays = request.ExpiresInDays is int d && d > 0 ? d : DefaultTtlDays;
        var emailNormalized = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : request.Email.Trim().ToLower();
        var maxUses = request.MaxUses is int m && m > 0 ? m : (int?)null;

        var invite = new Invite
        {
            OwnerId = owner,
            Code = GenerateCode(),
            RoleId = role?.Id,
            Email = emailNormalized,
            ExpiresAt = DateTime.UtcNow.AddDays(ttlDays),
            MaxUses = maxUses,
            Uses = 0,
            RevokedAt = null
        };

        await _unitOfWork.Repository<Invite>().AddAsync(invite);
        await _unitOfWork.SaveChangesAsync();

        var url = await BuildJoinUrlAsync(invite.Code);
        return Result<InviteResponse>.Success(MapToResponse(invite, role?.Name, url), MessageKeys.Invite.Created);
    }

    public async Task<Result<List<InviteResponse>>> ListAsync()
    {
        var now = DateTime.UtcNow;

        // Query filter scopes OwnerId to the tenant (JWT). Active = not deleted / revoked / expired.
        var rows = await _unitOfWork.Repository<Invite>()
            .Query()
            .AsNoTracking()
            .Where(i => i.DeletedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        // Resolve role names for any pinned roles (single round-trip). Own-plus-global filter is fine
        // for reads here — we only surface the name of a role the admin already pinned.
        var roleIds = rows.Where(i => i.RoleId != null).Select(i => i.RoleId!.Value).Distinct().ToList();
        var roleNames = roleIds.Count == 0
            ? new Dictionary<int, string>()
            : await _unitOfWork.Repository<Role>()
                .Query()
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Name);

        var appBaseUrl = await GetAppBaseUrlAsync();
        var list = rows
            .Select(i => MapToResponse(
                i,
                i.RoleId != null && roleNames.TryGetValue(i.RoleId.Value, out var n) ? n : null,
                BuildJoinUrl(appBaseUrl, i.Code)))
            .ToList();

        return Result<List<InviteResponse>>.Success(list);
    }

    public async Task<Result> RevokeAsync(int id)
    {
        var invite = await LoadOwnAsync(id);
        if (invite == null)
            return Result.NotFound(MessageKeys.Invite.NotFound);

        invite.RevokedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Invite>().Update(invite);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(MessageKeys.Invite.Revoked_Ok);
    }

    // Loads an invite owned by the caller's tenant. Explicitly scoped (IgnoreQueryFilters + own
    // owner) — mirrors PredefinedActionService.LoadOwnTenantWideAsync: never reachable cross-tenant,
    // and a super-admin acting for a tenant still matches only invites it concretely owns.
    private async Task<Invite?> LoadOwnAsync(int id)
    {
        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner) return null;

        return await _unitOfWork.Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .Where(i => i.Id == id && i.DeletedAt == null && i.OwnerId == owner)
            .FirstOrDefaultAsync();
    }

    // ── Anonymous accept flow ──────────────────────────────────────────────────

    public async Task<Result<InvitePreviewResponse>> GetPreviewAsync(string code)
    {
        var invite = await ResolveValidInviteAsync(code);
        if (invite == null)
            return Result<InvitePreviewResponse>.NotFound(MessageKeys.Invite.NotFound);

        // SAFE preview only: the owning admin's DisplayName + the pinned role's name. NEVER the
        // tenant GUID, the invite id, or any secret. Anonymous path → bypass filters and scope
        // explicitly to the invite's own OwnerId.
        var workspaceName = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null
                        && u.OwnerId == invite.OwnerId
                        && (u.Role.GrantsAdmin || u.Role.IsSuperAdmin))
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync();

        string? roleName = null;
        if (invite.RoleId is int rid)
        {
            roleName = await _unitOfWork.Repository<Role>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == rid)
                .Select(r => r.Name)
                .FirstOrDefaultAsync();
        }

        return Result<InvitePreviewResponse>.Success(new InvitePreviewResponse
        {
            WorkspaceName = workspaceName ?? "Workspace",
            RoleName = roleName,
            EmailLocked = invite.Email != null,
            Email = invite.Email
        });
    }

    public async Task<Result<LoginResponse>> AcceptAsync(AcceptInviteRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        // 1. Resolve the invite (anonymous path → IgnoreQueryFilters, like RegisterAsync). Reject
        //    anything not currently acceptable. The invite is the authorization — validate fully.
        var invite = await ResolveValidInviteAsync(request.Code);
        if (invite == null)
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // Email lock: if set, only that email may accept.
        if (invite.Email != null && invite.Email != emailNormalized)
            return Result<LoginResponse>.Failure(MessageKeys.Invite.EmailMismatch);

        // 2. Resolve the role: the invite's pinned RoleId if present, else validate the submitted
        //    roleId is a non-admin role of the invite's tenant or global (mirrors RegisterAsync).
        Role? role;
        if (invite.RoleId is int pinnedRoleId)
        {
            role = await ResolveAssignableRoleAsync(pinnedRoleId, invite.OwnerId);
        }
        else
        {
            if (request.RoleId is not int chosenRoleId)
                return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);
            role = await ResolveAssignableRoleAsync(chosenRoleId, invite.OwnerId);
        }

        if (role == null)
            return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);

        // 3. Look up any existing user by email (anonymous path → bypass User filter).
        var existing = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null && u.Email.ToLower() == emailNormalized)
            .FirstOrDefaultAsync();

        if (existing != null)
            // Same "account exists" handling as RegisterAsync — one account per email.
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

        // 4. Create the user pre-authorized + pre-scoped to the invite's tenant. The invite is the
        //    authorization, so we SKIP the pending approval queue: Approved + active immediately.
        var newUser = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            RoleId = role.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = invite.OwnerId
        };

        await _unitOfWork.Repository<User>().AddAsync(newUser);

        // 5. Increment Uses on the invite (re-load tracked, bypassing filters — anonymous path).
        var trackedInvite = await _unitOfWork.Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .FirstAsync(i => i.Id == invite.Id);
        trackedInvite.Uses += 1;
        _unitOfWork.Repository<Invite>().Update(trackedInvite);

        await _unitOfWork.SaveChangesAsync();

        // 6. Auto-signin: return a login token + user (reuse the login response builder).
        newUser.Role = role;
        var token = _tokenService.Issue(newUser);
        var response = new LoginResponse
        {
            Status = "ok",
            Token = token,
            User = UserMapper.ToMeResponse(newUser)
        };

        return Result<LoginResponse>.Success(response);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Resolves an invite by code that is currently acceptable: exists, not deleted, not revoked,
    // not expired, and uses remaining. Anonymous path → IgnoreQueryFilters (no tenant claim yet).
    private async Task<Invite?> ResolveValidInviteAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();
        var now = DateTime.UtcNow;

        var invite = await _unitOfWork.Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.Code == trimmed
                        && i.DeletedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now)
            .FirstOrDefaultAsync();

        if (invite == null) return null;

        // Usage cap (evaluated in-memory: null MaxUses = unlimited within TTL).
        if (invite.MaxUses is int max && invite.Uses >= max) return null;

        return invite;
    }

    // Validates that roleId is a valid, active, NON-admin role of the given tenant owner or a
    // global (null-owner) role. Mirrors RegisterAsync.cs role resolution. Anonymous/cross-tenant
    // safe: scoped explicitly to the invite's owner (or null-owner globals), never the caller's.
    private async Task<Role?> ResolveAssignableRoleAsync(int roleId, Guid ownerId)
    {
        return await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId
                                      && r.DeletedAt == null
                                      && r.IsActive
                                      && !r.GrantsAdmin
                                      && !r.IsSuperAdmin
                                      && (r.OwnerId == ownerId || r.OwnerId == null));
    }

    // 128-bit crypto-random, URL-safe (base64url, no padding) — same encoding style as
    // ResetTokenService / UploadSigner. Not signed: it is a DB row we look up.
    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<string> GetAppBaseUrlAsync()
    {
        var configured = await _settings.GetStringAsync(ISettingsService.AppBaseUrl, DefaultAppBaseUrl);
        return string.IsNullOrWhiteSpace(configured) ? DefaultAppBaseUrl : configured.TrimEnd('/');
    }

    private async Task<string> BuildJoinUrlAsync(string code) =>
        BuildJoinUrl(await GetAppBaseUrlAsync(), code);

    private static string BuildJoinUrl(string appBaseUrl, string code) =>
        $"{appBaseUrl}/join?code={Uri.EscapeDataString(code)}";

    private static InviteResponse MapToResponse(Invite i, string? roleName, string url) => new()
    {
        Id = i.Id,
        Code = i.Code,
        Url = url,
        RoleId = i.RoleId,
        RoleName = roleName,
        Email = i.Email,
        ExpiresAt = i.ExpiresAt,
        MaxUses = i.MaxUses,
        Uses = i.Uses
    };
}
