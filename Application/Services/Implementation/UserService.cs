using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
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

    public UserService(IUnitOfWork unitOfWork, IPasswordHasher passwordHasher)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
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

        var user = new User
        {
            Email = emailNormalized,
            PasswordHash = _passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            RoleId = role.Id,
            PublicId = Guid.NewGuid(),
            IsActive = true
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

        // Admin MAY grant an admin-granting role at this step — only active/non-deleted is required.
        var role = await GetActiveRoleAsync(request.RoleId);
        if (role == null)
            return Result<UserResponse>.Failure(MessageKeys.Role.Invalid);

        user.ApprovalStatus = ApprovalStatus.Approved;
        user.IsActive = true;
        user.RoleId = role.Id;

        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

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
