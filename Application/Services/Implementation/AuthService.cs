using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ICurrentUser _currentUser;
    private readonly ISettingsService _settings;
    private readonly IResetTokenService _resetTokens;
    private readonly IEmailService _emailService;

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ICurrentUser currentUser,
        ISettingsService settings,
        IResetTokenService resetTokens,
        IEmailService emailService)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _currentUser = currentUser;
        _settings = settings;
        _resetTokens = resetTokens;
        _emailService = emailService;
    }

    public async Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request)
    {
        var emailNormalized = (request.Email ?? string.Empty).Trim().ToLower();
        if (emailNormalized.Length > 0)
        {
            // Anonymous path → bypass the tenant query filter; only real (non-demo) active accounts.
            var user = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.DeletedAt == null && u.IsActive && !u.IsDemo && u.Email.ToLower() == emailNormalized)
                .FirstOrDefaultAsync();

            if (user != null)
            {
                var token = _resetTokens.Create(user.PublicId);
                var link = $"https://app.pointer.moamen.work/reset?token={Uri.EscapeDataString(token)}";
                try
                {
                    await _emailService.SendAsync(user.Email, "Reset your Pointer password",
                        $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Reset your password</h2>
  <p>Click the link below to choose a new password. It expires in 30 minutes.</p>
  <p><a href=""{link}"" style=""color:#2563eb"">Reset my password &rarr;</a></p>
  <p style=""color:#94a3b8;font-size:12px"">If you didn't request this, you can safely ignore this email.</p>
</div>");
                }
                catch { /* best-effort; sender logs failures */ }
            }
        }

        // Always succeed — never reveal whether an email is registered.
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");

        if (!_resetTokens.TryValidate(request.Token ?? string.Empty, out var publicId))
            return Result.Failure("This reset link is invalid or has expired.");

        var user = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Where(u => u.DeletedAt == null && u.PublicId == publicId)
            .FirstOrDefaultAsync();

        if (user == null)
            return Result.Failure("This reset link is invalid or has expired.");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        // Login is anonymous (no tenant claim yet), so the User global query filter would
        // only see OwnerId==null users — bypass it to authenticate any tenant's user by email.
        var user = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null && u.Email.ToLower() == emailNormalized)
            .FirstOrDefaultAsync();

        // Verify the password FIRST so account status is only revealed to correct credentials
        // (avoids leaking which emails exist / are pending/rejected to anonymous guessers).
        if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        if (user.ApprovalStatus == ApprovalStatus.Pending)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.PendingApproval,
                new LoginResponse { Status = "pending" });

        if (user.ApprovalStatus == ApprovalStatus.Rejected)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.Rejected,
                new LoginResponse { Status = "rejected" });

        if (!user.IsActive)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.Disabled,
                new LoginResponse { Status = "disabled" });

        var token = _tokenService.Issue(user);

        var response = new LoginResponse
        {
            Status = "ok",
            Token = token,
            User = UserMapper.ToMeResponse(user)
        };

        return Result<LoginResponse>.Success(response);
    }

    public async Task<Result> RegisterAsync(RegisterRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        // 1. Resolve the project by key to determine the tenant owner.
        //    Anonymous path → no tenant claim → global query filter hides tenant rows.
        //    We must bypass with IgnoreQueryFilters() and scope manually.
        var projectKeyNormalized = request.ProjectKey.Trim().ToLower();
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.DeletedAt == null && p.Key == projectKeyNormalized);

        if (project == null)
            return Result.Failure(MessageKeys.Project.NotFound);

        var projectOwnerId = project.OwnerId;

        // 2. Role must exist, be active, NON-admin, and belong to this tenant (or be a global role).
        //    IgnoreQueryFilters() required for the same anonymous-path reason above.
        var role = await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoleId
                                      && r.DeletedAt == null
                                      && r.IsActive
                                      && !r.GrantsAdmin
                                      && !r.IsSuperAdmin
                                      && (r.OwnerId == projectOwnerId || r.OwnerId == null));

        if (role == null)
            return Result.Failure(MessageKeys.Role.Invalid);

        // 3. Look up the existing user (if any) by normalized email, SCOPED to THIS project's tenant.
        //    A same-email user under a different tenant is not a conflict — the (email, owner_id)
        //    unique index allows it, and cross-tenant existence must not be revealed. Anonymous path
        //    → bypass the User global query filter (it would only see OwnerId==null users otherwise).
        var user = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Where(u => u.DeletedAt == null
                        && u.Email.ToLower() == emailNormalized
                        && u.OwnerId == projectOwnerId)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            // No account → create a pending, inactive user bound to the project's tenant.
            var newUser = new User
            {
                Email = emailNormalized,
                PasswordHash = _passwordHasher.Hash(request.Password),
                DisplayName = request.DisplayName,
                RoleId = role.Id,
                PublicId = Guid.NewGuid(),
                ApprovalStatus = ApprovalStatus.Pending,
                IsActive = false,
                OwnerId = projectOwnerId
            };

            await _unitOfWork.Repository<User>().AddAsync(newUser);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
        }

        if (user.ApprovalStatus == ApprovalStatus.Rejected)
        {
            // Re-apply ("Request again"): only the genuine account owner (correct password) may re-queue.
            if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
                return Result.Failure(MessageKeys.Auth.InvalidCredentials);

            user.ApprovalStatus = ApprovalStatus.Pending;
            user.RoleId = role.Id;
            user.OwnerId = projectOwnerId;

            _unitOfWork.Repository<User>().Update(user);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
        }

        // Pending or Approved → already has an account.
        return Result.Conflict(MessageKeys.Auth.AccountExists);
    }

    public async Task<Result> RegisterAdminAsync(RegisterAdminRequest request)
    {
        // Check the global toggle — if disabled, self-signup is forbidden.
        var enabled = await _settings.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, fallback: false);
        if (!enabled)
            return Result.Forbidden("Self-signup is disabled.");

        var emailNormalized = request.Email.Trim().ToLower();

        // Duplicate email check — scoped to self-owned WORKSPACE accounts (OwnerId == PublicId), the
        // tenant boundary for a new workspace. A same email existing only as a stakeholder under some
        // OTHER tenant is not a conflict here (each workspace is its own tenant; the (email, owner_id)
        // unique index permits the same address across tenants). Anonymous path → IgnoreQueryFilters().
        var existing = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null
                        && u.Email.ToLower() == emailNormalized
                        && u.OwnerId == u.PublicId)
            .FirstOrDefaultAsync();

        if (existing != null)
            return Result.Conflict("An account with that email already exists.");

        // Resolve the global "Workspace Admin" role — anonymous path, must bypass query filter.
        var role = await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.DeletedAt == null
                                      && r.Name == "Workspace Admin"
                                      && r.OwnerId == null);

        if (role == null)
            return Result.Failure("Workspace Admin role not found.");

        var publicId = Guid.NewGuid();
        var newUser = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            RoleId = role.Id,
            PublicId = publicId,
            ApprovalStatus = ApprovalStatus.Pending,
            IsActive = false,
            // Tenant owns itself while pending; super-admin activates later.
            OwnerId = publicId
        };

        await _unitOfWork.Repository<User>().AddAsync(newUser);
        await _unitOfWork.SaveChangesAsync();

        // Signup plan selector (workspace signup only). Free / none ⇒ today's flow (no subscription row;
        // effective plan resolves to Free). A paid, active, non-hidden plan ⇒ create a subscription in
        // PendingActivation; a super-admin activates it later (approval flip + IBillingProvider.Activate).
        if (request.PlanId is int planId)
        {
            var plan = await _unitOfWork.Repository<Plan>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == planId
                                          && p.DeletedAt == null
                                          && p.IsActive
                                          && p.DisplayState != PlanDisplayState.Hidden);

            // Only create a subscription for a real paid plan; Free (or an unknown/invalid id) keeps
            // the zero-write path (missing subscription ⇒ Free).
            if (plan != null && plan.Slug != "free")
            {
                await _unitOfWork.Repository<Subscription>().AddAsync(new Subscription
                {
                    OwnerId = publicId,
                    PlanId = plan.Id,
                    Status = SubscriptionStatus.PendingActivation
                });
                await _unitOfWork.SaveChangesAsync();
            }
        }

        return Result.Success("Registration submitted. Your workspace is pending approval.");
    }

    public Result<MeResponse> Me()
    {
        var publicId = _currentUser.Id;

        if (publicId == null)
            return Result<MeResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        var user = _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null && u.PublicId == publicId.Value)
            .FirstOrDefault();

        if (user == null)
            return Result<MeResponse>.NotFound(MessageKeys.User.NotFound);

        return Result<MeResponse>.Success(UserMapper.ToMeResponse(user));
    }
}
