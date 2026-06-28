using Pointer.Application.DTOs.Profile;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProfileService
{
    Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId);
    Task<Result<UserProfileResponse>> GetByIdAsync(int userId);
}
