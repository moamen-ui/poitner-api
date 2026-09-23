using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Common.Email;
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
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;
    private readonly IEmailVerificationService _emailVerification;
    private readonly IDemoService? _demo;
    private readonly ILogger<InviteService>? _logger;

    private const int DefaultTtlDays = 7;

    /// <summary>Magic links live longer than a staff invite: a client uses theirs repeatedly.</summary>
    private const int QuickAccessLinkTtlDays = 14;
    private const string DefaultAppBaseUrl = "https://app.pointer.moamen.work";

    public InviteService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ISettingsService settings,
        IEntitlementService entitlements,
        IEmailService emailService,
        IBrandingService branding,
        IMembershipService memberships,
        IAuditWriter? audit = null,
        IEmailVerificationService? emailVerification = null,
        // DB-17 review finding #7: nullable-with-default, last — every existing hand-rolled test
        // construction compiles unchanged and simply skips the demo-TTL-clearing side effect (with
        // a logged warning, never a silent no-op — Gemini's ask).
        IDemoService? demo = null,
        ILogger<InviteService>? logger = null
    )
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _settings = settings;
        _entitlements = entitlements;
        _emailService = emailService;
        _branding = branding;
        _memberships = memberships;
        _audit = audit ?? NoopAuditWriter.Instance;
        _emailVerification = emailVerification ?? NoopEmailVerification.Instance;
        _demo = demo;
        _logger = logger;
    }

    /// <summary>
    /// DB-17 review finding #7: every site that flips <c>identity.EmailVerifiedAt</c> in this
    /// service (an addressed invite proves the address by delivery) also clears the TTL of any
    /// converted-but-unverified workspace this identity administers elsewhere
    /// (<c>Demo:ConvertRequiresVerification</c> mode; a no-op with the flag off — mirrors
    /// AuthService's ConfirmEmailChangeAsync and EmailVerificationService's ConfirmAsync call
    /// sites). Best-effort: never fails the invite accept/create it is called from. Gemini: if
    /// <see cref="IDemoService"/> was never wired in (no DI container — a hand-rolled test double),
    /// that is logged as a Warning, not silently skipped.
    /// </summary>
    private async Task NotifyDemoEmailVerifiedAsync(User identity)
    {
        if (_demo == null)
        {
            _logger?.LogWarning(
                "InviteService: IDemoService not available — skipped OnEmailVerifiedAsync for {PublicId}",
                identity.PublicId
            );
            return;
        }

        try
        {
            await _demo.OnEmailVerifiedAsync(identity);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "InviteService: OnEmailVerifiedAsync failed for {PublicId}",
                identity.PublicId
            );
        }
    }

    // ── Admin (auth, tenant-scoped) ────────────────────────────────────────────

    public async Task<Result<InviteResponse>> CreateAsync(CreateInviteRequest request, bool writeAudit = true)
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

                var targetAdmin = await _memberships.CurrentAdminAsync(targetOwnerId);
                if (targetAdmin == null)
                    return Result<InviteResponse>.Failure(MessageKeys.User.WorkspaceNotFound);

                role = await _unitOfWork
                    .Repository<Role>()
                    .Query()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r =>
                        r.Name == DeputyRoleName && r.DeletedAt == null && r.IsActive
                    );
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
            // it is the tenant boundary. S-14: a non-super-admin without a tenant claim is Forbidden,
            // never minted a tenant from its own id.
            if (!TenantStamp.TryRequireOwner(_currentUser, out var scopedOwner))
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

        // Quick-access role (e.g. "Client"): skip the normal defer-to-accept flow entirely — eagerly
        // provision the User now with a generated password, emailed together with a direct link to
        // the target project. `owner` is always a concrete tenant here: the only branch above that
        // leaves `role` null (CreateNewWorkspace) can never resolve a QuickAccess role.
        if (role != null && role.QuickAccess)
        {
            if (owner is not Guid quickOwner)
                return Result<InviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);
            return await CreateQuickAccessInviteAsync(quickOwner, role, request);
        }

        var ttlDays = request.ExpiresInDays is int d && d > 0 ? d : DefaultTtlDays;
        var emailNormalized = EmailNormalizer.Normalize(request.Email);
        var maxUses = request.MaxUses is int m && m > 0 ? m : (int?)null;

        // A workspace invite mints a whole tenant, so it is held to stricter rules than a member
        // invite. These are enforced here, not only in the new validator, because the legacy
        // /api/admin/invites route reaches this same branch through CreateInviteRequestValidator,
        // which allows an unlimited-use invite and a TTL of up to a year.
        if (request.CreateNewWorkspace)
        {
            // Email-locked: an unlocked link is a workspace anyone who sees it can claim.
            if (emailNormalized is null)
                return Result<InviteResponse>.Failure(MessageKeys.User.EmailRequired);

            // Single-use. Left unlimited, one leaked link could mint N workspaces.
            maxUses = 1;

            // Bounded lifetime; a year-long workspace-minting link is not a reasonable artefact.
            ttlDays = Math.Clamp(ttlDays, 1, 30);

            // DB-11a (D13): one identity may administer several workspaces — the old
            // "an address that already owns a workspace cannot accept" refusal is removed. Accept
            // now join-or-creates the identity (AcceptCreateNewWorkspaceAsync).
        }

        var invite = new Invite
        {
            OwnerId = owner,
            Code = GenerateCode(),
            RoleId = role?.Id,
            Email = emailNormalized,
            ExpiresAt = DateTime.UtcNow.AddDays(ttlDays),
            MaxUses = maxUses,
            Uses = 0,
            RevokedAt = null,
            PlanId = request.CreateNewWorkspace ? request.PlanId : null,
            DisplayName = request.CreateNewWorkspace ? request.DisplayName?.Trim() : null,
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
            // One lookup per send. Null for a brand-new-workspace invite (invite.OwnerId is null) and
            // for a still-placeholder-named workspace — either way the subject/body fall back to the
            // pre-existing, workspace-agnostic wording.
            var workspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(
                _unitOfWork,
                invite.OwnerId
            );
            var subject =
                workspaceName != null
                    ? $"You're invited to {workspaceName} on {brand.ProductName}"
                    : $"You're invited to {brand.ProductName}";
            try
            {
                emailSent = await _emailService.SendAsync(
                    emailNormalized,
                    subject,
                    EmailTemplateBuilder.WorkspaceInvite(
                        url,
                        role?.Name,
                        brand.ProductName,
                        invite.ExpiresAt,
                        invite.OwnerId is null,
                        workspaceName,
                        brand.PrimaryColor,
                        brand.Urls.App.TrimEnd('/')
                    )
                );
            }
            catch
            { /* logged inside the sender; ignore here */
            }
        }

        var createAfter = new Dictionary<string, string>
        {
            ["max_uses"] = invite.MaxUses?.ToString() ?? string.Empty,
            ["expires_at"] = invite.ExpiresAt.ToString("O"),
            ["kind"] = "admin",
        };
        if (invite.RoleId is int inviteRoleId)
            createAfter["role_id"] = inviteRoleId.ToString();
        if (emailNormalized != null)
            createAfter["email_hash"] = PseudonymHasher.EmailHash(emailNormalized);
        if (writeAudit)
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.InviteCreated, AuditTargets.Invite, invite.Id.ToString(), owner, After: createAfter)
            );
        }

        var response = MapToResponse(invite, role?.Name, url);
        response.EmailSent = emailSent;
        return Result<InviteResponse>.Success(response, MessageKeys.Invite.Created);
    }

    public async Task<Result<List<InviteResponse>>> ListAsync()
    {
        var now = DateTime.UtcNow;

        // Query filter scopes OwnerId to the tenant (JWT). Active = not deleted / revoked / expired /
        // used up. Without the usage check, a quick-access invite (immediately marked Uses==MaxUses
        // at creation — see CreateQuickAccessInviteAsync) would show up here forever even though the
        // real, already-approved user it created is already visible in the Users list — the exact
        // same row appearing twice, once as a real user and once as a stale "pending" invite.
        var rows = await _unitOfWork
            .Repository<Invite>()
            .Query()
            .AsNoTracking()
            .Where(i =>
                i.DeletedAt == null
                && i.RevokedAt == null
                && i.ExpiresAt > now
                && (i.MaxUses == null || i.Uses < i.MaxUses)
            )
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        // Resolve role names for any pinned roles (single round-trip). Own-plus-global filter is fine
        // for reads here — we only surface the name of a role the admin already pinned.
        var roleIds = rows.Where(i => i.RoleId != null)
            .Select(i => i.RoleId!.Value)
            .Distinct()
            .ToList();
        var roleNames =
            roleIds.Count == 0
                ? new Dictionary<int, string>()
                : await _unitOfWork
                    .Repository<Role>()
                    .Query()
                    .AsNoTracking()
                    .Where(r => roleIds.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id, r => r.Name);

        var appBaseUrl = await GetAppBaseUrlAsync();
        var list = rows.Select(i =>
                MapToResponse(
                    i,
                    i.RoleId != null && roleNames.TryGetValue(i.RoleId.Value, out var n) ? n : null,
                    BuildJoinUrl(appBaseUrl, i.Code)
                )
            )
            .ToList();

        return Result<List<InviteResponse>>.Success(list);
    }

    public async Task<Result<InviteRevokeResponse>> RevokeAsync(int id, bool writeAudit = true)
    {
        var invite = await LoadOwnAsync(id);
        if (invite == null)
            return Result<InviteRevokeResponse>.NotFound(MessageKeys.Invite.NotFound);

        var now = DateTime.UtcNow;
        invite.RevokedAt = now;
        _unitOfWork.Repository<Invite>().Update(invite);

        // The magic link, not the audit row, is what a leaked quick-access invite hands an
        // attacker: LoginWithInviteAsync resolves the QuickAccessLink by token hash and checks only
        // that link's RevokedAt — it never reads the Invite. Stamping the invite alone therefore
        // left a revoked invite's link signing people in indefinitely.
        await RevokeLinksForInviteAsync(invite.Id, now);

        await _unitOfWork.SaveChangesAsync();

        // DB-11c §3.6: this invite's own live memberships — empty when it was never accepted.
        // Memberships created before DB-11a have invite_id == NULL except quick-access ones, so an
        // older invite returns an empty list; that is expected. Not scoped by owner: a super-admin
        // "new workspace" invite's OwnerId is null, but its accept mints a brand-new workspace, so
        // searching by InviteId directly is the only correct lookup either way.
        var invitees = await _unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Include(m => m.User)
            .Include(m => m.Role)
            .Where(m => m.InviteId == invite.Id && m.LeftAt == null)
            .Select(m => new InviteeMembership
            {
                UserId = m.User.Id,
                PublicId = m.User.PublicId,
                Email = m.User.Email,
                DisplayName = m.User.DisplayName,
                RoleName = m.Role.Name,
                IsActive = m.IsActive,
            })
            .ToListAsync();

        if (writeAudit)
        {
            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.InviteRevoked,
                    AuditTargets.Invite,
                    invite.Id.ToString(),
                    invite.OwnerId,
                    After: new Dictionary<string, string> { ["count"] = invitees.Count.ToString() }
                )
            );
        }

        return Result<InviteRevokeResponse>.Success(
            new InviteRevokeResponse { InviteId = invite.Id, Invitees = invitees },
            MessageKeys.Invite.Revoked_Ok
        );
    }

    public async Task<Result<InviteResponse>> RotateQuickLinkAsync(int id)
    {
        var invite = await LoadOwnAsync(id);
        if (invite == null)
            return Result<InviteResponse>.NotFound(MessageKeys.Invite.NotFound);

        if (invite.RevokedAt is not null)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.Revoked);

        // Rotation replaces a credential; it cannot resurrect an expired invite into a working one.
        if (invite.ExpiresAt <= DateTime.UtcNow)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.Expired);

        // The link carries the user and project, so an invite that never issued one has nothing to
        // rotate — that is a normal invite, and saying so beats minting a link with no account.
        var current = await _unitOfWork
            .Repository<QuickAccessLink>()
            .Query()
            .IgnoreQueryFilters()
            .Where(l => l.InviteId == invite.Id && l.DeletedAt == null)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

        if (current == null)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.NotQuickAccess);

        var project = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == current.ProjectId && p.DeletedAt == null);

        if (project == null || string.IsNullOrWhiteSpace(project.AppUrl))
            return Result<InviteResponse>.Failure(MessageKeys.Invite.QuickAccessAppUrlRequired);

        var now = DateTime.UtcNow;
        var ttlDays = QuickAccessLinkTtlDays;
        var rawToken = QuickAccessTokenGenerator.NewToken();

        // Revoke every live link for this invite BEFORE adding the replacement, so there is never a
        // moment where two tokens both work — and so a second rotate cannot leave the first
        // rotation's token behind.
        await RevokeLinksForInviteAsync(invite.Id, now);

        await _unitOfWork
            .Repository<QuickAccessLink>()
            .AddAsync(
                new QuickAccessLink
                {
                    OwnerId = current.OwnerId,
                    UserId = current.UserId,
                    ProjectId = current.ProjectId,
                    InviteId = invite.Id,
                    TokenHash = QuickAccessTokenGenerator.Hash(rawToken),
                    ExpiresAt = now.AddDays(ttlDays),
                    // Same as issue: 0 = unlimited within the TTL, or the client could not come back after
                    // their 12h JWT expires.
                    MaxUses = 0,
                }
            );
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.InviteQuickLinkRotated,
                AuditTargets.Invite,
                invite.Id.ToString(),
                invite.OwnerId
            )
        );

        var role = invite.RoleId is int roleId
            ? await _unitOfWork
                .Repository<Role>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roleId)
            : null;

        var magicLink = QuickAccessTokenGenerator.BuildMagicLink(project.AppUrl!, rawToken);
        var response = MapToResponse(invite, role?.Name, magicLink);
        response.MagicLink = magicLink;
        response.LinkExpiresAt = now.AddDays(ttlDays);
        return Result<InviteResponse>.Success(response, MessageKeys.Invite.LinkRotated);
    }

    /// <summary>
    /// Stamps RevokedAt on every not-yet-revoked link for an invite. Does NOT save — the caller
    /// commits, so revoking the invite and killing its link land in one transaction.
    /// </summary>
    private async Task RevokeLinksForInviteAsync(int inviteId, DateTime now)
    {
        var links = await _unitOfWork
            .Repository<QuickAccessLink>()
            .Query()
            .IgnoreQueryFilters()
            .Where(l => l.InviteId == inviteId && l.DeletedAt == null && l.RevokedAt == null)
            .ToListAsync();

        foreach (var link in links)
        {
            link.RevokedAt = now;
            _unitOfWork.Repository<QuickAccessLink>().Update(link);
        }
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
            return await _unitOfWork
                .Repository<Invite>()
                .Query()
                .IgnoreQueryFilters()
                .Where(i => i.Id == id && i.DeletedAt == null)
                .FirstOrDefaultAsync();
        }

        if (!TenantStamp.TryRequireOwner(_currentUser, out var owner))
            return null;

        return await _unitOfWork
            .Repository<Invite>()
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
            return Result<InvitePreviewResponse>.Success(
                new InvitePreviewResponse
                {
                    IsNewWorkspace = true,
                    EmailLocked = invite.Email != null,
                }
            );
        }

        // SAFE preview only: the workspace's own name + the pinned role's name. NEVER the tenant
        // GUID, the invite id, or any secret. Anonymous path → bypass filters and scope explicitly
        // to the invite's own OwnerId.
        var workspaceName = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == invite.OwnerId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync();

        string? roleName = null;
        if (invite.RoleId is int rid)
        {
            roleName = await _unitOfWork
                .Repository<Role>()
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
        return Result<InvitePreviewResponse>.Success(
            new InvitePreviewResponse
            {
                WorkspaceName = workspaceName ?? Workspace.PlaceholderName,
                RoleName = roleName,
                EmailLocked = invite.Email != null,
            }
        );
    }

    public async Task<Result<LoginResponse>> AcceptAsync(AcceptInviteRequest request)
    {
        // M2: guard nulls before any .Trim()/.Hash() so null fields return 400 not 500.
        if (string.IsNullOrWhiteSpace(request.Code))
            return Result<LoginResponse>.Failure(MessageKeys.Invite.NotFound);
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result<LoginResponse>.Failure(MessageKeys.User.EmailRequired);
        // DB-14 §3.6: replaces the old short-password check.
        if (PasswordPolicy.Validate(request.Password, request.Email) is string pwErr)
            return Result<LoginResponse>.Failure(pwErr);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Result<LoginResponse>.Failure(MessageKeys.User.DisplayNameRequired);

        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // 1. Resolve the invite (anonymous path → IgnoreQueryFilters, like RegisterAsync). Reject
        //    anything not currently acceptable. The invite is the authorization — validate fully.
        var invite = await ResolveValidInviteAsync(request.Code);
        if (invite == null)
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // Email lock: if set, only that email may accept.
        if (invite.Email != null && invite.Email != emailNormalized)
            return Result<LoginResponse>.Failure(MessageKeys.Invite.EmailMismatch);

        return invite.OwnerId is Guid existingOwnerId
            ? await AcceptJoinExistingWorkspaceAsync(
                invite,
                existingOwnerId,
                emailNormalized,
                request
            )
            : await AcceptCreateNewWorkspaceAsync(invite, emailNormalized, request);
    }

    // Joins an EXISTING tenant — the original accept flow (invite.OwnerId non-null).
    private async Task<Result<LoginResponse>> AcceptJoinExistingWorkspaceAsync(
        Invite invite,
        Guid ownerId,
        string emailNormalized,
        AcceptInviteRequest request
    )
    {
        // DB-17 review finding #4 (Opus/Gemini): a demo admin can mint an addressed invite into
        // their own workspace — accepting it must not mint a full token into a workspace whose TTL
        // has already passed (the sweep may hard-delete it within the next 15 minutes regardless of
        // what this invite just created).
        var demoExpired = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w =>
                w.Id == ownerId && w.DemoExpiresAt != null && w.DemoExpiresAt < DateTime.UtcNow
            );
        if (demoExpired)
            return Result<LoginResponse>.Failure(MessageKeys.Demo.DemoExpired);

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

        // DB-11a join-or-create (§3.3): one identity per e-mail; a membership per workspace.
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        var isNewIdentity = identity == null;
        var existingIdentityJustVerified = false;

        if (identity != null)
        {
            // Step 3: anonymous caller with an existing identity must present its password (D4) —
            // same message as a brand-new conflict, revealing nothing new.
            if (
                identity.PasswordlessOnly
                || !_passwordHasher.Verify(request.Password, identity.PasswordHash)
            )
                return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

            // Step 4: a live membership already in THIS workspace → conflict (M1: scoped to this
            // invite's tenant only — a same-email identity already active elsewhere is not a
            // conflict here).
            var already = await _memberships.GetMembershipAsync(identity.Id, ownerId);
            if (already != null)
                return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

            // DB-14 §3.2 row 12 (agy #2): an addressed invite proves possession of THIS identity's
            // own address exactly as it would for a new one — never gate a person who just proved
            // it twice (password + addressed link).
            if (
                invite.Email != null
                && EmailNormalizer.Normalize(invite.Email) == identity.Email
                && identity.EmailVerifiedAt == null
            )
            {
                identity.EmailVerifiedAt = DateTime.UtcNow;
                _unitOfWork.Repository<User>().Update(identity);
                existingIdentityJustVerified = true;
                await NotifyDemoEmailVerifiedAsync(identity);
            }
        }

        // MaxSeats: count LIVE memberships of the invite's tenant. Checked BEFORE claiming a slot so
        // an over-limit accept never consumes a use.
        var seatCount = await _memberships.InWorkspace(ownerId).CountAsync(m => m.LeftAt == null);
        var seatCheck = await _entitlements.CheckCountAsync(
            ownerId,
            EntitlementCatalog.MaxSeats,
            seatCount
        );
        if (!seatCheck.IsSuccess)
            return Result<LoginResponse>.LimitReached(
                seatCheck.Message ?? MessageKeys.Plan.LimitReached,
                seatCheck.Limit!
            );

        // 4. H1: atomically claim a usage slot BEFORE creating anything. The UnitOfWork issues a
        //    single UPDATE … WHERE (not deleted/revoked/expired AND uses < maxUses) … SET uses+=1
        //    returning rows-affected. Two concurrent requests both seeing Uses=0/MaxUses=1 cannot
        //    both succeed — only one gets claimed=1; the other gets claimed=0 and is rejected
        //    without ever creating a user. This replaces the old read-check-then-increment pattern.
        var claimed = await _unitOfWork.AtomicClaimInviteSlotAsync(invite.Id, DateTime.UtcNow);

        if (claimed == 0)
            // Exhausted or revoked concurrently — do NOT create anything.
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // 5. Create the identity pre-authorized + pre-scoped to the invite's tenant. The invite is
        //    the authorization, so we SKIP the pending approval queue: Approved + active immediately.
        //    DisplayName from the request only for a NEW identity (an existing identity's own
        //    DisplayName is never overwritten by a join).
        if (isNewIdentity)
        {
            // The super admin may have named the workspace when inviting; the invitee can override it.
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? (invite.DisplayName ?? request.DisplayName ?? string.Empty)
                : request.DisplayName;
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(request.Password),
                displayName,
                role,
                ownerId
            );
            // DB-14 §3.2 row 3/5: an addressed invite (matched at the top of AcceptAsync) proves
            // possession by delivery — verified at creation, no mail. An open invite proves nothing
            // about the typed address — unverified, mail sent below once persisted.
            if (invite.Email != null)
                identity.EmailVerifiedAt = DateTime.UtcNow;

            try
            {
                await _unitOfWork.Repository<User>().AddAsync(identity);
                await _unitOfWork.SaveChangesAsync();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // L2: duplicate-email insert race (two concurrent accepts with the same email both
                // pass the check above; the second violates ux_users_email_live).
                return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);
            }

            // DB-17 review finding #7: after the save (so identity.Id is valid) — a brand-new
            // identity has no other memberships yet, so this is a no-op in practice, but the site is
            // still wired for consistency with every other EmailVerifiedAt flip in this service.
            if (invite.Email != null)
                await NotifyDemoEmailVerifiedAsync(identity);

            if (invite.Email == null)
                await _emailVerification.SendAsync(identity);
        }

        var membership = await _memberships.JoinAsync(
            identity!,
            ownerId,
            role,
            ApprovalStatus.Approved,
            isActive: true,
            inviteId: invite.Id
        );
        await _unitOfWork.SaveChangesAsync();
        // Populated AFTER the save above so EF never tries to re-insert the already-existing role row.
        membership.Role = role;

        // Review finding #3: invalidate the gate's cache only now that the flip above is actually
        // persisted (a claim failure or seat-limit refusal earlier would have returned before this
        // save ever ran, so invalidating any sooner could race an unpersisted change).
        if (existingIdentityJustVerified)
            _emailVerification.InvalidateGate(identity!.PublicId);

        // 6. Auto-signin: return a login token + user (reuse the login response builder).
        var token = _tokenService.Issue(identity!, membership);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.InviteAccepted,
                AuditTargets.Invite,
                invite.Id.ToString(),
                ownerId,
                After: new Dictionary<string, string> { ["role_id"] = role.Id.ToString(), ["kind"] = "join" },
                ActorUserIdOverride: identity!.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        // DB-17 review finding #11 (NIT): pass the workspace so MeResponse.DemoExpiresAt/DemoCanExtend
        // are populated (this is an EXISTING workspace being joined — it may itself be a demo, e.g.
        // via a quick-access/addressed invite the demo admin minted).
        var currentWorkspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == ownerId);

        return Result<LoginResponse>.Success(
            new LoginResponse
            {
                Status = "ok",
                Token = token,
                User = UserMapper.ToMeResponse(identity!, membership.Role, workspace: currentWorkspace),
            }
        );
    }

    // Mints a brand-new self-owned tenant (invite.OwnerId is null) — the invitee becomes its
    // "Workspace Admin", approved and active immediately. Mirrors TenantService.CreateAsync's
    // direct-create path (including the OwnerId==u.PublicId duplicate-email scoping and the
    // absence of a seat-limit check — there is no existing tenant to check limits against yet).
    private async Task<Result<LoginResponse>> AcceptCreateNewWorkspaceAsync(
        Invite invite,
        string emailNormalized,
        AcceptInviteRequest request
    )
    {
        // DB-11a (D13): one identity may administer several workspaces — join-or-create the
        // identity rather than refusing outright.
        var workspaceAdminRole = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.DeletedAt == null
                && r.IsActive
                && r.Name == WorkspaceAdminRoleName
                && r.OwnerId == null
            );
        if (workspaceAdminRole == null)
            return Result<LoginResponse>.Failure(MessageKeys.Role.Invalid);

        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        var isNewIdentity = identity == null;
        var existingIdentityJustVerified = false;

        if (identity != null)
        {
            // D4/D5: the existing account's password must verify.
            if (
                identity.PasswordlessOnly
                || !_passwordHasher.Verify(request.Password, identity.PasswordHash)
            )
                return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);

            // DB-14 §3.2 row 12 (agy #2) — see AcceptJoinExistingWorkspaceAsync's twin comment.
            if (
                invite.Email != null
                && EmailNormalizer.Normalize(invite.Email) == identity.Email
                && identity.EmailVerifiedAt == null
            )
            {
                identity.EmailVerifiedAt = DateTime.UtcNow;
                _unitOfWork.Repository<User>().Update(identity);
                existingIdentityJustVerified = true;
                await NotifyDemoEmailVerifiedAsync(identity);
            }
        }

        var claimed = await _unitOfWork.AtomicClaimInviteSlotAsync(invite.Id, DateTime.UtcNow);
        if (claimed == 0)
            return Result<LoginResponse>.NotFound(MessageKeys.Invite.NotFound);

        // workspaces.id no longer needs to equal anyone's public_id — a fresh id every time.
        var workspaceId = Guid.NewGuid();
        var isNewIdentityJustVerified = false;

        Workspace newWorkspace;
        try
        {
            if (isNewIdentity)
            {
                identity = _memberships.NewIdentity(
                    emailNormalized,
                    _passwordHasher.Hash(request.Password),
                    request.DisplayName,
                    workspaceAdminRole,
                    workspaceId
                );
                // DB-14 §3.2 — see AcceptJoinExistingWorkspaceAsync's twin comment.
                if (invite.Email != null)
                {
                    identity.EmailVerifiedAt = DateTime.UtcNow;
                    isNewIdentityJustVerified = true;
                }
                await _unitOfWork.Repository<User>().AddAsync(identity);
            }

            // A brand-new tenant: the workspace's own name (Q3) comes from what the super admin
            // typed on the invite, never from the invitee's DisplayName.
            var trimmedInviteName = invite.DisplayName?.Trim();
            var workspaceName = string.IsNullOrWhiteSpace(trimmedInviteName)
                ? Workspace.PlaceholderName
                : trimmedInviteName[..Math.Min(120, trimmedInviteName.Length)];
            newWorkspace = new Workspace
            {
                Id = workspaceId,
                Name = workspaceName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            };
            await _unitOfWork.Workspaces.AddAsync(newWorkspace);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists);
        }

        // Review finding #3: invalidate the gate's cache only now that the flip above is actually
        // persisted (the save just above is the first one that could have persisted it).
        if (existingIdentityJustVerified)
            _emailVerification.InvalidateGate(identity!.PublicId);

        // DB-17 review finding #7: after the save (so identity.Id is valid) — a brand-new identity
        // has no other memberships yet, so this is a no-op in practice, but the site is still wired
        // for consistency with every other EmailVerifiedAt flip in this service.
        if (isNewIdentityJustVerified)
            await NotifyDemoEmailVerifiedAsync(identity!);

        if (isNewIdentity && invite.Email == null)
            await _emailVerification.SendAsync(identity!);

        var membership = await _memberships.JoinAsync(
            identity!,
            workspaceId,
            workspaceAdminRole,
            ApprovalStatus.Approved,
            isActive: true,
            inviteId: invite.Id
        );
        await _unitOfWork.SaveChangesAsync();

        // Apply the invited plan. Written inline rather than through TenantService.ChangePlanAsync:
        // TenantService already composes IInviteService, so calling back would be a DI cycle.
        //
        // Status = Active, unlike self-serve signup (AuthService.RegisterAdminAsync:414-419) which
        // parks a paid plan in PendingActivation. That asymmetry is deliberate: anyone can sign up,
        // so signup needs a second pair of eyes; a super admin issuing this invitation IS that
        // approval, and waiting on the person who just invited the workspace would be circular.
        // Consequence, accepted knowingly: an invited paid plan goes Active with no payment taken.
        // That is right for comped/sales-led/migrated workspaces, and inert while the billing
        // provider is Noop. When real billing lands, invited paid workspaces need a comped marker.
        // No IBillingProvider call here — this service has no billing dependency and acceptance is
        // an anonymous request.
        if (invite.PlanId is int invitedPlanId)
        {
            var plan = await _unitOfWork
                .Repository<Plan>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.Id == invitedPlanId
                    && p.DeletedAt == null
                    && p.IsActive
                    && p.DisplayState != PlanDisplayState.Hidden
                );

            // Free is the zero-write default (a missing subscription already means Free).
            if (plan != null && plan.Slug != "free")
            {
                await _unitOfWork
                    .Repository<Subscription>()
                    .AddAsync(
                        new Subscription
                        {
                            // Set explicitly: accept runs with no tenant context, so TenantStamp would
                            // produce null and violate this entity's non-null OwnerId.
                            OwnerId = workspaceId,
                            PlanId = plan.Id,
                            Status = SubscriptionStatus.Active,
                        }
                    );
                await _unitOfWork.SaveChangesAsync();
            }
        }

        // Populated AFTER every SaveChangesAsync in this method has run, so EF never tries to
        // re-insert the already-existing role row.
        membership.Role = workspaceAdminRole;
        var token = _tokenService.Issue(identity!, membership);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.InviteAccepted,
                AuditTargets.Invite,
                invite.Id.ToString(),
                workspaceId,
                After: new Dictionary<string, string>
                {
                    ["role_id"] = workspaceAdminRole.Id.ToString(),
                    ["kind"] = "new_workspace",
                },
                ActorUserIdOverride: identity!.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceCreated,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "invite" },
                ActorUserIdOverride: identity!.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        return Result<LoginResponse>.Success(
            new LoginResponse
            {
                Status = "ok",
                Token = token,
                // DB-17 review finding #11 (NIT): the workspace was just minted above (a plain
                // brand-new workspace, never a demo) — pass it so MeResponse's demo fields resolve
                // to their correct all-null values instead of the ToMeResponse default.
                User = UserMapper.ToMeResponse(identity!, membership.Role, workspace: newWorkspace),
            }
        );
    }

    // Eagerly provisions a User for a Role.QuickAccess invite (e.g. "Client") instead of deferring
    // creation to a click-through accept step: the invitee gets emailed a direct link to the
    // project's AppUrl plus a generated password, and logs into the widget's existing login UI
    // as-is — no accept page, no signup form.
    private async Task<Result<InviteResponse>> CreateQuickAccessInviteAsync(
        Guid ownerId,
        Role role,
        CreateInviteRequest request
    )
    {
        var emailNormalized = EmailNormalizer.Normalize(request.Email);
        if (emailNormalized == null)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.QuickAccessEmailRequired);

        if (request.ProjectId is not int projectId)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.QuickAccessProjectRequired);

        var project = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Id == projectId && p.DeletedAt == null && p.OwnerId == ownerId
            );
        if (project == null)
            return Result<InviteResponse>.Failure(MessageKeys.Project.NotFound);
        if (string.IsNullOrWhiteSpace(project.AppUrl))
            return Result<InviteResponse>.Failure(MessageKeys.Invite.QuickAccessAppUrlRequired);

        // DB-11a join-or-create: an identity that already exists just gets a Client membership in
        // this workspace (its password, if any, is untouched); a live membership already here is a
        // conflict (mirrors AcceptJoinExistingWorkspaceAsync).
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        var isNewIdentity = identity == null;

        if (identity != null)
        {
            var alreadyMember = await _memberships.GetMembershipAsync(identity.Id, ownerId);
            if (alreadyMember != null)
                return Result<InviteResponse>.Conflict(MessageKeys.Auth.AccountExists);
        }

        var seatCount = await _memberships.InWorkspace(ownerId).CountAsync(m => m.LeftAt == null);
        var seatCheck = await _entitlements.CheckCountAsync(
            ownerId,
            EntitlementCatalog.MaxSeats,
            seatCount
        );
        if (!seatCheck.IsSuccess)
            return Result<InviteResponse>.LimitReached(
                seatCheck.Message ?? MessageKeys.Plan.LimitReached,
                seatCheck.Limit!
            );

        var ttlDays = request.ExpiresInDays is int d && d > 0 ? d : QuickAccessLinkTtlDays;

        if (isNewIdentity)
        {
            // Deliberately UNUSABLE. The account has no password anyone knows, types, or receives —
            // a random hash input that is never revealed, plus PasswordlessOnly so LoginAsync refuses
            // the account explicitly rather than relying on the hash never matching.
            //
            // This replaces emailing a generated password in plaintext (CWE-319).
            var unusableSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(unusableSecret),
                emailNormalized.Split('@')[0],
                role,
                ownerId,
                passwordlessOnly: true
            );
            // DB-14 §3.2: the magic link is e-mailed to this exact address — verified at creation,
            // no mail (a Client never acts as an admin anyway, and PasswordlessOnly already exempts
            // it from the resend/verify surface).
            identity.EmailVerifiedAt = DateTime.UtcNow;
        }

        // Already "used": there is no accept step left to consume — the Invite row exists purely for
        // the admin's own audit/history view (who was invited, when, to which project).
        var invite = new Invite
        {
            OwnerId = ownerId,
            Code = GenerateCode(),
            RoleId = role.Id,
            Email = emailNormalized,
            ProjectId = projectId,
            ExpiresAt = DateTime.UtcNow.AddDays(ttlDays),
            MaxUses = 1,
            Uses = 1,
            RevokedAt = null,
        };

        var rawToken = QuickAccessTokenGenerator.NewToken();
        WorkspaceMembership membership;

        try
        {
            if (isNewIdentity)
                await _unitOfWork.Repository<User>().AddAsync(identity!);
            await _unitOfWork.Repository<Invite>().AddAsync(invite);
            await _unitOfWork.SaveChangesAsync();

            // DB-17 review finding #7: after the save (so identity.Id is valid) — a brand-new
            // identity has no other memberships yet, so this is a no-op in practice, but the site is
            // still wired for consistency with every other EmailVerifiedAt flip in this service.
            if (isNewIdentity)
                await NotifyDemoEmailVerifiedAsync(identity!);

            membership = await _memberships.JoinAsync(
                identity!,
                ownerId,
                role,
                ApprovalStatus.Approved,
                isActive: true,
                inviteId: invite.Id
            );
            await _unitOfWork.SaveChangesAsync();

            await _unitOfWork
                .Repository<QuickAccessLink>()
                .AddAsync(
                    new QuickAccessLink
                    {
                        OwnerId = ownerId,
                        UserId = identity!.PublicId,
                        ProjectId = projectId,
                        InviteId = invite.Id,
                        TokenHash = QuickAccessTokenGenerator.Hash(rawToken),
                        ExpiresAt = DateTime.UtcNow.AddDays(ttlDays),
                        // 0 = unlimited within the TTL. Single-use would break the silent re-sign-in after
                        // the 12h JWT expires, which is the entire point of the link.
                        MaxUses = 0,
                    }
                );
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Duplicate-email insert race (mirrors AcceptJoinExistingWorkspaceAsync).
            return Result<InviteResponse>.Conflict(MessageKeys.Auth.AccountExists);
        }

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var extensionStoreUrl = await _settings.GetStringAsync(
            ISettingsService.ExtensionStoreUrl,
            string.Empty
        );
        var magicLink = QuickAccessTokenGenerator.BuildMagicLink(project.AppUrl!, rawToken);

        // Delivery is link-copy by default: the admin pastes the link wherever they already talk to
        // the client. Email is opt-in, and when it is on it carries the LINK — never a password.
        var emailSent = false;
        if (await _settings.GetBoolAsync(ISettingsService.QuickAccessInviteEmailEnabled, false))
        {
            var workspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(
                _unitOfWork,
                ownerId
            );
            try
            {
                emailSent = await _emailService.SendAsync(
                    emailNormalized,
                    $"You're invited to review {project.Name}",
                    EmailTemplateBuilder.QuickAccessInvite(
                        magicLink,
                        emailNormalized,
                        brand.ProductName,
                        project.Name!,
                        extensionStoreUrl,
                        workspaceName,
                        brand.PrimaryColor,
                        brand.Urls.App.TrimEnd('/')
                    )
                );
            }
            catch
            { /* logged inside the sender; ignore here */
            }
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.InviteCreated,
                AuditTargets.Invite,
                invite.Id.ToString(),
                ownerId,
                After: new Dictionary<string, string>
                {
                    ["role_id"] = role.Id.ToString(),
                    ["max_uses"] = invite.MaxUses?.ToString() ?? string.Empty,
                    ["expires_at"] = invite.ExpiresAt.ToString("O"),
                    ["kind"] = "quick_access",
                    ["email_hash"] = PseudonymHasher.EmailHash(emailNormalized),
                }
            )
        );

        var response = MapToResponse(invite, role.Name, magicLink);
        response.EmailSent = emailSent;
        response.MagicLink = magicLink;
        response.LinkExpiresAt = DateTime.UtcNow.AddDays(ttlDays);
        return Result<InviteResponse>.Success(response, MessageKeys.Invite.Created);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Resolves an invite by code that is currently acceptable: exists, not deleted, not revoked,
    // not expired, and uses remaining. Anonymous path → IgnoreQueryFilters (no tenant claim yet).
    private async Task<Invite?> ResolveValidInviteAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var trimmed = code.Trim();
        var now = DateTime.UtcNow;

        var invite = await _unitOfWork
            .Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i =>
                i.Code == trimmed && i.DeletedAt == null && i.RevokedAt == null && i.ExpiresAt > now
            )
            .FirstOrDefaultAsync();

        if (invite == null)
            return null;

        // Usage cap (evaluated in-memory: null MaxUses = unlimited within TTL).
        if (invite.MaxUses is int max && invite.Uses >= max)
            return null;

        return invite;
    }

    // Validates that roleId is a valid, active, NON-admin role of the given tenant owner or a
    // global (null-owner) role. Mirrors RegisterAsync.cs role resolution. Anonymous/cross-tenant
    // safe: scoped explicitly to the invite's owner (or null-owner globals), never the caller's.
    // Used for: an anonymous acceptor's FREE choice of role on an unpinned invite — must never
    // include Deputy, or anyone could self-escalate to admin-tier by guessing/enumerating its id.
    private async Task<Role?> ResolveAssignableRoleAsync(int roleId, Guid ownerId)
    {
        return await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.Id == roleId
                && r.DeletedAt == null
                && r.IsActive
                && !r.GrantsAdmin
                && !r.IsSuperAdmin
                && (r.OwnerId == ownerId || r.OwnerId == null)
            );
    }

    // Same as ResolveAssignableRoleAsync but also allows "Workspace Admin Deputy". Used only where
    // the role was chosen by an authenticated admin-tier caller, never by an anonymous acceptor:
    // (a) an admin PINNING a role on CreateAsync, (b) accept-time resolution of an already-pinned
    // invite.RoleId (the admin made that choice at creation time, not the anonymous acceptor now).
    private async Task<Role?> ResolvePinnableRoleAsync(int roleId, Guid ownerId)
    {
        return await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.Id == roleId
                && r.DeletedAt == null
                && r.IsActive
                && (!r.GrantsAdmin && !r.IsSuperAdmin || r.Name == DeputyRoleName)
                && (r.OwnerId == ownerId || r.OwnerId == null)
            );
    }

    // 128-bit crypto-random, URL-safe (base64url, no padding) — same encoding style as
    // ResetTokenService / UploadSigner. Not signed: it is a DB row we look up.
    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Where the invitee's join link points. Resolution order, first non-empty wins:
    //   1. app_base_url  — an explicit override, for the rare install whose /join page is not on
    //      the same origin as the dashboard. Editable at PUT /api/admin/settings.
    //   2. brand_url_app — the dashboard URL every white-labelled install already configures
    //      (PUT /api/admin/branding -> urls.app). Without this step a self-hosted instance mailed
    //      join links pointing at the SaaS host, where the code does not exist: the invite was
    //      simply dead. app_base_url had no writer at all before this, so the fallback below was
    //      the only reachable value.
    //   3. the compiled default (this project's own SaaS dashboard).
    private async Task<string> GetAppBaseUrlAsync()
    {
        var configured = await _settings.GetStringAsync(ISettingsService.AppBaseUrl);
        if (string.IsNullOrWhiteSpace(configured))
            configured = await _settings.GetStringAsync(ISettingsService.BrandUrlApp);

        return string.IsNullOrWhiteSpace(configured) ? DefaultAppBaseUrl : configured.TrimEnd('/');
    }

    private async Task<string> BuildJoinUrlAsync(string code) =>
        BuildJoinUrl(await GetAppBaseUrlAsync(), code);

    private static string BuildJoinUrl(string appBaseUrl, string code) =>
        $"{appBaseUrl}/join?code={Uri.EscapeDataString(code)}";

    private static InviteResponse MapToResponse(Invite i, string? roleName, string url) =>
        new()
        {
            Id = i.Id,
            Code = i.Code,
            Url = url,
            RoleId = i.RoleId,
            RoleName = roleName,
            Email = i.Email,
            ExpiresAt = i.ExpiresAt,
            MaxUses = i.MaxUses,
            Uses = i.Uses,
            ProjectId = i.ProjectId,
        };

    public async Task<Result<InviteResponse>> ResendAsync(int id, bool rotate = false, bool writeAudit = true)
    {
        var invite = await LoadOwnAsync(id);
        if (invite is null)
            return Result<InviteResponse>.NotFound(MessageKeys.Invite.NotFound);

        if (invite.RevokedAt is not null)
            return Result<InviteResponse>.Failure(MessageKeys.Invite.Revoked);

        if (rotate)
        {
            // A new code makes the old link dead immediately — that is the point of rotating.
            invite.Code = GenerateCode();
            invite.Uses = 0;
        }

        // Extend from now, using the invite's original lifetime where it can be recovered, so a
        // resend does not quietly shorten a 30-day link to the 7-day default.
        var originalTtl = invite.ExpiresAt - invite.CreatedAt;
        var ttlDays =
            originalTtl.TotalDays >= 1 ? (int)Math.Round(originalTtl.TotalDays) : DefaultTtlDays;
        if (invite.OwnerId is null)
            ttlDays = Math.Clamp(ttlDays, 1, 30);

        invite.ExpiresAt = DateTime.UtcNow.AddDays(ttlDays);

        _unitOfWork.Repository<Invite>().Update(invite);
        await _unitOfWork.SaveChangesAsync();

        var url = await BuildJoinUrlAsync(invite.Code);
        var emailSent = false;

        if (!string.IsNullOrWhiteSpace(invite.Email))
        {
            var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
            string? roleName = null;
            if (invite.RoleId is int roleId)
            {
                roleName = await _unitOfWork
                    .Repository<Role>()
                    .Query()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(r => r.Id == roleId)
                    .Select(r => r.Name)
                    .FirstOrDefaultAsync();
            }

            var workspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(
                _unitOfWork,
                invite.OwnerId
            );
            var subject =
                workspaceName != null
                    ? $"You're invited to {workspaceName} on {brand.ProductName}"
                    : $"You're invited to {brand.ProductName}";

            try
            {
                emailSent = await _emailService.SendAsync(
                    invite.Email!,
                    subject,
                    EmailTemplateBuilder.WorkspaceInvite(
                        url,
                        roleName,
                        brand.ProductName,
                        invite.ExpiresAt,
                        invite.OwnerId is null,
                        workspaceName,
                        brand.PrimaryColor,
                        brand.Urls.App.TrimEnd('/')
                    )
                );
            }
            catch
            { /* logged inside the sender; a failed send still returns the copyable link */
            }
        }

        if (writeAudit)
        {
            await _audit.WriteAsync(
                new AuditEntry(AuditActions.InviteResent, AuditTargets.Invite, invite.Id.ToString(), invite.OwnerId)
            );
        }

        var response = MapToResponse(invite, null, url);
        response.EmailSent = emailSent;
        return Result<InviteResponse>.Success(response);
    }
}
