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
    // Mirrors UserService's naming/lookup convention for the two global admin-tier roles.
    private const string WorkspaceAdminRoleName = "Workspace Admin";
    private const string DeputyRoleName = "Workspace Admin Deputy";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ISettingsService _settings;
    private readonly IEntitlementService _entitlements;
    private readonly IEmailService _emailService;
    private readonly IBrandingService _branding;

    private const int DefaultTtlDays = 7;
    private const string DefaultAppBaseUrl = "https://app.pointer.moamen.work";

    public InviteService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ISettingsService settings,
        IEntitlementService entitlements,
        IEmailService emailService,
        IBrandingService branding)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _settings = settings;
        _entitlements = entitlements;
        _emailService = emailService;
        _branding = branding;
    }

    // ── Admin (auth, tenant-scoped) ────────────────────────────────────────────

    public async Task<Result<InviteResponse>> CreateAsync(CreateInviteRequest request)
    {
        Guid? owner;
        Role? role = null;

        if (_currentUser.IsSuperAdmin)
        {
            if (request.CreateNewWorkspace)
            {
                // New-workspace invite: OwnerId stays null — accept mints a brand-new self-owned
                // tenant (its accepter becomes "Workspace Admin"), immediately active, same as
                // TenantService.CreateAsync's direct-create path but deferred to the invitee via a
                // shareable link. TargetOwnerId/RoleId are irrelevant here and ignored.
                owner = null;
            }
            else
            {
                // Same restriction as UserService.CreateAsync's super-admin branch: a super admin can
                // only invite a Deputy into an EXISTING workspace they explicitly pick — never mint a
                // new self-owned workspace this way, never pin any other role. request.RoleId is
                // ignored.
                if (request.TargetOwnerId is not Guid targetOwnerId)
                    return Result<InviteResponse>.Failure(MessageKeys.User.TargetWorkspaceRequired);

                var targetAdminExists = await _unitOfWork.Repository<User>()
                    .Query()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(u => u.OwnerId == targetOwnerId && u.DeletedAt == null && u.Role.Name == WorkspaceAdminRoleName);
                if (!targetAdminExists)
                    return Result<InviteResponse>.Failure(MessageKeys.User.WorkspaceNotFound);

                role = await _unitOfWork.Repository<Role>()
                    .Query()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Name == DeputyRoleName && r.DeletedAt == null && r.IsActive);
                if (role == null)
                    return Result<InviteResponse>.Failure(MessageKeys.Role.Invalid);

                owner = targetOwnerId;
            }
        }
        else
        {
            // Only a super admin may mint a whole new workspace via invite (mirrors
            // TenantService.CreateAsync's Policies.SuperAdmin gate).
            if (request.CreateNewWorkspace)
                return Result<InviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);

            // ISOLATION-LOAD-BEARING: a join-existing-tenant invite MUST carry a non-null owner —
            // it is the tenant boundary. Scoped admin → their tenant.
            var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
            if (ownerId is not Guid scopedOwner)
                return Result<InviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);
            owner = scopedOwner;

            // If a role is pinned, it must be a valid, active, NON-admin role of THIS tenant or
            // global — except Deputy, which the current Workspace Admin (or one of their deputies)
            // may delegate, same as via direct-add.
            if (request.RoleId is int pinnedRoleId)
            {
                role = await ResolvePinnableRoleAsync(pinnedRoleId, scopedOwner);
                if (role == null)
                    return Result<InviteResponse>.Failure(MessageKeys.Role.Invalid);
            }
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

        // Best-effort: an invite is still fully usable via its Url if the email never lands (email
        // disabled, capped, or send failure) — the admin can copy/share it manually. Never fail the
        // invite itself over a notification (mirrors UserService.SafeSendAsync).
        var emailSent = false;
        if (emailNormalized != null)
        {
            var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
            try
            {
                emailSent = await _emailService.SendAsync(emailNormalized,
                    $"You're invited to {brand.ProductName}",
                    BuildInviteEmailHtml(url, role?.Name, brand.ProductName, invite.ExpiresAt));
            }
            catch { /* logged inside the sender; ignore here */ }
        }

        var response = MapToResponse(invite, role?.Name, url);
        response.EmailSent = emailSent;
        return Result<InviteResponse>.Success(response, MessageKeys.Invite.Created);
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
    // owner) — mirrors PredefinedActionService.LoadOwnTenantWideAsync: never reachable cross-tenant.
    // M3: super-admins get full reach (they see all invites in ListAsync via the query filter; they
    // must be able to revoke any of them — mirroring the IsSuperAdmin bypass on all other loaders).
    private async Task<Invite?> LoadOwnAsync(int id)
    {
        if (_currentUser.IsSuperAdmin)
        {
            // Super-admin: bypass tenant scoping — can revoke any invite (consistent with ListAsync).
            return await _unitOfWork.Repository<Invite>()
                .Query()
                .IgnoreQueryFilters()
                .Where(i => i.Id == id && i.DeletedAt == null)
                .FirstOrDefaultAsync();
        }

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

        if (invite.OwnerId == null)
        {
            // New-workspace invite — no existing tenant to preview.
            return Result<InvitePreviewResponse>.Success(new InvitePreviewResponse
            {
                IsNewWorkspace = true,
                EmailLocked = invite.Email != null
            });
        }

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

        // L1: do NOT return the raw locked email to anonymous callers — return only the bool flag.
        // The server still enforces the lock on accept; the client renders a masked hint from
        // EmailLocked=true without knowing the actual address.
        return Result<InvitePreviewResponse>.Success(new InvitePreviewResponse
        {
            WorkspaceName = workspaceName ?? "Workspace",
            RoleName = roleName,
            EmailLocked = invite.Email != null
        });
    }

    public async Task<Result<LoginResponse>> AcceptAsync(AcceptInviteRequest request)
    {
        // M2: guard nulls before any .Trim()/.Hash() so null fields return 400 not 500.
        if (string.IsNullOrWhiteSpace(request.Code))
            return Result<LoginResponse>.Failure(MessageKeys.Invite.NotFound);
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<LoginResponse>.Failure(MessageKeys.User.EmailRequired);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Result<LoginResponse>.Failure(MessageKeys.User.PasswordWeak);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Result<LoginResponse>.Failure(MessageKeys.User.DisplayNameRequired);

        var emailNormalized = request.Email.Trim().ToLower();

        // 1. Resolve the invite (anonymous path → IgnoreQueryFilters, like RegisterAsync). Reject
        //    anything not currently acceptable. The invite is the authorization — validate fully.
        var invite = await ResolveValidInviteAsync(request.Code);
        if (invite == null)
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // Email lock: if set, only that email may accept.
        if (invite.Email != null && invite.Email != emailNormalized)
            return Result<LoginResponse>.Failure(MessageKeys.Invite.EmailMismatch);

        return invite.OwnerId is Guid existingOwnerId
            ? await AcceptJoinExistingWorkspaceAsync(invite, existingOwnerId, emailNormalized, request)
            : await AcceptCreateNewWorkspaceAsync(invite, emailNormalized, request);
    }

    // Joins an EXISTING tenant — the original accept flow (invite.OwnerId non-null).
    private async Task<Result<LoginResponse>> AcceptJoinExistingWorkspaceAsync(
        Invite invite, Guid ownerId, string emailNormalized, AcceptInviteRequest request)
    {
        // 2. Resolve the role: the invite's pinned RoleId if present (the admin already chose it at
        //    creation time — may be Deputy), else validate the anonymous acceptor's OWN submitted
        //    roleId as a non-admin role of the invite's tenant or global (never Deputy — an
        //    anonymous acceptor must never be able to self-select an admin-tier role).
        Role? role;
        if (invite.RoleId is int pinnedRoleId)
        {
            role = await ResolvePinnableRoleAsync(pinnedRoleId, ownerId);
        }
        else
        {
            if (request.RoleId is not int chosenRoleId)
                return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);
            role = await ResolveAssignableRoleAsync(chosenRoleId, ownerId);
        }

        if (role == null)
            return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);

        // 3. M1: scope the duplicate-email check to THIS invite's tenant only — a same-email user
        //    under a different tenant is not a conflict (the (email, owner_id) unique index allows it,
        //    and cross-tenant existence must not be revealed via 409 vs 400 distinction).
        var existing = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null
                        && u.Email == emailNormalized
                        && u.OwnerId == ownerId)
            .FirstOrDefaultAsync();

        if (existing != null)
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

        // MaxSeats: count active users owned by the invite's tenant. Checked BEFORE claiming a slot so
        // an over-limit accept never consumes a use. Grandfather-safe (counts DeletedAt == null, on add).
        var seatCount = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(u => u.OwnerId == ownerId && u.DeletedAt == null);
        var seatCheck = await _entitlements.CheckCountAsync(ownerId, EntitlementCatalog.MaxSeats, seatCount);
        if (!seatCheck.IsSuccess)
            return Result<LoginResponse>.LimitReached(seatCheck.Message ?? MessageKeys.Plan.LimitReached, seatCheck.Limit!);

        // 4. H1: atomically claim a usage slot BEFORE creating the user. The UnitOfWork issues a
        //    single UPDATE … WHERE (not deleted/revoked/expired AND uses < maxUses) … SET uses+=1
        //    returning rows-affected. Two concurrent requests both seeing Uses=0/MaxUses=1 cannot
        //    both succeed — only one gets claimed=1; the other gets claimed=0 and is rejected
        //    without ever creating a user. This replaces the old read-check-then-increment pattern.
        var claimed = await _unitOfWork.AtomicClaimInviteSlotAsync(invite.Id, DateTime.UtcNow);

        if (claimed == 0)
            // Exhausted or revoked concurrently — do NOT create the user.
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // 5. Create the user pre-authorized + pre-scoped to the invite's tenant. The invite is the
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
            OwnerId = ownerId
        };

        try
        {
            await _unitOfWork.Repository<User>().AddAsync(newUser);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // L2: duplicate-email insert race (two concurrent accepts with the same email both
            // pass the check above; the second violates the unique index (email, owner_id)).
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);
        }

        // 6. Auto-signin: return a login token + user (reuse the login response builder).
        newUser.Role = role;
        var token = _tokenService.Issue(newUser);
        return Result<LoginResponse>.Success(new LoginResponse
        {
            Status = "ok",
            Token = token,
            User = UserMapper.ToMeResponse(newUser)
        });
    }

    // Mints a brand-new self-owned tenant (invite.OwnerId is null) — the invitee becomes its
    // "Workspace Admin", approved and active immediately. Mirrors TenantService.CreateAsync's
    // direct-create path (including the OwnerId==u.PublicId duplicate-email scoping and the
    // absence of a seat-limit check — there is no existing tenant to check limits against yet).
    private async Task<Result<LoginResponse>> AcceptCreateNewWorkspaceAsync(
        Invite invite, string emailNormalized, AcceptInviteRequest request)
    {
        var exists = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.DeletedAt == null && u.Email == emailNormalized && u.OwnerId == u.PublicId);

        if (exists)
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

        var workspaceAdminRole = await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.DeletedAt == null && r.IsActive
                                      && r.Name == WorkspaceAdminRoleName && r.OwnerId == null);
        if (workspaceAdminRole == null)
            return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);

        var claimed = await _unitOfWork.AtomicClaimInviteSlotAsync(invite.Id, DateTime.UtcNow);
        if (claimed == 0)
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        var publicId = Guid.NewGuid();
        var newUser = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            RoleId = workspaceAdminRole.Id,
            PublicId = publicId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = publicId // a brand-new tenant owns itself
        };

        try
        {
            await _unitOfWork.Repository<User>().AddAsync(newUser);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);
        }

        newUser.Role = workspaceAdminRole;
        var token = _tokenService.Issue(newUser);
        return Result<LoginResponse>.Success(new LoginResponse
        {
            Status = "ok",
            Token = token,
            User = UserMapper.ToMeResponse(newUser)
        });
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
    // Used for: an anonymous acceptor's FREE choice of role on an unpinned invite — must never
    // include Deputy, or anyone could self-escalate to admin-tier by guessing/enumerating its id.
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

    // Same as ResolveAssignableRoleAsync but also allows "Workspace Admin Deputy". Used only where
    // the role was chosen by an authenticated admin-tier caller, never by an anonymous acceptor:
    // (a) an admin PINNING a role on CreateAsync, (b) accept-time resolution of an already-pinned
    // invite.RoleId (the admin made that choice at creation time, not the anonymous acceptor now).
    private async Task<Role?> ResolvePinnableRoleAsync(int roleId, Guid ownerId)
    {
        return await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId
                                      && r.DeletedAt == null
                                      && r.IsActive
                                      && (!r.GrantsAdmin && !r.IsSuperAdmin || r.Name == DeputyRoleName)
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

    private static string BuildInviteEmailHtml(string joinUrl, string? roleName, string productName, DateTime expiresAtUtc)
    {
        var roleLine = roleName != null
            ? $"<p style=\"margin:0 0 16px\">You've been invited to join as <b>{roleName}</b>.</p>"
            : string.Empty;
        return $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">You're invited to {productName} 🐕</h2>
  {roleLine}
  <p style=""margin:0 0 16px""><a href=""{joinUrl}"" style=""display:inline-block;background:#2563eb;color:#fff;padding:10px 20px;border-radius:8px;text-decoration:none"">Accept invite →</a></p>
  <p style=""margin:0;color:#475569;font-size:13px"">This link expires on {expiresAtUtc:yyyy-MM-dd HH:mm} UTC. If you weren't expecting this, you can ignore this email.</p>
</div>";
    }

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
