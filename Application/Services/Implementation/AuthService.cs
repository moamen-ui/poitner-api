using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
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

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _currentUser = currentUser;
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        var user = await _unitOfWork.Repository<User>()
            .Query()
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
            User = MapToMeResponse(user)
        };

        return Result<LoginResponse>.Success(response);
    }

    public async Task<Result> RegisterAsync(RegisterRequest request)
    {
        var emailNormalized = request.Email.Trim().ToLower();

        // 1. Role must exist, be active, and be NON-admin. Self-signup can never request an admin role.
        var role = await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoleId
                                      && r.DeletedAt == null
                                      && r.IsActive
                                      && !r.GrantsAdmin);

        if (role == null)
            return Result.Failure(MessageKeys.Role.Invalid);

        // 2. Look up the existing user (if any) by normalized email.
        var user = await _unitOfWork.Repository<User>()
            .Query()
            .Where(u => u.DeletedAt == null && u.Email.ToLower() == emailNormalized)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            // No account → create a pending, inactive user.
            var newUser = new User
            {
                Email = emailNormalized,
                PasswordHash = _passwordHasher.Hash(request.Password),
                DisplayName = request.DisplayName,
                RoleId = role.Id,
                PublicId = Guid.NewGuid(),
                ApprovalStatus = ApprovalStatus.Pending,
                IsActive = false
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

            _unitOfWork.Repository<User>().Update(user);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
        }

        // Pending or Approved → already has an account.
        return Result.Conflict(MessageKeys.Auth.AccountExists);
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

        return Result<MeResponse>.Success(MapToMeResponse(user));
    }

    private static MeResponse MapToMeResponse(User user) => new()
    {
        Id = user.PublicId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        RoleId = user.RoleId,
        RoleName = user.Role?.Name ?? string.Empty,
        IsAdmin = user.Role?.GrantsAdmin ?? false,
        Language = user.Language,
        Theme = user.Theme,
    };
}
