using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.AppEnvironment;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class AppEnvironmentService : IAppEnvironmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public AppEnvironmentService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<List<AppEnvironmentResponse>>> ListAsync()
    {
        var query = _unitOfWork.Repository<AppEnvironment>()
            .Query()
            .AsNoTracking()
            .Where(e => e.DeletedAt == null);

        var environments = await query
            .Select(e => new AppEnvironmentResponse
            {
                Id = e.Id,
                Name = e.Name,
                IsGlobal = e.OwnerId == null,
                CanManage = _currentUser.IsSuperAdmin || e.OwnerId == _currentUser.TenantId,
                IsEnabled = e.IsEnabled,
                IsRetired = e.IsRetired,
                ProjectUrlCount = e.ProjectAppUrls.Count(u => u.DeletedAt == null)
            })
            .OrderBy(e => e.IsRetired ? 1 : 0) // retired last
            .ThenBy(e => e.IsGlobal ? 0 : 1) // global first, then the tenant's own
            .ThenBy(e => e.Name)
            .ToListAsync();

        return Result<List<AppEnvironmentResponse>>.Success(environments);
    }

    public async Task<Result<AppEnvironmentResponse>> CreateAsync(CreateAppEnvironmentRequest request)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name))
            return Result<AppEnvironmentResponse>.Failure(MessageKeys.AppEnvironment.NameRequired);

        var owner = TenantStamp.OwnerFor(_currentUser);
        var exists = await _unitOfWork.Repository<AppEnvironment>()
            .Query()
            .AsNoTracking()
            .AnyAsync(e => e.DeletedAt == null && e.OwnerId == owner && e.Name.ToLower() == name.ToLower());
        if (exists)
            return Result<AppEnvironmentResponse>.Conflict(MessageKeys.AppEnvironment.NameTaken);

        var environment = new AppEnvironment { Name = name, OwnerId = owner, IsEnabled = true };
        await _unitOfWork.Repository<AppEnvironment>().AddAsync(environment);
        await _unitOfWork.SaveChangesAsync();

        return Result<AppEnvironmentResponse>.Success(MapToResponse(environment));
    }

    public async Task<Result<AppEnvironmentResponse>> UpdateAsync(int id, UpdateAppEnvironmentRequest request)
    {
        var environment = await _unitOfWork.Repository<AppEnvironment>()
            .Query()
            .Where(e => e.Id == id && e.DeletedAt == null)
            .FirstOrDefaultAsync();
        if (environment == null)
            return Result<AppEnvironmentResponse>.NotFound(MessageKeys.AppEnvironment.NotFound);

        if (!CanManage(environment))
            return Result<AppEnvironmentResponse>.Forbidden(MessageKeys.AppEnvironment.NotManageable);

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrEmpty(name))
                return Result<AppEnvironmentResponse>.Failure(MessageKeys.AppEnvironment.NameRequired);

            var exists = await _unitOfWork.Repository<AppEnvironment>()
                .Query()
                .AsNoTracking()
                .AnyAsync(e => e.DeletedAt == null && e.Id != id && e.OwnerId == environment.OwnerId && e.Name.ToLower() == name.ToLower());
            if (exists)
                return Result<AppEnvironmentResponse>.Conflict(MessageKeys.AppEnvironment.NameTaken);

            environment.Name = name;
        }

        if (request.IsEnabled.HasValue)
        {
            environment.IsEnabled = request.IsEnabled.Value;
        }

        _unitOfWork.Repository<AppEnvironment>().Update(environment);
        await _unitOfWork.SaveChangesAsync();

        return Result<AppEnvironmentResponse>.Success(MapToResponse(environment));
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var environment = await _unitOfWork.Repository<AppEnvironment>()
            .Query()
            .Where(e => e.Id == id && e.DeletedAt == null)
            .FirstOrDefaultAsync();
        if (environment == null)
            return Result.NotFound(MessageKeys.AppEnvironment.NotFound);

        if (!CanManage(environment))
            return Result.Forbidden(MessageKeys.AppEnvironment.NotManageable);

        var inUse = await _unitOfWork.Repository<ProjectAppUrl>()
            .Query()
            .AnyAsync(u => u.AppEnvironmentId == id && u.DeletedAt == null);
        if (inUse)
            return Result.Conflict(MessageKeys.AppEnvironment.InUse);

        environment.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<AppEnvironment>().Update(environment);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    // A super admin manages everything (the global catalog and any tenant's own); a tenant manages
    // only its own — mirrors RoleService.CanManage.
    private bool CanManage(AppEnvironment environment) =>
        _currentUser.IsSuperAdmin || environment.OwnerId == _currentUser.TenantId;

    private AppEnvironmentResponse MapToResponse(AppEnvironment environment) => new()
    {
        Id = environment.Id,
        Name = environment.Name,
        IsGlobal = environment.OwnerId == null,
        CanManage = CanManage(environment),
        IsEnabled = environment.IsEnabled,
        IsRetired = environment.IsRetired,
        ProjectUrlCount = environment.ProjectAppUrls?.Count(u => u.DeletedAt == null) ?? 0
    };
}
