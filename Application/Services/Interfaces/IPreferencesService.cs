using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IPreferencesService
{
    Task<Result<MeResponse>> UpdateAsync(UpdatePreferencesRequest request);
}
