using Pointer.Application.DTOs.Profile;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProfileService
{
    Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId);
    Task<Result<UserProfileResponse>> GetByIdAsync(int userId);

    /// <summary>Returns the caller's existing API key, generating one (persisted) if they don't
    /// have one yet — always the SAME key on repeat calls, never rotates on its own.</summary>
    Task<Result<ApiKeyResponse>> GetOrCreateApiKeyAsync(Guid publicId);

    /// <summary>Replaces the caller's API key with a freshly generated one, invalidating the old
    /// one immediately.</summary>
    Task<Result<ApiKeyResponse>> RegenerateApiKeyAsync(Guid publicId);
}
