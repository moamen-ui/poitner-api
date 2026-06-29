using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class ProjectService : IProjectService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public ProjectService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request)
    {
        var keyNormalized = request.Key.Trim().ToLower();

        // EF query filter already scopes this to the caller's tenant;
        // the check is still correct — a scoped admin cannot key-conflict with another tenant.
        var exists = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
            .AnyAsync();

        if (exists)
            return Result<ProjectResponse>.Conflict(MessageKeys.Project.KeyTaken);

        var project = new Project
        {
            Key = keyNormalized,
            Name = request.Name,
            IsActive = true,
            OwnerId = TenantStamp.OwnerFor(_currentUser)
        };

        await _unitOfWork.Repository<Project>().AddAsync(project);
        await _unitOfWork.SaveChangesAsync();

        return Result<ProjectResponse>.Success(MapToResponse(project));
    }

    public async Task<Result<List<ProjectResponse>>> ListAsync()
    {
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .ToListAsync();

        return Result<List<ProjectResponse>>.Success(projects.Select(MapToResponse).ToList());
    }

    public async Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request)
    {
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result<ProjectResponse>.NotFound(MessageKeys.Project.NotFound);

        if (request.Name != null)
            project.Name = request.Name;

        if (request.IsActive.HasValue)
            project.IsActive = request.IsActive.Value;

        _unitOfWork.Repository<Project>().Update(project);
        await _unitOfWork.SaveChangesAsync();

        return Result<ProjectResponse>.Success(MapToResponse(project));
    }

    public async Task<Result<int>> EnsureAsync(string key)
    {
        var keyNormalized = key.Trim().ToLower();
        var ownerId = TenantStamp.OwnerFor(_currentUser);

        // EF query filter is the primary tenant boundary; the explicit OwnerId match is
        // belt-and-suspenders to prevent a scoped admin's key resolving to a global project.
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized && p.OwnerId == ownerId)
            .Select(p => new { p.Id, p.IsActive })
            .FirstOrDefaultAsync();

        if (project == null)
        {
            // Lazy self-registration: a developer wired up VITE_POINTER_PROJECT=<key>.
            // Name defaults to the key; an admin can rename it in the dashboard.
            var created = new Project
            {
                Key = keyNormalized,
                Name = keyNormalized,
                IsActive = true,
                OwnerId = ownerId
            };
            await _unitOfWork.Repository<Project>().AddAsync(created);
            await _unitOfWork.SaveChangesAsync();
            return Result<int>.Success(created.Id);
        }

        if (!project.IsActive)
            return Result<int>.Conflict(MessageKeys.Project.Disabled);

        return Result<int>.Success(project.Id);
    }

    private static ProjectResponse MapToResponse(Project project) => new()
    {
        Id = project.Id,
        Key = project.Key,
        Name = project.Name,
        IsActive = project.IsActive
    };
}
