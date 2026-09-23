using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class UserService : IUserService
{
    // "Workspace Admin" / "Workspace Admin Deputy" are global system roles (Role.OwnerId == null),
    // identified by name like the existing "Workspace Admin" precedent (see CreateAsync's original
    // ownership comment) — Role has no dedicated flag distinguishing "the one canonical admin" from
    // "a deputy" beyond the literal name.
    private const string WorkspaceAdminRoleName = "Workspace Admin";
    private const string DeputyRoleName = "Workspace Admin Deputy";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUser _currentUser;
    private readonly IEmailService _emailService;
    private readonly IEntitlementService _entitlements;
    private readonly IBrandingService _branding;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;

    public UserService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ICurrentUser currentUser,
        IEmailService emailService,
        IEntitlementService entitlements,
        IBrandingService branding,
        IMembershipService memberships,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
        _emailService = emailService;
        _entitlements = entitlements;
        _branding = branding;
        _memberships = memberships;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    // Best-effort notification: a send failure must never fail the admin action.
    private async Task SafeSendAsync(string to, string subject, string html)
    {
        try { await _emailService.SendAsync(to, subject, html); }
        catch { /* logged inside the sender; ignore here */ }
    }

    public async Task<Result<UserResponse>> CreateAsync(CreateUserRequest request)
    {
        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        Role role;
        Guid ownerId;

        if (_currentUser.IsSuperAdmin)
        {
            // Super admins are platform-management only (ProjectService.CreateAsync/
            // CommentService.CreateAsync already forbid them owning tenant-scoped resources) — the
            // only thing this endpoint lets them do is delegate a Deputy to an EXISTING workspace
            // they explicitly pick. `request.RoleId` is ignored entirely: creating a brand-new
            // workspace (with its own primary "Workspace Admin") stays exclusively on
            // TenantService.CreateAsync / the Tenants page, never duplicated here.
            if (request.TargetOwnerId is not Guid targetOwnerId)
                return Result<UserResponse>.Failure(MessageKeys.User.TargetWorkspaceRequired);
            if (await _memberships.CurrentAdminAsync(targetOwnerId) == null)
                return Result<UserResponse>.Failure(MessageKeys.User.WorkspaceNotFound);

            var deputyRole = await _unitOfWork.Repository<Role>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Name == DeputyRoleName && r.DeletedAt == null && r.IsActive);
            if (deputyRole == null)
                return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

            role = deputyRole;
            ownerId = targetOwnerId;
        }
        else
        {
            var resolvedRole = await GetActiveRoleAsync(request.RoleId);
            if (resolvedRole == null)
                return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

            // Privilege-escalation guard: only a super admin may assign an admin-tier role — except
            // Deputy, which the current Workspace Admin may delegate to their own team.
            if ((resolvedRole.GrantsAdmin || resolvedRole.IsSuperAdmin) && resolvedRole.Name != DeputyRoleName)
                return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

            role = resolvedRole;
            // The new user joins the CALLER's tenant. S-14: a non-super-admin without a tenant claim
            // is Forbidden, never minted a tenant from its own id.
            if (!TenantStamp.TryRequireOwner(_currentUser, out var o))
                return Result<UserResponse>.Forbidden(MessageKeys.Common.Forbidden);
            ownerId = o;
        }

        // DB-11a join-or-create: admin-driven, so no password check — an existing identity is just
        // joined. "email taken" is scoped to THIS workspace (a live membership already here), never
        // global.
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        if (identity != null)
        {
            var already = await _memberships.GetMembershipAsync(identity.Id, ownerId);
            if (already != null)
                return Result<UserResponse>.Conflict(MessageKeys.User.AlreadyMember);
        }

        // MaxSeats: count LIVE memberships of this workspace. Grandfather-safe.
        var seatCount = await _memberships.InWorkspace(ownerId).CountAsync(m => m.LeftAt == null);
        var seatCheck = await _entitlements.CheckCountAsync(ownerId, EntitlementCatalog.MaxSeats, seatCount);
        if (!seatCheck.IsSuccess)
            return Result<UserResponse>.LimitReached(seatCheck.Message ?? MessageKeys.Plan.LimitReached, seatCheck.Limit!);

        var isNewIdentity = identity == null;
        if (isNewIdentity)
        {
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(request.Password),
                request.DisplayName,
                role,
                ownerId
            );
            await _unitOfWork.Repository<User>().AddAsync(identity);
            await _unitOfWork.SaveChangesAsync();
        }

        var membership = await _memberships.JoinAsync(
            identity!,
            ownerId,
            role,
            ApprovalStatus.Approved,
            isActive: true,
            inviteId: null
        );
        await _unitOfWork.SaveChangesAsync();
        // Populated AFTER the save above so EF never tries to re-insert the already-existing role row.
        membership.Role = role;

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.MemberCreated,
                AuditTargets.Membership,
                membership.Id.ToString(),
                ownerId,
                After: new Dictionary<string, string>
                {
                    ["role_id"] = membership.RoleId.ToString(),
                    ["is_active"] = membership.IsActive.ToString(),
                    ["approval_status"] = membership.ApprovalStatus.ToString(),
                }
            )
        );

        return Result<UserResponse>.Success(MapToResponse(membership));
    }

    public async Task<Result<List<UserResponse>>> ListAsync(ApprovalStatus? status = null)
    {
        if (!TenantStamp.TryRequireOwner(_currentUser, out var ownerId))
            return Result<List<UserResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var query = _memberships.InWorkspace(ownerId).Where(m => m.LeftAt == null);

        if (status.HasValue)
            query = query.Where(m => m.ApprovalStatus == status.Value);

        var memberships = await query.OrderBy(m => m.User.Id).ToListAsync();

        return Result<List<UserResponse>>.Success(memberships.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Resolves the identity (users.id == id, live) and, when there is a single unambiguous
    /// workspace scope for the call, its membership: the caller's own tenant for a scoped admin, or
    /// (DB-11a leaves this a narrow case) the identity's sole live membership for a super admin.
    /// </summary>
    private async Task<(User? Identity, WorkspaceMembership? Membership)> ResolveTargetAsync(int id)
    {
        var identity = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);
        if (identity == null)
            return (null, null);

        if (TenantStamp.TryRequireOwner(_currentUser, out var ownerId))
        {
            var membership = await _memberships.GetMembershipAsync(identity.Id, ownerId);
            return (identity, membership);
        }

        // Super admin, no explicit workspace on this endpoint: only unambiguous when the identity
        // has exactly one live membership.
        var live = await _memberships.ListForIdentityAsync(identity.Id);
        return (identity, live.Count == 1 ? live[0] : null);
    }

    public async Task<Result<UserResponse>> ApproveAsync(int id, ApproveUserRequest request)
    {
        var (identity, membership) = await ResolveTargetAsync(id);
        if (identity == null || membership == null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

        // Only super admin may grant an admin-tier role at approval time.
        var role = await GetActiveRoleAsync(request.RoleId);
        if (role == null)
            return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

        // Privilege-escalation guard: only a super admin may assign an admin-tier role — except
        // Deputy, which the current Workspace Admin may delegate to their own team.
        if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin) && role.Name != DeputyRoleName)
            return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

        var approveBeforeStatus = membership.ApprovalStatus;
        var approveBeforeRoleId = membership.RoleId;

        membership.ApprovalStatus = ApprovalStatus.Approved;
        membership.IsActive = true;
        membership.RoleId = role.Id;

        _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
        await _unitOfWork.SaveChangesAsync();
        membership.Role = role;

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.MemberApproved,
                AuditTargets.Membership,
                membership.Id.ToString(),
                membership.OwnerId,
                Before: new Dictionary<string, string>
                {
                    ["approval_status"] = approveBeforeStatus.ToString(),
                    ["role_id"] = approveBeforeRoleId.ToString(),
                },
                After: new Dictionary<string, string>
                {
                    ["approval_status"] = membership.ApprovalStatus.ToString(),
                    ["role_id"] = membership.RoleId.ToString(),
                }
            )
        );

        var approveBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var approveProductName = approveBrand.ProductName;
        var approveAppUrl = approveBrand.Urls.App.TrimEnd('/');
        // One lookup per send; null (missing row or still the DB-03 placeholder) falls back to the
        // pre-existing, workspace-agnostic wording.
        var approveWorkspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(_unitOfWork, membership.OwnerId);
        var approveSubject = approveWorkspaceName != null
            ? $"Your {approveProductName} account for {approveWorkspaceName} is approved"
            : $"Your {approveProductName} account is approved";
        var approveWorkspaceLine = approveWorkspaceName != null
            ? $@"<p>You now have access to the <b>{System.Net.WebUtility.HtmlEncode(approveWorkspaceName)}</b> workspace.</p>"
            : string.Empty;
        await SafeSendAsync(identity.Email, approveSubject,
            $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">You're in ✅</h2>
  <p>Your {approveProductName} account (<b>{identity.Email}</b>) has been approved and is now active.</p>
  {approveWorkspaceLine}
  <p><a href=""{approveAppUrl}"" style=""color:#2563eb"">Sign in to {approveProductName} →</a></p>
</div>");

        return Result<UserResponse>.Success(MapToResponse(membership));
    }

    public async Task<Result<UserResponse>> RejectAsync(int id)
    {
        var (identity, membership) = await ResolveTargetAsync(id);
        if (identity == null || membership == null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

        var rejectBeforeStatus = membership.ApprovalStatus;
        var rejectBeforeRoleId = membership.RoleId;

        membership.ApprovalStatus = ApprovalStatus.Rejected;
        membership.IsActive = false;
        // H1/R16: revoke this WORKSPACE's live access tokens — the identity's other memberships (and
        // any other workspace's sessions) are untouched.
        membership.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.MemberRejected,
                AuditTargets.Membership,
                membership.Id.ToString(),
                membership.OwnerId,
                Before: new Dictionary<string, string>
                {
                    ["approval_status"] = rejectBeforeStatus.ToString(),
                    ["role_id"] = rejectBeforeRoleId.ToString(),
                },
                After: new Dictionary<string, string>
                {
                    ["approval_status"] = membership.ApprovalStatus.ToString(),
                    ["role_id"] = membership.RoleId.ToString(),
                }
            )
        );

        var rejectBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var rejectProductName = rejectBrand.ProductName;
        var rejectWorkspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(_unitOfWork, membership.OwnerId);
        var rejectSubject = rejectWorkspaceName != null
            ? $"Your {rejectProductName} account request for {rejectWorkspaceName}"
            : $"Your {rejectProductName} account request";
        var rejectWorkspaceLine = rejectWorkspaceName != null
            ? $@"<p>This was for the <b>{System.Net.WebUtility.HtmlEncode(rejectWorkspaceName)}</b> workspace.</p>"
            : string.Empty;
        await SafeSendAsync(identity.Email, rejectSubject,
            $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <p>Thanks for your interest in {rejectProductName}. Unfortunately your account request for
  <b>{identity.Email}</b> was not approved at this time.</p>
  {rejectWorkspaceLine}
</div>");

        return Result<UserResponse>.Success(MapToResponse(membership));
    }

    public async Task<Result<UserResponse>> UpdateAsync(int id, UpdateUserRequest request)
    {
        var (identity, membership) = await ResolveTargetAsync(id);
        if (identity == null || membership == null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

        var updateBeforeRoleId = membership.RoleId;
        var updateBeforeIsActive = membership.IsActive;
        var passwordWasSet = !string.IsNullOrEmpty(request.Password);

        if (request.RoleId.HasValue)
        {
            var role = await GetActiveRoleAsync(request.RoleId.Value);
            if (role == null)
                return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

            // Privilege-escalation guard: only a super admin may assign an admin-tier role — except
            // Deputy, which the current Workspace Admin may delegate to their own team.
            if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin) && role.Name != DeputyRoleName)
                return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

            // Self-demotion guard: the current Workspace Admin can't change their OWN role away from
            // Workspace Admin via this endpoint — that would leave the tenant with no admin and no
            // recovery path (mirrors DeleteAsync's CannotDeleteAdmin: promote a deputy first, then
            // that new admin can change the old one's role).
            if (role.Id != membership.RoleId && identity.PublicId == _currentUser.Id)
            {
                var currentRole = await GetActiveRoleAsync(membership.RoleId);
                if (currentRole?.Name == WorkspaceAdminRoleName)
                    return Result<UserResponse>.Failure(MessageKeys.User.CannotChangeSelfFromAdmin);
            }

            // A role change alters is_admin/is_super_admin/is_quick_access baked into the JWT at
            // issue time — rotate the MEMBERSHIP stamp (R16: workspace-scoped event) so a live
            // session can't keep acting under the old role for the rest of the token's lifetime.
            if (role.Id != membership.RoleId)
                membership.SecurityStamp = Guid.NewGuid();

            membership.RoleId = role.Id;
        }

        if (request.IsActive.HasValue)
            membership.IsActive = request.IsActive.Value;

        if (!string.IsNullOrEmpty(request.Password))
        {
            // D6: an admin may only set another member's password when that identity has exactly
            // one live membership — otherwise the password is shared with workspaces this admin
            // cannot see into, so the member must change it themselves.
            var liveCount = (await _memberships.ListForIdentityAsync(identity.Id)).Count;
            if (liveCount != 1)
                return Result<UserResponse>.Failure(MessageKeys.User.PasswordManagedElsewhere);

            identity.PasswordHash = _passwordHasher.Hash(request.Password);
            // Identity-wide event (R16): rotates the IDENTITY stamp — every workspace's sessions end.
            identity.SecurityStamp = Guid.NewGuid();
            _unitOfWork.Repository<User>().Update(identity);
        }

        // H1/R16: disabling the membership must revoke THIS workspace's existing access tokens.
        if (request.IsActive == false)
            membership.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
        await _unitOfWork.SaveChangesAsync();

        var updateAfter = new Dictionary<string, string>
        {
            ["role_id"] = membership.RoleId.ToString(),
            ["is_active"] = membership.IsActive.ToString(),
        };
        if (passwordWasSet)
            updateAfter["with_password"] = "true";

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.MemberUpdated,
                AuditTargets.Membership,
                membership.Id.ToString(),
                membership.OwnerId,
                Before: new Dictionary<string, string>
                {
                    ["role_id"] = updateBeforeRoleId.ToString(),
                    ["is_active"] = updateBeforeIsActive.ToString(),
                },
                After: updateAfter
            )
        );

        var current = await GetActiveRoleAsync(membership.RoleId);
        membership.Role = current;
        return Result<UserResponse>.Success(MapToResponse(membership));
    }

    /// <summary>
    /// Ends a membership (never hard-deletes the identity — DB-11a). Authorization matrix: super
    /// admin → anyone EXCEPT whoever currently holds "Workspace Admin" (promote a deputy first, or
    /// use TenantService.HardDeleteAsync for a full teardown — this is an intentional limitation,
    /// not a gap). Workspace Admin → anyone in their own tenant except themselves. Deputy → anyone
    /// in their own tenant except themselves, the admin, or another deputy. Key/link revocation and
    /// the sole-admin guard are DB-11c.
    /// </summary>
    public async Task<Result> DeleteAsync(int id)
    {
        var (identity, membership) = await ResolveTargetAsync(id);
        if (identity == null || membership == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (identity.PublicId == _currentUser.Id)
            return Result.Failure(MessageKeys.User.CannotDeleteSelf);

        if (membership.Role.Name == WorkspaceAdminRoleName)
            return Result.Failure(MessageKeys.User.CannotDeleteAdmin);

        if (!_currentUser.IsSuperAdmin && membership.Role.Name == DeputyRoleName && _currentUser.Id is Guid callerPublicId)
        {
            var callerIdentity = await _memberships.FindIdentityByPublicIdAsync(callerPublicId);
            var callerMembership = callerIdentity != null
                ? await _memberships.GetMembershipAsync(callerIdentity.Id, membership.OwnerId)
                : null;
            if (callerMembership?.Role.Name == DeputyRoleName)
                return Result.Failure(MessageKeys.User.CannotDeleteDeputy);
        }

        var deleteBeforeRoleId = membership.RoleId;
        var deleteBeforeIsActive = membership.IsActive;

        membership.LeftAt = DateTime.UtcNow;
        membership.LeftReason = MembershipEndReason.Removed;
        membership.IsActive = false;
        membership.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.MemberRemoved,
                AuditTargets.Membership,
                membership.Id.ToString(),
                membership.OwnerId,
                Before: new Dictionary<string, string>
                {
                    ["role_id"] = deleteBeforeRoleId.ToString(),
                    ["is_active"] = deleteBeforeIsActive.ToString(),
                }
            )
        );

        return Result.Success();
    }

    /// <summary>
    /// Promotes an existing Deputy to become the tenant's new Workspace Admin, demoting the current
    /// admin to Deputy. Callable by the current admin themselves (self-service handoff) or a super
    /// admin (administrative override). Swaps RoleId on the two MEMBERSHIPS in the deputy's
    /// workspace and rotates both membership stamps (R16) — no identity-level write.
    /// </summary>
    public async Task<Result> TransferOwnershipAsync(Guid deputyPublicId)
    {
        var target = await _memberships.FindIdentityByPublicIdAsync(deputyPublicId);
        if (target == null)
            return Result.Failure(MessageKeys.User.NotADeputy);

        var deputyMemberships = await _memberships.ListForIdentityAsync(target.Id);
        var deputyMembership = deputyMemberships.FirstOrDefault(m => m.Role.Name == DeputyRoleName);
        if (deputyMembership == null)
            return Result.Failure(MessageKeys.User.NotADeputy);

        var tenantOwnerId = deputyMembership.OwnerId;
        var currentAdminMembership = await _memberships.CurrentAdminAsync(tenantOwnerId);
        if (currentAdminMembership == null)
            return Result.Failure(MessageKeys.User.WorkspaceNotFound);

        if (!_currentUser.IsSuperAdmin && _currentUser.Id != currentAdminMembership.User.PublicId)
            return Result.Failure(MessageKeys.User.TransferNotAuthorized);

        var adminRole = await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == WorkspaceAdminRoleName && r.DeletedAt == null && r.IsActive);
        var deputyRole = await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == DeputyRoleName && r.DeletedAt == null && r.IsActive);
        if (adminRole == null || deputyRole == null)
            return Result.Failure(MessageKeys.Role.Invalid);

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var trackedAdminM = await _unitOfWork.Repository<WorkspaceMembership>()
                .Query()
                .IgnoreQueryFilters()
                .FirstAsync(m => m.Id == currentAdminMembership.Id);
            var trackedDeputyM = await _unitOfWork.Repository<WorkspaceMembership>()
                .Query()
                .IgnoreQueryFilters()
                .FirstAsync(m => m.Id == deputyMembership.Id);

            var previousAdminRoleId = trackedAdminM.RoleId;
            var previousDeputyRoleId = trackedDeputyM.RoleId;

            trackedAdminM.RoleId = deputyRole.Id;
            trackedAdminM.SecurityStamp = Guid.NewGuid();
            trackedDeputyM.RoleId = adminRole.Id;
            trackedDeputyM.SecurityStamp = Guid.NewGuid();

            _unitOfWork.Repository<WorkspaceMembership>().Update(trackedAdminM);
            _unitOfWork.Repository<WorkspaceMembership>().Update(trackedDeputyM);
            await _unitOfWork.SaveChangesAsync();

            // Two rows — one per membership (§3.6).
            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.OwnershipTransferred,
                    AuditTargets.Membership,
                    trackedAdminM.Id.ToString(),
                    tenantOwnerId,
                    Before: new Dictionary<string, string> { ["role_id"] = previousAdminRoleId.ToString() },
                    After: new Dictionary<string, string> { ["role_id"] = trackedAdminM.RoleId.ToString() }
                )
            );
            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.OwnershipTransferred,
                    AuditTargets.Membership,
                    trackedDeputyM.Id.ToString(),
                    tenantOwnerId,
                    Before: new Dictionary<string, string> { ["role_id"] = previousDeputyRoleId.ToString() },
                    After: new Dictionary<string, string> { ["role_id"] = trackedDeputyM.RoleId.ToString() }
                )
            );
        });

        return Result.Success();
    }

    private async Task<Role?> GetActiveRoleAsync(int roleId) =>
        await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId && r.DeletedAt == null && r.IsActive);

    private static UserResponse MapToResponse(WorkspaceMembership m) => new()
    {
        Id = m.User.Id,
        PublicId = m.User.PublicId,
        Email = m.User.Email,
        DisplayName = m.User.DisplayName,
        RoleId = m.RoleId,
        RoleName = m.Role?.Name ?? string.Empty,
        IsAdmin = m.Role?.GrantsAdmin ?? false,
        IsActive = m.IsActive,
        CreatedAt = m.JoinedAt,
        ApprovalStatus = m.ApprovalStatus
    };
}
