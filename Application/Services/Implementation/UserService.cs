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
            .Where(u => u.DeletedAt == null && u.Email.ToLower() == emailNormalized)
            .AnyAsync();

        if (exists)
            return Result<UserResponse>.Conflict(MessageKeys.User.EmailTaken);

        var role = await GetActiveRoleAsync(request.RoleId);
        if (role == null)
            return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

        // Privilege-escalation guard: only the super admin may assign an admin-tier role.
        if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin))
            return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

        var ownerId = TenantStamp.OwnerFor(_currentUser);

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
            PublicId = Guid.NewGuid(),
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

        // Privilege-escalation guard: only the super admin may assign an admin-tier role.
        if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin))
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

            // Privilege-escalation guard: only the super admin may assign an admin-tier role.
            if (!_currentUser.IsSuperAdmin && (role.GrantsAdmin || role.IsSuperAdmin))
                return Result<UserResponse>.Failure(MessageKeys.Role.EscalationNotAllowed);

            user.RoleId = role.Id;
        }

        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;

        if (!string.IsNullOrEmpty(request.Password))
            user.PasswordHash = _passwordHasher.Hash(request.Password);

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var current = await GetActiveRoleAsync(user.RoleId);
        return Result<UserResponse>.Success(MapToResponse(user, current));
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
        ApprovalStatus = user.ApprovalStatus
    };
}
