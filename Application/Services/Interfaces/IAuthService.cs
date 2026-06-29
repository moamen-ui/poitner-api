using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request);
    Task<Result> RegisterAsync(RegisterRequest request);
    Task<Result> RegisterAdminAsync(RegisterAdminRequest request);
    Result<MeResponse> Me();
}
