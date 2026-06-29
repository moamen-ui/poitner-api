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

    public async Task<Result<List<PublicRoleResponse>>> ListPublicAsync(string? projectKey = null)
    {
        Guid? projectOwnerId = null;

        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            // Anonymous path — no tenant claim → must bypass EF global query filter and scope manually.
            var keyNormalized = projectKey.Trim().ToLower();
            var project = await _unitOfWork.Repository<Project>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.DeletedAt == null && p.Key == keyNormalized);

            // If project key is unknown, fall through to global-only roles (graceful degradation).
            if (project != null)
                projectOwnerId = project.OwnerId;
        }

        // IgnoreQueryFilters() is required: anonymous caller has no tenant claim, so the global
        // query filter would hide all tenant-owned rows. We scope manually below.
        var query = _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.IsActive && !r.GrantsAdmin && !r.IsSuperAdmin);

        // When a project owner was resolved, include their roles AND global roles (OwnerId == null).
        // Otherwise (no project key or unknown key) only return global roles.
        query = projectOwnerId.HasValue
            ? query.Where(r => r.OwnerId == projectOwnerId || r.OwnerId == null)
            : query.Where(r => r.OwnerId == null);

        var roles = await query
            .OrderBy(r => r.Id)
            .Select(r => new PublicRoleResponse { Id = r.Id, Name = r.Name })
            .ToListAsync();

        return Result<List<PublicRoleResponse>>.Success(roles);
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

    public async Task<Result<RoleDeleteResponse>> DeleteAsync(int id, int? reassignToRoleId)
    {
        var role = await _unitOfWork.Repository<Role>().GetByIdAsync(id);

        if (role == null || role.DeletedAt != null)
            return Result<RoleDeleteResponse>.NotFound(MessageKeys.Role.NotFound);

        // System roles (e.g. Admin) can't be deleted — protects dashboard access.
        if (role.IsSystem)
            return Result<RoleDeleteResponse>.Conflict(MessageKeys.Role.SystemImmutable);

        var users = await _unitOfWork.Repository<User>()
            .Query()
            .Where(u => u.DeletedAt == null && u.RoleId == id)
            .ToListAsync();

        var reassigned = 0;
        if (users.Count > 0)
        {
            if (reassignToRoleId == null)
                return Result<RoleDeleteResponse>.Conflict(MessageKeys.Role.HasUsers);
            if (reassignToRoleId == id)
                return Result<RoleDeleteResponse>.Conflict(MessageKeys.Role.ReassignSame);

            var target = await _unitOfWork.Repository<Role>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.Id == reassignToRoleId && r.DeletedAt == null && r.IsActive);
            if (target == null)
                return Result<RoleDeleteResponse>.Conflict(MessageKeys.Role.Invalid);

            foreach (var u in users)
            {
                u.RoleId = reassignToRoleId.Value;
                _unitOfWork.Repository<User>().Update(u);
            }
            reassigned = users.Count;
        }

        role.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Role>().Update(role);
        await _unitOfWork.SaveChangesAsync();

        return Result<RoleDeleteResponse>.Success(new RoleDeleteResponse
        {
            Id = id,
            ReassignedUsers = reassigned,
            ReassignedToRoleId = reassigned > 0 ? reassignToRoleId : null
        });
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
