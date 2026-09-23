using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
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
    private readonly IMembershipService _memberships;

    public PreferencesService(IUnitOfWork unitOfWork, ICurrentUser currentUser, IMembershipService memberships)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _memberships = memberships;
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
            .Include(u => u.Role)
            .Where(u => u.DeletedAt == null && u.PublicId == publicId.Value)
            .FirstOrDefaultAsync();

        if (user == null) return Result<MeResponse>.NotFound(MessageKeys.Preferences.NotFound);

        if (request.Language != null) user.Language = request.Language;
        if (request.Theme != null) user.Theme = request.Theme;
        // Empty string clears the override (widget falls back to its built-in default);
        // null (property omitted) leaves the current value untouched.
        if (request.AddCommentShortcut != null)
        {
            if (request.AddCommentShortcut.Length > 40)
                return Result<MeResponse>.Failure(MessageKeys.Preferences.Invalid);
            user.AddCommentShortcut = request.AddCommentShortcut.Length == 0 ? null : request.AddCommentShortcut;
        }
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var role = user.Role;
        Workspace? workspace = null;
        if (_currentUser.TenantId is Guid tenant)
        {
            var membership = await _memberships.GetMembershipAsync(user.Id, tenant);
            role = membership?.Role ?? user.Role;
            // DB-17 review finding #11 (NIT): pass the workspace so MeResponse.DemoExpiresAt/
            // DemoCanExtend are populated here too.
            workspace = await _unitOfWork
                .Workspaces.IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == tenant);
        }

        return Result<MeResponse>.Success(UserMapper.ToMeResponse(user, role, workspace: workspace));
    }
}
