using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Role;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class RoleService : IRoleService
{
    private readonly IUnitOfWork _unitOfWork;

    public RoleService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<RoleResponse>> CreateAsync(CreateRoleRequest request)
    {
        var name = request.Name.Trim();

        var exists = await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .AnyAsync(r => r.DeletedAt == null && r.Name.ToLower() == name.ToLower());

        if (exists)
            return Result<RoleResponse>.Conflict(MessageKeys.Role.NameTaken);

        var role = new Role
        {
            Name = name,
            GrantsAdmin = request.GrantsAdmin,
            IsSystem = false,
            IsActive = true
        };

        await _unitOfWork.Repository<Role>().AddAsync(role);
        await _unitOfWork.SaveChangesAsync();

        return Result<RoleResponse>.Success(MapToResponse(role));
    }

    public async Task<Result<List<RoleResponse>>> ListAsync()
    {
        var roles = await _unitOfWork.Repository<Role>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null)
            .OrderBy(r => r.Id)
            .ToListAsync();

        return Result<List<RoleResponse>>.Success(roles.Select(MapToResponse).ToList());
    }

    public async Task<Result<RoleResponse>> UpdateAsync(int id, UpdateRoleRequest request)
    {
        var role = await _unitOfWork.Repository<Role>().GetByIdAsync(id);

        if (role == null || role.DeletedAt != null)
            return Result<RoleResponse>.NotFound(MessageKeys.Role.NotFound);

        // System roles (e.g. Admin) are immutable — protects dashboard access.
        if (role.IsSystem)
            return Result<RoleResponse>.Conflict(MessageKeys.Role.SystemImmutable);

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            var clash = await _unitOfWork.Repository<Role>()
                .Query()
                .AsNoTracking()
                .AnyAsync(r =>
                    r.DeletedAt == null && r.Id != id && r.Name.ToLower() == name.ToLower());
            if (clash)
                return Result<RoleResponse>.Conflict(MessageKeys.Role.NameTaken);
            role.Name = name;
        }

        if (request.GrantsAdmin.HasValue)
            role.GrantsAdmin = request.GrantsAdmin.Value;

        if (request.IsActive.HasValue)
            role.IsActive = request.IsActive.Value;

        _unitOfWork.Repository<Role>().Update(role);
        await _unitOfWork.SaveChangesAsync();

        return Result<RoleResponse>.Success(MapToResponse(role));
    }

    private static RoleResponse MapToResponse(Role role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        GrantsAdmin = role.GrantsAdmin,
        IsSystem = role.IsSystem,
        IsActive = role.IsActive
    };
}
