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

    public UserService(IUnitOfWork unitOfWork, IPasswordHasher passwordHasher, ICurrentUser currentUser, IEmailService emailService, IEntitlementService entitlements, IBrandingService branding)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
        _emailService = emailService;
        _entitlements = entitlements;
        _branding = branding;
    }

    /// <summary>
    /// Resolves the CURRENT canonical Workspace Admin for a tenant (by role, not by
    /// OwnerId == PublicId — that only ever holds for the founding admin and breaks once ownership
    /// can change hands via TransferOwnershipAsync). Bypasses query filters since this is called by
    /// super-admin-eligible flows and by a caller checking their OWN tenant.
    /// </summary>
    private async Task<User?> GetCurrentAdminAsync(Guid ownerId) =>
        await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.OwnerId == ownerId && u.DeletedAt == null && u.Role.Name == WorkspaceAdminRoleName);

    // Best-effort notification: a send failure must never fail the admin action.
    private async Task SafeSendAsync(string to, string subject, string html)
    {
        try { await _emailService.SendAsync(to, subject, html); }
        catch { /* logged inside the sender; ignore here */ }
    }

    public async Task<Result<UserResponse>> CreateAsync(CreateUserRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        var exists = await _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null && u.Email == emailNormalized)
            .AnyAsync();

        if (exists)
            return Result<UserResponse>.Conflict(MessageKeys.User.EmailTaken);

        Role role;
        Guid? ownerId;

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
            if (await GetCurrentAdminAsync(targetOwnerId) == null)
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
            // The new user joins the CALLER's tenant — `?? _currentUser.Id` is defensive (a real,
            // non-super-admin caller should always have a TenantId; this guards a malformed-claim
            // edge case rather than silently producing a null-owner row).
            ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        }

        var publicId = Guid.NewGuid();

        // MaxSeats: count active users owned by this tenant (direct-add path). Grandfather-safe.
        if (ownerId is Guid seatOwner)
        {
            var seatCount = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .CountAsync(u => u.OwnerId == seatOwner && u.DeletedAt == null);
            var seatCheck = await _entitlements.CheckCountAsync(seatOwner, EntitlementCatalog.MaxSeats, seatCount);
            if (!seatCheck.IsSuccess)
                return Result<UserResponse>.LimitReached(seatCheck.Message ?? MessageKeys.Plan.LimitReached, seatCheck.Limit!);
        }

        var user = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            RoleId = role.Id,
            PublicId = publicId,
            IsActive = true,
            OwnerId = ownerId
        };

        await _unitOfWork.Repository<User>().AddAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return Result<UserResponse>.Success(MapToResponse(user, role));
    }

    public async Task<Result<List<UserResponse>>> ListAsync(ApprovalStatus? status = null)
    {
        var query = _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null);

        if (status.HasValue)
            query = query.Where(u => u.ApprovalStatus == status.Value);

        var users = await query
            .OrderBy(u => u.Id)
            .ToListAsync();

        return Result<List<UserResponse>>.Success(
            users.Select(u => MapToResponse(u, u.Role)).ToList());
    }

    public async Task<Result<UserResponse>> ApproveAsync(int id, ApproveUserRequest request)
    {
        var user = await _unitOfWork.Repository<User>().GetByIdAsync(id);

        if (user == null || user.DeletedAt != null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

        // Only super admin may grant an admin-tier role at approval time.
        var role = await GetActiveRoleAsync(request.RoleId);
        if (role == null)
            return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

        // Privilege-escalation guard: only a super admin may assign an admin-tier role — except
        // Deputy, which the current Workspace Admin may delegate to their own team.
        if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin) && role.Name != DeputyRoleName)
            return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

        user.ApprovalStatus = ApprovalStatus.Approved;
        user.IsActive = true;
        user.RoleId = role.Id;

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var approveBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var approveProductName = approveBrand.ProductName;
        var approveAppUrl = approveBrand.Urls.App.TrimEnd('/');
        await SafeSendAsync(user.Email, $"Your {approveProductName} account is approved",
            $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">You're in ✅</h2>
  <p>Your {approveProductName} account (<b>{user.Email}</b>) has been approved and is now active.</p>
  <p><a href=""{approveAppUrl}"" style=""color:#2563eb"">Sign in to {approveProductName} →</a></p>
</div>");

        return Result<UserResponse>.Success(MapToResponse(user, role));
    }

    public async Task<Result<UserResponse>> RejectAsync(int id)
    {
        var user = await _unitOfWork.Repository<User>().GetByIdAsync(id);

        if (user == null || user.DeletedAt != null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

        user.ApprovalStatus = ApprovalStatus.Rejected;
        user.IsActive = false;
        // H1: revoke any live access token for this now-rejected user.
        user.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var rejectBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var rejectProductName = rejectBrand.ProductName;
        await SafeSendAsync(user.Email, $"Your {rejectProductName} account request",
            $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <p>Thanks for your interest in {rejectProductName}. Unfortunately your account request for
  <b>{user.Email}</b> was not approved at this time.</p>
</div>");

        var role = await GetActiveRoleAsync(user.RoleId);
        return Result<UserResponse>.Success(MapToResponse(user, role));
    }

    public async Task<Result<UserResponse>> UpdateAsync(int id, UpdateUserRequest request)
    {
        var user = await _unitOfWork.Repository<User>().GetByIdAsync(id);

        if (user == null || user.DeletedAt != null)
            return Result<UserResponse>.NotFound(MessageKeys.User.NotFound);

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
            if (role.Id != user.RoleId && user.PublicId == _currentUser.Id)
            {
                var currentRole = await GetActiveRoleAsync(user.RoleId);
                if (currentRole?.Name == WorkspaceAdminRoleName)
                    return Result<UserResponse>.Failure(MessageKeys.User.CannotChangeSelfFromAdmin);
            }

            // A role change alters is_admin/is_super_admin/is_quick_access baked into the JWT at
            // issue time — rotate the stamp so a live session can't keep acting under the old role
            // for the rest of the token's lifetime.
            if (role.Id != user.RoleId)
                user.SecurityStamp = Guid.NewGuid();

            user.RoleId = role.Id;
        }

        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;

        if (!string.IsNullOrEmpty(request.Password))
            user.PasswordHash = _passwordHasher.Hash(request.Password);

        // H1: disabling the user or changing their password must revoke existing access tokens.
        if (request.IsActive == false || !string.IsNullOrEmpty(request.Password))
            user.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var current = await GetActiveRoleAsync(user.RoleId);
        return Result<UserResponse>.Success(MapToResponse(user, current));
    }

    /// <summary>
    /// Soft-deletes a user. Authorization matrix: super admin → anyone EXCEPT whoever currently
    /// holds "Workspace Admin" (promote a deputy first, or use TenantService.HardDeleteAsync for a
    /// full teardown — this is an intentional limitation, not a gap). Workspace Admin → anyone in
    /// their own tenant except themselves. Deputy → anyone in their own tenant except themselves,
    /// the admin, or another deputy. Tenant scoping for non-super-admin callers comes for free from
    /// the standard EF query filter — `target` below can never resolve outside their own tenant.
    /// </summary>
    public async Task<Result> DeleteAsync(int id)
    {
        var target = await _unitOfWork.Repository<User>()
            .Query()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);

        if (target == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (target.PublicId == _currentUser.Id)
            return Result.Failure(MessageKeys.User.CannotDeleteSelf);

        if (target.Role.Name == WorkspaceAdminRoleName)
            return Result.Failure(MessageKeys.User.CannotDeleteAdmin);

        if (!_currentUser.IsSuperAdmin && target.Role.Name == DeputyRoleName)
        {
            var caller = await _unitOfWork.Repository<User>()
                .Query()
                .AsNoTracking()
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.PublicId == _currentUser.Id && u.DeletedAt == null);

            if (caller?.Role.Name == DeputyRoleName)
                return Result.Failure(MessageKeys.User.CannotDeleteDeputy);
        }

        target.DeletedAt = DateTime.UtcNow;
        target.IsActive = false;
        target.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<User>().Update(target);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    /// <summary>
    /// Promotes an existing Deputy to become the tenant's new Workspace Admin, demoting the current
    /// admin to Deputy. Callable by the current admin themselves (self-service handoff) or a super
    /// admin (administrative override). No OwnerId writes anywhere — OwnerId is a stable, opaque
    /// tenant identifier; "who's the current admin" is Role.Name == "Workspace Admin" for that
    /// OwnerId, not OwnerId == PublicId (which only ever holds for the founding admin).
    /// </summary>
    public async Task<Result> TransferOwnershipAsync(Guid deputyPublicId)
    {
        var target = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.PublicId == deputyPublicId && u.DeletedAt == null);

        if (target == null || target.Role.Name != DeputyRoleName || target.OwnerId is not Guid tenantOwnerId)
            return Result.Failure(MessageKeys.User.NotADeputy);

        var currentAdmin = await GetCurrentAdminAsync(tenantOwnerId);
        if (currentAdmin == null)
            return Result.Failure(MessageKeys.User.WorkspaceNotFound);

        if (!_currentUser.IsSuperAdmin && _currentUser.Id != currentAdmin.PublicId)
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
            var trackedAdmin = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .FirstAsync(u => u.Id == currentAdmin.Id);
            var trackedTarget = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .FirstAsync(u => u.Id == target.Id);

            trackedAdmin.RoleId = deputyRole.Id;
            trackedAdmin.SecurityStamp = Guid.NewGuid();
            trackedTarget.RoleId = adminRole.Id;
            trackedTarget.SecurityStamp = Guid.NewGuid();

            _unitOfWork.Repository<User>().Update(trackedAdmin);
            _unitOfWork.Repository<User>().Update(trackedTarget);
            await _unitOfWork.SaveChangesAsync();
        });

        return Result.Success();
    }

    private async Task<Role?> GetActiveRoleAsync(int roleId) =>
        await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId && r.DeletedAt == null && r.IsActive);

    private static UserResponse MapToResponse(User user, Role? role) => new()
    {
        Id = user.Id,
        PublicId = user.PublicId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        RoleId = user.RoleId,
        RoleName = role?.Name ?? string.Empty,
        IsAdmin = role?.GrantsAdmin ?? false,
        IsActive = user.IsActive,
        CreatedAt = user.CreatedAt,
        ApprovalStatus = user.ApprovalStatus
    };
}
