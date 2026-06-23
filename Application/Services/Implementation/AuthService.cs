using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

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
            .Where(u => u.DeletedAt == null && u.IsActive && u.Email.ToLower() == emailNormalized)
            .FirstOrDefaultAsync();

        if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        var token = _tokenService.Issue(user);

        var response = new LoginResponse
        {
            Token = token,
            User = MapToMeResponse(user)
        };

        return Result<LoginResponse>.Success(response);
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
        IsAdmin = user.Role?.GrantsAdmin ?? false
    };
}
