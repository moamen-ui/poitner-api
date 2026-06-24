using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class PreferencesService : IPreferencesService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public PreferencesService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<MeResponse>> UpdateAsync(UpdatePreferencesRequest request)
    {
        var publicId = _currentUser.Id;
        if (publicId == null) return Result<MeResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        // Enforce allowed values here: the project registers FluentValidation validators but does
        // not auto-run them, so guard at the service layer (the path that actually executes).
        if (request.Language is not null and not ("ar" or "en"))
            return Result<MeResponse>.Failure(MessageKeys.Preferences.Invalid);
        if (request.Theme is not null and not ("light" or "dark"))
            return Result<MeResponse>.Failure(MessageKeys.Preferences.Invalid);

        var user = await _unitOfWork.Repository<User>().Query()
            .Where(u => u.DeletedAt == null && u.PublicId == publicId.Value)
            .FirstOrDefaultAsync();

        if (user == null) return Result<MeResponse>.NotFound(MessageKeys.Preferences.NotFound);

        if (request.Language != null) user.Language = request.Language;
        if (request.Theme != null) user.Theme = request.Theme;
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result<MeResponse>.Success(new MeResponse
        {
            Id = user.PublicId,
            Email = user.Email,
            DisplayName = user.DisplayName,
            RoleId = user.RoleId,
            RoleName = string.Empty,
            IsAdmin = false,
            Language = user.Language,
            Theme = user.Theme,
        });
    }
}
